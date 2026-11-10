import argparse
import os
import sys
from pathlib import Path

from PIL import Image
import numpy as np
import torch


def weights(path):
    checkpoint = torch.load(path, map_location="cpu", weights_only=True, mmap=True)
    state = checkpoint.get("state_dict", checkpoint)
    return {key.removeprefix("module."): value for key, value in state.items()}


@torch.no_grad()
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--strength", type=int, choices=range(1, 101), required=True)
    args = parser.parse_args()

    if not torch.cuda.is_available():
        raise RuntimeError("DiffBIR requires a CUDA-enabled PyTorch installation and an NVIDIA GPU.")
    print("device cuda", file=sys.stderr, flush=True)
    torch.set_num_threads(min(8, os.cpu_count() or 1))
    torch.manual_seed(2026)
    upstream = args.runtime / "upstream"
    sys.path.insert(0, str(upstream))
    os.environ["ATTN_MODE"] = "sdp"
    from omegaconf import OmegaConf
    from diffbir.pipeline import SwinIRPipeline
    from diffbir.utils.common import instantiate_from_config

    def build(name):
        config = upstream / "configs" / "inference" / f"{name}.yaml"
        return instantiate_from_config(OmegaConf.load(config))

    checkpoints = args.runtime / "weights"
    cleaner = build("swinir")
    cleaner.load_state_dict(weights(checkpoints / "realesrgan_s4_swinir_100k.pth"))
    cleaner.eval().to("cuda")
    cldm = build("cldm")
    _, missing = cldm.load_pretrained_sd(weights(checkpoints / "sd2.1-base-zsnr-laionaes5.ckpt"))
    # The pinned config uses the penultimate text block and never projects a pooled embedding.
    clip = "cond_stage_model.model."
    required_missing = {key for key in missing if not key.startswith(clip + "transformer.resblocks.23.")
                        and key not in {clip + "logit_scale", clip + "text_projection"}}
    if required_missing:
        raise RuntimeError(f"Stable Diffusion checkpoint is missing parameters: {', '.join(sorted(required_missing))}")
    cldm.load_controlnet_from_ckpt(weights(checkpoints / "DiffBIR_v2.1.pt"))
    cldm.cast_dtype(torch.float16)
    cldm.eval().to("cuda")
    diffusion = build("diffusion_v2.1").to("cuda")
    pipeline = SwinIRPipeline(cleaner, cldm, diffusion, None, "cuda")

    with Image.open(args.input) as image:
        original = image.convert("RGBA")
    rgb = original.convert("RGB")
    print("drawing", file=sys.stderr, flush=True)
    with torch.autocast("cuda", torch.float16):
        generated = pipeline.run(
            np.array(rgb)[None], steps=14, strength=1.0,
            cleaner_tiled=True, cleaner_tile_size=256, cleaner_tile_stride=192,
            vae_encoder_tiled=True, vae_encoder_tile_size=256,
            vae_decoder_tiled=True, vae_decoder_tile_size=256,
            cldm_tiled=True, cldm_tile_size=512, cldm_tile_stride=384,
            pos_prompt="natural photographic detail", neg_prompt="distorted shapes, artificial texture, artifacts",
            cfg_scale=3.0, start_point_type="noise", sampler_type="edm_dpm++_2m",
            noise_aug=0, rescale_cfg=False, s_churn=0, s_tmin=0, s_tmax=float("inf"),
            s_noise=1.0, eta=0, order=2,
        )[0]
    result = Image.blend(rgb, Image.fromarray(generated), args.strength / 100)
    alpha = original.getchannel("A")
    if alpha.getextrema() != (255, 255):
        result.putalpha(alpha)
    result.save(args.output)
    print(f"peak reserved GPU memory: {torch.cuda.max_memory_reserved() / 1024**3:.2f} GiB", flush=True)


if __name__ == "__main__":
    main()
