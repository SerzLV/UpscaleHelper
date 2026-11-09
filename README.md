<h1 align="center">UpscaleHelper</h1>
<p align="center"><strong>Local photo and video upscaling for Windows</strong></p>
<p align="center">
  <a href="https://github.com/SerzLV/UpscaleHelper/releases/latest">Download for Windows</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="README.ru.md">На русском</a>
</p>

Upscale photos and videos on your GPU. Test a short clip before processing the full video, then inspect the original and the result side by side or frame by frame. Media processing stays on your PC.

![UpscaleHelper comparing a photo at 480 × 450 with its 960 × 900 result](docs/demo/comparison-en.png)

## What you can do

| Feature | What it does |
| --- | --- |
| **2× and 4× upscaling** | Choose Detail for photos and live-action footage, Fast 2× for shorter processing times, or Animation for drawn content. |
| **Photo comparison** | Use side by side, a draggable wipe, linked zoom, or a 1:1 view. See both resolutions and file sizes. |
| **Video comparison** | Try a 5- or 10-second clip and step through both panels in sync. Reopen a completed job or load an existing pair of files. |
| **Batch queue** | See frame progress and estimated time left. Completed rows show processing time and output size. |
| **Export options** | Save photos as PNG and SDR videos as H.264 MP4. Keep audio or export a silent video. |

## Quick start

UpscaleHelper requires **Windows x64**, a Vulkan-capable GPU with current drivers, the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), and [FFmpeg](https://ffmpeg.org/download.html) with `ffmpeg.exe` and `ffprobe.exe` on PATH.

1. Download and extract the Windows ZIP from [Releases](https://github.com/SerzLV/UpscaleHelper/releases).
2. Open PowerShell in the extracted folder and run `./setup-engines.ps1` once. It downloads the pinned Real-ESRGAN and Waifu2x releases and verifies their SHA-256 checksums.
3. Launch `UpscaleHelper.exe`, drop in files, choose a model and scale, and press **F5**. Results go into an `Upscaled` folder beside each source by default.

Use **Ctrl+P** to preview a selected video and **Ctrl+Shift+C** to compare files you already have. In the video comparison window, **←** and **→** step through frames. The **RU / EN** switch is in the top bar.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and FFmpeg, then run these commands from the checkout.

```powershell
./setup-engines.ps1
dotnet run --project UpscaleHelper.csproj -c Release
```

You can also place `ffmpeg.exe` and `ffprobe.exe` in `tools/ffmpeg/`. The application looks beside the executable first, then uses PATH.

## Demo and credits

Try the [original photo](docs/demo/rose-original.jpg) and [2× result](docs/demo/rose-upscaled.png) with **Compare files**. The source photo is [Rose rosa by Rillke](https://commons.wikimedia.org/wiki/File:Rose_rosa.jpg), shared under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/). The original was resized and compressed for this example, then upscaled with the Detail model.

UpscaleHelper is built with C# and WPF. Its processing backends are [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0), [Waifu2x ncnn Vulkan](https://github.com/nihui/waifu2x-ncnn-vulkan/releases/tag/20250915), and [FFmpeg](https://ffmpeg.org/). See the upstream terms for [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN/blob/master/LICENSE), [Real-ESRGAN ncnn Vulkan](https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/blob/master/LICENSE), [Waifu2x ncnn Vulkan](https://github.com/nihui/waifu2x-ncnn-vulkan/blob/20250915/LICENSE), and [FFmpeg](https://ffmpeg.org/legal.html).
