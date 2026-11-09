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
    Task<byte[]> ReadFrameAsync(ComparisonClip clip, int index, CancellationToken token);
}

internal sealed record ComparisonClip(string Path, int FrameCount, double FramesPerSecond)
{
    public TimeSpan PositionOf(int index) => TimeSpan.FromSeconds(index / FramesPerSecond);
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
        PrepareAsync(async (folder, playback) =>
        {
            string result = await processor.ProcessAsync(request with
            {
                DestinationFolder = folder,
                Segment = segment
            }, progress, token);
            progress.Report(new(T("PreparingComparison"), 99));
            await media.BuildComparisonAsync(request.Source, result, playback, segment, null, null, token);
        }, token);

    public Task<VideoPreview> CompareAsync(string original, string enhanced, VideoSegment? segment,
        IProgress<UpscaleProgress> progress, CancellationToken token) =>
        PrepareAsync((_, playback) =>
            media.BuildComparisonAsync(original, enhanced, playback, segment, segment, progress, token), token);

    private async Task<VideoPreview> PrepareAsync(Func<string, string, Task> build, CancellationToken token)
    {
        var folder = new TemporaryFolder(Path.GetTempPath());
        try
        {
            string playback = Path.Combine(folder.Path, "comparison.mp4");
            await build(folder.Path, playback);
            var info = await media.ProbeAsync(playback, token);
            if (info.FrameCount <= 0)
                throw new InvalidDataException(T("FrameCountError"));
            return new VideoPreview(folder, new(playback, info.FrameCount, info.FramesPerSecond));
        }
        catch
        {
            folder.Dispose();
            throw;
        }
    }
}
