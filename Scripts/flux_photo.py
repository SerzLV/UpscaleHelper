import argparse
import io
import json
import os
import sys
from pathlib import Path


PROMPT = (
    "Restore this exact photograph. Reduce blur and compression artifacts and reveal subtle, "
    "natural texture consistent with the input. Keep the same composition, framing, colors, "
    "lighting, shapes, facial identity and expression, and original background blur. "
    "Natural photographic detail."
)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--comfy", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--dependencies", type=Path)
    args = parser.parse_args()
    protocol = sys.stdout
    sys.stdout = sys.stderr

    # Load only the installed inference code. No web server or custom nodes are used.
    sys.path.insert(0, str(args.comfy))
    if args.dependencies:
        sys.path.insert(0, str(args.dependencies))
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    import comfy_aimdo.control
    comfy_aimdo.control.init()
    import comfy.memory_management
    import comfy.model_management
    import comfy.model_patcher
    import folder_paths
    import nodes
    import torch

    if not torch.cuda.is_available():
        raise RuntimeError("FLUX requires CUDA.")
    if comfy_aimdo.control.init_devices((device.index, 0) for device in comfy.model_management.get_all_torch_devices()):
        comfy.model_patcher.CoreModelPatcher = comfy.model_patcher.ModelPatcherDynamic
        comfy.memory_management.aimdo_enabled = True
    for name in ("diffusion_models", "text_encoders", "vae"):
        folder_paths.add_model_folder_path(name, str(args.models / name), is_default=True)

    with torch.inference_mode():
        model, = nodes.UNETLoader().load_unet("flux-2-klein-4b-fp8.safetensors", "default")
        clip, = nodes.CLIPLoader().load_clip("qwen_3_4b.safetensors", "flux2")
        vae, = nodes.VAELoader().load_vae("flux2-vae.safetensors")
        positive, = nodes.CLIPTextEncode().encode(clip, PROMPT)
        negative, = nodes.ConditioningZeroOut().zero_out(positive)
        del clip

    for line in sys.stdin:
        request = json.loads(line)
        print("flux:drawing", file=protocol, flush=True)
        restore(request, model, vae, (positive, negative))
        if request["ReleaseGpu"]:
            comfy.model_management.unload_all_models()
            comfy.model_management.soft_empty_cache()
        print("flux:done", file=protocol, flush=True)


def restore(request, model, vae, conditioning):
    import nodes
    import numpy as np
    import torch
    from PIL import Image, ImageCms
    from comfy_extras import nodes_custom_sampler as sampler
    from comfy_extras.nodes_edit_model import ReferenceLatent
    from comfy_extras.nodes_flux import EmptyFlux2LatentImage, Flux2Scheduler

    srgb = ImageCms.createProfile("sRGB")
    with Image.open(request["Input"]) as source:
        rgba = source.convert("RGBA")
        image = rgba.convert("RGB")
        if source.info.get("icc_profile"):
            profile = ImageCms.ImageCmsProfile(io.BytesIO(source.info["icc_profile"]))
            if profile.profile.xcolor_space.strip() == "RGB":
                image = ImageCms.profileToProfile(image, profile, srgb, outputMode="RGB")

    width, height = image.size
    pad_x, pad_y = (-width) % 32, (-height) % 32
    left, top = pad_x // 2, pad_y // 2
    padded = np.pad(np.asarray(image), ((top, pad_y - top), (left, pad_x - left), (0, 0)), mode="edge")
    with torch.inference_mode():
        pixels = torch.from_numpy(padded).unsqueeze(0).float().div_(255)
        reference, = nodes.VAEEncode().encode(vae, pixels)
        positive = ReferenceLatent.execute(conditioning[0], reference)[0]
        negative = ReferenceLatent.execute(conditioning[1], reference)[0]
        guider = sampler.CFGGuider.execute(model, positive, negative, 1.0)[0]
        noise = sampler.RandomNoise.execute(2026)[0]
        euler = sampler.KSamplerSelect.execute("euler")[0]
        sigmas = Flux2Scheduler.execute(4, width + pad_x, height + pad_y)[0]
        latent = EmptyFlux2LatentImage.execute(width + pad_x, height + pad_y)[0]
        sampled = sampler.SamplerCustomAdvanced.execute(noise, guider, euler, sigmas, latent)[0]
        decoded, = nodes.VAEDecode().decode(vae, sampled)
        raw = Image.fromarray(decoded[0].mul(255).clamp(0, 255).byte().cpu().numpy())
    if raw.size != (width + pad_x, height + pad_y):
        raise RuntimeError("FLUX returned an unexpected image size.")
    raw = raw.crop((left, top, left + width, top + height))
    corrected = preserve_appearance(image, raw, srgb)
    alpha = rgba.getchannel("A")
    profile_bytes = ImageCms.ImageCmsProfile(srgb).tobytes()
    for result, target in ((raw, request["Raw"]), (corrected, request["Corrected"])):
        if alpha.getextrema() != (255, 255):
            result.putalpha(alpha)
        result.save(target, icc_profile=profile_bytes)


def preserve_appearance(source, enhanced, srgb):
    import numpy as np
    from PIL import Image, ImageCms
    from scipy.ndimage import gaussian_filter

    lab = ImageCms.createProfile("LAB")
    source_lab = np.asarray(ImageCms.profileToProfile(source, srgb, lab, outputMode="LAB"), dtype=np.float32)
    enhanced_light = np.asarray(ImageCms.profileToProfile(enhanced, srgb, lab, outputMode="LAB"), dtype=np.float32)[:, :, 0]
    source_light = source_lab[:, :, 0]
    source_base = gaussian_filter(source_light, 2.3)
    enhanced_base = gaussian_filter(enhanced_light, 2.3)
    reliability = np.exp(-((source_base - enhanced_base) / 38.0) ** 2)
    # Keep original chroma and broad lighting. Transfer fine luminance detail from FLUX.
    source_lab[:, :, 0] += 0.85 * reliability * ((enhanced_light - enhanced_base) - (source_light - source_base))
    values = np.rint(np.clip(source_lab, 0, 255)).astype(np.uint8)
    combined = Image.frombytes("LAB", source.size, values.tobytes())
    return ImageCms.profileToProfile(combined, lab, srgb, outputMode="RGB")


if __name__ == "__main__":
    main()
