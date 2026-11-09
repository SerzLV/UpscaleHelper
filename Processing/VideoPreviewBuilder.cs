namespace UpscaleHelper.Processing;

internal interface IVideoPreviewBuilder
{
    Task<VideoPreview> BuildAsync(UpscaleRequest request, VideoSegment segment,
        IProgress<UpscaleProgress> progress, CancellationToken token);
    Task<VideoPreview> CompareAsync(string original, string enhanced, VideoSegment? segment,
        IProgress<UpscaleProgress> progress, CancellationToken token);
}

internal interface IVideoFrameProvider
{
    Task<PreviewFrame> ReadFrameAsync(ComparisonClip clip, ComparisonTrack track, int index, CancellationToken token);
}

internal sealed record ComparisonTrack(string Path, TimeSpan Start);

internal sealed record ComparisonClip(string Path, int FrameCount, string FrameRate, double FramesPerSecond,
    ComparisonTrack Original, ComparisonTrack Enhanced)
{
    public long? EstimatedExportBytes { get; init; }

    public TimeSpan PositionOf(int index) => TimeSpan.FromSeconds(index / FramesPerSecond);

    public (TimeSpan Seek, int RelativeIndex) FrameWindow(ComparisonTrack track, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        // A preroll on the same output frame grid gives the FPS filter context for VFR and interlaced sources.
        int anchor = Math.Max(0, index - (int)Math.Ceiling(2 * FramesPerSecond));
        return (track.Start + PositionOf(anchor), index - anchor);
    }
}

internal sealed class VideoPreview(TemporaryFolder folder, ComparisonClip clip) : IDisposable
{
    public ComparisonClip Clip { get; } = clip;
    public void Dispose() => folder.Dispose();
}

internal sealed class VideoPreviewBuilder(IUpscaleProcessor processor, MediaTools media) : IVideoPreviewBuilder
{
    public Task<VideoPreview> BuildAsync(UpscaleRequest request, VideoSegment segment,
        IProgress<UpscaleProgress> progress, CancellationToken token) =>
        PrepareAsync(new(request.Source, segment.Start), async (folder, playback) =>
        {
            string result = await processor.ProcessAsync(request with
            {
                DestinationFolder = folder,
                Segment = segment
            }, progress, token);
            progress.Report(new(T("PreparingComparison"), 99));
            var exported = await media.BuildComparisonAsync(request.Source, result, playback, segment, null, null, token);
            var source = await media.ProbeAsync(request.Source, token);
            long? estimate = EstimateExportSize(new FileInfo(result).Length, exported.Duration, source.Duration);
            return (new ComparisonTrack(result, TimeSpan.Zero), estimate);
        }, token);

    public Task<VideoPreview> CompareAsync(string original, string enhanced, VideoSegment? segment,
        IProgress<UpscaleProgress> progress, CancellationToken token) =>
        PrepareAsync(new(original, segment?.Start ?? TimeSpan.Zero), async (_, playback) =>
        {
            await media.BuildComparisonAsync(original, enhanced, playback, segment, segment, progress, token);
            return (new ComparisonTrack(enhanced, segment?.Start ?? TimeSpan.Zero), null);
        }, token);

    private async Task<VideoPreview> PrepareAsync(ComparisonTrack original,
        Func<string, string, Task<(ComparisonTrack Track, long? EstimatedBytes)>> build, CancellationToken token)
    {
        var folder = new TemporaryFolder(Path.GetTempPath());
        try
        {
            string playback = Path.Combine(folder.Path, "comparison.mp4");
            var (enhanced, estimate) = await build(folder.Path, playback);
            var info = await media.ProbeAsync(playback, token);
            if (info.FrameCount <= 0)
                throw new InvalidDataException(T("FrameCountError"));
            return new VideoPreview(folder, new(playback, info.FrameCount, info.FrameRate, info.FramesPerSecond, original, enhanced)
            {
                EstimatedExportBytes = estimate
            });
        }
        catch
        {
            folder.Dispose();
            throw;
        }
    }

    internal static long? EstimateExportSize(long sampleBytes, double sampleSeconds, double fullSeconds)
    {
        if (sampleBytes <= 0 || sampleSeconds <= 0 || !double.IsFinite(sampleSeconds) || fullSeconds <= 0)
            return null;
        double estimate = Math.Round(sampleBytes * Math.Max(1, fullSeconds / sampleSeconds));
        return estimate is > 0 and < long.MaxValue ? (long)estimate : null;
    }
}
