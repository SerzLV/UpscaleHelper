import argparse
import os
import runpy
import sys
from pathlib import Path

from PIL import Image
import numpy as np
import torch
from torch.nn import functional as F


def starts(length, size, overlap):
    if length <= size:
        return [0]
    return [*range(0, length - size, size - 2 * overlap), length - size]


def weights(length, start, full_length, overlap):
    result = torch.ones(length)
    fade = torch.arange(1, overlap + 1) / (overlap + 1)
    if start > 0:
        result[:overlap] = fade
    if start + length < full_length:
        result[-overlap:] = fade.flip(0)
    return result


def automatic_mode(pixels):
    green = pixels[..., 1]
    stride = max(2, 2 * (min(green.shape) // 512))
    # Diagonal detail from adjacent 2×2 samples discounts smooth gradients.
    a = green[:-1:stride, :-1:stride].astype(np.float32)
    b = green[:-1:stride, 1::stride]
    c = green[1::stride, :-1:stride]
    d = green[1::stride, 1::stride]
    detail = (a + d - b - c) / 2
    alpha = pixels[..., 3]
    opaque = ((alpha[:-1:stride, :-1:stride] == 255) & (alpha[:-1:stride, 1::stride] == 255) &
              (alpha[1::stride, :-1:stride] == 255) & (alpha[1::stride, 1::stride] == 255))
    detail = detail[opaque]
    if detail.size < 64:
        return "focus"
    noise = np.median(np.abs(detail - np.median(detail))) / 0.67449
    return "noise" if noise >= 6 else "focus"


def restore(model, pixels, device, tile):
    _, _, height, width = pixels.shape
    padding = (0, (-width) % 8, 0, (-height) % 8)
    mode = "reflect" if padding[1] < width and padding[3] < height else "replicate"
    padded = F.pad(pixels, padding, mode=mode)
    full_height, full_width = padded.shape[-2:]
    tile_height, tile_width = min(tile, full_height), min(tile, full_width)
    overlap = 48
    rows = starts(full_height, tile_height, overlap)
    columns = starts(full_width, tile_width, overlap)
    total = len(rows) * len(columns)
    if total == 1:
        result = model(padded.to(device)).cpu()
        print("tiles 1 1", file=sys.stderr, flush=True)
        return result[:, :, :height, :width]

    output = torch.zeros_like(padded)
    coverage = torch.zeros((full_height, full_width))
    for row, y in enumerate(rows):
        vertical = weights(tile_height, y, full_height, overlap)
        for column, x in enumerate(columns):
            horizontal = weights(tile_width, x, full_width, overlap)
            region = (..., slice(y, y + tile_height), slice(x, x + tile_width))
            patch = model(padded[region].contiguous().to(device)).cpu()
            blend = vertical[:, None] * horizontal[None, :]
            output[region].add_(patch * blend)
            coverage[region].add_(blend)
            print(f"tiles {row * len(columns) + column + 1} {total}", file=sys.stderr, flush=True)
    return output[:, :, :height, :width].div_(coverage[:height, :width])


@torch.inference_mode()
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mode", choices=("auto", "focus", "motion", "noise"), required=True)
    parser.add_argument("--strength", type=int, choices=range(1, 101), required=True)
    args = parser.parse_args()

    with Image.open(args.input) as original:
        image = original.convert("RGBA")
    width, height = image.size
    rgba = np.array(image)
    mode = automatic_mode(rgba) if args.mode == "auto" else args.mode
    print(f"mode {mode}", file=sys.stderr, flush=True)
    model_file = {
        "focus": "single_image_defocus_deblurring.pth",
        "motion": "motion_deblurring.pth",
        "noise": "real_denoising.pth",
    }[mode]
    torch.set_num_threads(min(8, os.cpu_count() or 1))
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device {device.type}", file=sys.stderr, flush=True)
    model_type = runpy.run_path(str(args.runtime / "restormer_arch.py"))["Restormer"]
    model = model_type(LayerNorm_type="BiasFree" if mode == "noise" else "WithBias")
    checkpoint = torch.load(args.runtime / "weights" / model_file, map_location="cpu", weights_only=True)
    model.load_state_dict(checkpoint["params"])
    del checkpoint
    model.eval().to(device)

    pixels = torch.from_numpy(rgba[..., :3])
    pixels = pixels.permute(2, 0, 1).unsqueeze(0).to(dtype=torch.float32).div_(255)
    del rgba
    tile = 384
    if device.type == "cuda":
        memory = torch.cuda.get_device_properties(device).total_memory
        tile = 832 if memory >= 10 * 1024**3 else 512
    try:
        restored = restore(model, pixels, device, tile)
    except torch.cuda.OutOfMemoryError:
        if device.type != "cuda" or tile <= 384:
            raise
        restored = None
    if restored is None:
        torch.cuda.empty_cache()
        restored = restore(model, pixels, device, 384)
    restored.clamp_(0, 1)
    pixels.lerp_(restored, args.strength / 100)
    del restored
    pixels = pixels[0].permute(1, 2, 0).mul_(255).round_().to(torch.uint8)
    result = Image.fromarray(pixels.numpy())
    alpha = image.getchannel("A")
    if alpha.getextrema() != (255, 255):
        result.putalpha(alpha)
    result.save(args.output)


if __name__ == "__main__":
    main()
