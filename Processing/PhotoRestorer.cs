using System.Globalization;

namespace UpscaleHelper.Processing;

internal enum RestorationMode { Auto, Focus, Motion, Noise, DrawDetails }
internal sealed record RestorationResult(string Path, string? RawFluxPath = null);

internal interface IPhotoRestorer
{
    bool IsInstalled { get; }
    bool IsDrawDetailsInstalled { get; }
    bool UsesFluxForDetails { get; }
    Task<RestorationResult> RestoreAsync(string source, string target, RestorationMode mode, int strength, bool upscale,
        IProgress<UpscaleProgress> progress, CancellationToken token);
}

internal sealed class PhotoRestorer(MediaTools media, ToolRunner runner, IUpscaleProcessor upscaler,
    string runtime, string worker, FluxRuntime? flux = null) : IPhotoRestorer, IDisposable
{
    private readonly string python = Path.Combine(runtime, "venv", "Scripts", "python.exe");
    private readonly string detailWorker = Path.Combine(Path.GetDirectoryName(worker)!, "draw_details.py");
    private readonly string detailRuntime = Path.Combine(runtime, "diffbir");
    private readonly FluxSession? fluxSession = flux is null ? null :
        new(flux, Path.Combine(Path.GetDirectoryName(worker)!, "flux_photo.py"), runner);

    public bool IsInstalled => File.Exists(python) && File.Exists(worker) &&
        File.Exists(Path.Combine(runtime, "restormer_arch.py")) &&
        File.Exists(Path.Combine(runtime, "runtime-v1.ready"));

    public bool UsesFluxForDetails => fluxSession?.IsAvailable == true;

    private bool HasDiffBir => IsInstalled && File.Exists(detailWorker) &&
        File.Exists(Path.Combine(detailRuntime, "runtime-v1.ready"));

    public bool IsDrawDetailsInstalled => UsesFluxForDetails || HasDiffBir;

    public async Task<RestorationResult> RestoreAsync(string source, string target, RestorationMode mode, int strength, bool upscale,
        IProgress<UpscaleProgress> progress, CancellationToken token)
    {
        bool drawDetails = mode == RestorationMode.DrawDetails;
        bool useFlux = drawDetails && UsesFluxForDetails;
        if (!useFlux && !IsInstalled) throw new InvalidOperationException(T("RestorationSetupNeeded"));
        if (SupportedMedia.GetKind(source) != MediaKind.Image) throw new InvalidDataException(T("UnsupportedFile"));
        if (drawDetails && !IsDrawDetailsInstalled)
            throw new InvalidOperationException(T("DrawDetailsSetupNeeded"));
        string task = mode switch
        {
            RestorationMode.Auto => "auto",
            RestorationMode.Focus => "focus",
            RestorationMode.Motion => "motion",
            RestorationMode.Noise => "noise",
            RestorationMode.DrawDetails => "",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        using var work = new TemporaryFolder(Path.GetDirectoryName(target)!);
        string normalized = Path.Combine(work.Path, "source.png");
        string restored = Path.Combine(work.Path, "restored.png");
        progress.Report(new(T("PreparingImage"), 0));
        await media.NormalizeImageAsync(source, normalized, token);
        var (width, height) = PngPhotoMetadata.Dimensions(normalized);
        if (width > 8192 || height > 8192 || (long)width * height > 16_000_000)
            throw new InvalidDataException(T("RestorationSizeLimit"));
        if (drawDetails && (Math.Max(width, height) > 1600 || (long)width * height > 2_000_000 ||
            Math.Max(width, height) > 4 * Math.Min(width, height)))
            throw new InvalidDataException(T("DrawDetailsSizeLimit"));
        if (useFlux && ((long)((width + 31) / 32) * ((height + 31) / 32) * 1024 > 1_048_576 ||
            Math.Max(width, height) > 1280))
        {
            if (!HasDiffBir) throw new InvalidDataException(T("FluxSizeLimit"));
            useFlux = false;
        }
        if (upscale) OutputLimits.Validate(width, height, 2, 4, MediaKind.Image);

        if (useFlux && fluxSession is { } session)
        {
            string raw = Path.Combine(work.Path, "raw-flux.png");
            await session.RunAsync(normalized, raw, restored, releaseGpu: upscale, progress, token);
            string rawOutput = Path.ChangeExtension(target, ".flux.png");
            raw = await FinalizeAsync(raw, copyColor: false);
            restored = await FinalizeAsync(restored, copyColor: false);
            token.ThrowIfCancellationRequested();
            File.Move(raw, rawOutput, overwrite: true);
            File.Move(restored, target, overwrite: true);
            return new(target, rawOutput);
        }

        string stage = T(drawDetails ? "DrawDetailsLoading" : "RestoringPhoto");
        progress.Report(new(stage, null));
        string[] modeArguments = drawDetails ? [] : ["--mode", task];
        await runner.RunAsync(python, ["-I", "-B", drawDetails ? detailWorker : worker,
            "--runtime", drawDetails ? detailRuntime : runtime, "--input", normalized,
            "--output", restored, .. modeArguments, "--strength", Math.Clamp(strength, 1, 100).ToString(CultureInfo.InvariantCulture)],
            token, onErrorLine: line =>
            {
                if (line == "drawing")
                {
                    stage = T("DrawingDetails");
                    progress.Report(new(stage, null));
                }
                else if (!drawDetails && line is ("device cuda" or "device cpu"))
                {
                    stage = T(line == "device cuda" ? "RestoringPhotoGpu" : "RestoringPhotoCpu");
                    progress.Report(new(stage, null));
                }
                else if (line.StartsWith("tiles ", StringComparison.Ordinal))
                {
                    var values = line.Split(' ');
                    if (values.Length == 3 && int.TryParse(values[1], out int completed) &&
                        int.TryParse(values[2], out int total) && total > 0)
                        progress.Report(new(stage, (upscale ? 75d : 95d) * completed / total));
                }
            });
        restored = await FinalizeAsync(restored);
        token.ThrowIfCancellationRequested();
        File.Move(restored, target, overwrite: true);
        return new(target);

        async Task<string> FinalizeAsync(string result, bool copyColor = true)
        {
            if (!File.Exists(result) || PngPhotoMetadata.Dimensions(result) != (width, height))
                throw new InvalidDataException(T("MissingImageOutput"));
            PngPhotoMetadata.Restore(normalized, result, token, copyColor);
            if (!upscale) return result;
            return await upscaler.ProcessAsync(new(result, MediaKind.Image, 2, ContentProfile.Natural, work.Path),
                new MappedProgress(progress), token);
        }
    }

    public void Dispose() => fluxSession?.Dispose();

    private sealed class MappedProgress(IProgress<UpscaleProgress> target) : IProgress<UpscaleProgress>
    {
        public void Report(UpscaleProgress update) => target.Report(update with
        {
            Percent = update.Percent is { } percent ? 75 + percent / 4 : null
        });
    }
}
