using UpscaleHelper.Infrastructure;

namespace UpscaleHelper.Processing;

internal sealed class UpscaleProcessor(MediaTools media, FrameUpscaler engine) : IUpscaleProcessor
{
    public async Task<string> ProcessAsync(UpscaleRequest request, IProgress<UpscaleProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = request.DestinationFolder ??
            Path.Combine(Path.GetDirectoryName(request.Source)!, "Upscaled");
        using var work = new TemporaryFolder(folder);
        string extension = request.Kind == MediaKind.Image ? ".png" : ".mp4";
        string staged = Path.Combine(work.Path, "result" + extension);
        var model = engine.Select(request);
        DiagnosticLog.Write($"Processing {request.Kind} at {request.Scale}x using {model.WeightName}.");
        if (request.Kind == MediaKind.Image)
            await ProcessImageAsync(request, model, work.Path, staged, progress, cancellationToken);
        else
            await ProcessVideoAsync(request, model, work.Path, staged, progress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(staged) || new FileInfo(staged).Length == 0)
            throw new IOException(T("MissingOutput"));
        string stem = $"{Path.GetFileNameWithoutExtension(request.Source)}_x{request.Scale}";
        string result = Publish(staged, folder, stem, extension);
        DiagnosticLog.Write($"Published {request.Kind} result.");
        return result;
    }

    private async Task ProcessImageAsync(UpscaleRequest request, UpscaleModel model, string work, string output,
        IProgress<UpscaleProgress> progress, CancellationToken token)
    {
        string input = Path.Combine(work, "source.png");
        bool resize = model.NativeScale > request.Scale;
        string enhanced = resize ? Path.Combine(work, "enhanced.png") : output;
        progress.Report(new(T("PreparingImage"), 5));
        await media.NormalizeImageAsync(request.Source, input, token);
        var (width, height) = PngPhotoMetadata.Dimensions(input);
        OutputLimits.Validate(width, height, request.Scale, model.NativeScale, request.Kind);
        progress.Report(new(T("GpuUpscale"), null));
        await engine.RunAsync(model, input, enhanced, token);
        if (!File.Exists(enhanced))
            throw new IOException(T("MissingImageOutput"));
        if (resize)
        {
            progress.Report(new(T("SavingPng"), 95));
            await media.ResizeImageAsync(enhanced, output, token);
        }
        PngPhotoMetadata.Restore(input, output, token);
    }

    private async Task ProcessVideoAsync(UpscaleRequest request, UpscaleModel model, string work, string output,
        IProgress<UpscaleProgress> progress, CancellationToken token)
    {
        progress.Report(new(T("ReadingVideo"), null));
        VideoInfo info = await media.ProbeAsync(request.Source, token);
        OutputLimits.Validate(info.Width, info.Height, request.Scale, model.NativeScale, request.Kind);
        double duration = info.Duration;
        if (request.Segment is { } clip)
        {
            if (clip.Start < TimeSpan.Zero || clip.Length <= TimeSpan.Zero)
                throw new InvalidDataException(T("InvalidSegment"));
            if (duration > 0 && clip.Start.TotalSeconds >= duration)
                throw new InvalidDataException(T("SegmentPastEnd"));
            duration = duration > 0 ? Math.Min(duration - clip.Start.TotalSeconds, clip.Length.TotalSeconds) :
                clip.Length.TotalSeconds;
        }
        bool resize = model.NativeScale > request.Scale;
        long frameBytes = Math.Max(1L, (long)info.Width * info.Height) * model.NativeScale * model.NativeScale * 4;
        int batchSize = (int)Math.Clamp((192L * 1024 * 1024) / frameBytes, 1, 24);
        string original = Directory.CreateDirectory(Path.Combine(work, "input")).FullName;
        string enhanced = Directory.CreateDirectory(Path.Combine(work, "output")).FullName;
        double estimate = Math.Ceiling(duration * info.FramesPerSecond);
        var timing = new FrameTiming(estimate is > 0 and <= int.MaxValue ? (int)estimate : null, progress);
        int processed = 0;

        await media.EncodeAsync(request.Source, output, info, request.Scale, resize, request.Segment,
            request.RemoveAudio, request.VideoQuality, token,
            (encoder, encodeToken) => media.DecodeAsync(request.Source, info, request.Segment, encodeToken, async (decoder, decodeToken) =>
            {
                var reader = new PngFrameReader(decoder);
                while (true)
                {
                    int count = 0;
                    while (count < batchSize && await reader.ReadAsync(FramePath(original, count), decodeToken))
                        count++;
                    if (count == 0) break;
                    if (processed == 0)
                    {
                        var (width, height) = PngPhotoMetadata.Dimensions(FramePath(original, 0));
                        OutputLimits.Validate(width, height, request.Scale, model.NativeScale, request.Kind);
                    }

                    int completed = processed;
                    timing.Report(completed);
                    await engine.RunAsync(model, original, enhanced, decodeToken, () => timing.Report(++completed));

                    for (int i = 0; i < count; i++)
                    {
                        string frame = FramePath(enhanced, i);
                        if (!File.Exists(frame))
                            throw new IOException(T("MissedFrame", processed + i + 1));
                        await using (var png = File.OpenRead(frame))
                            await png.CopyToAsync(encoder, decodeToken);
                        File.Delete(frame);
                        File.Delete(FramePath(original, i));
                    }
                    processed += count;
                    timing.Report(processed, force: true);
                }
                if (processed == 0)
                    throw new InvalidDataException(T("NoDecodedFrames"));
                timing.Finish(processed);
            }));
    }

    private static string FramePath(string folder, int index) => Path.Combine(folder, $"frame-{index:D4}.png");

    internal static string Publish(string staged, string folder, string stem, string extension)
    {
        for (int suffix = 1; ; suffix++)
        {
            string name = suffix == 1 ? stem : $"{stem} ({suffix})";
            string destination = Path.Combine(folder, name + extension);
            try
            {
                File.Move(staged, destination);
                return destination;
            }
            catch (IOException) when (File.Exists(destination)) { }
        }
    }
}
