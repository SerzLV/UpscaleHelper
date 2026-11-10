using UpscaleHelper.Infrastructure;
using UpscaleHelper.Localization;
using UpscaleHelper.Presentation;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Tests;

public class StudioViewModelTests
{
    [Fact]
    public void PhotoToolsUseTheFinishedPhotoAndOpenTheRequestedMode()
    {
        using var work = new TestDirectory();
        string source = Path.Combine(work.Path, "photo.jpg");
        string result = Path.Combine(work.Path, "photo_x2.png");
        string video = Path.Combine(work.Path, "clip.mp4");
        foreach (string path in new[] { source, result, video }) File.WriteAllText(path, "placeholder");
        var dialogs = new TestDialogs();
        var studio = new StudioViewModel(new FakeProcessor(), new EmptyPreview(), new UnusedVideoPreview(),
            dialogs, new TestLanguage(), new MemorySettings(new()), new(work.Path, "queue.json"));
        studio.AddFiles([source, video]);
        var photo = studio.Items[0];
        studio.CompressPhotoCommand.Execute(photo);
        Assert.Equal(source, dialogs.CompressionSource);

        photo.Output = result;
        photo.State = JobState.Completed;
        studio.CompressPhotoCommand.Execute(photo);
        studio.RestorePhotoCommand.Execute(photo);
        Assert.Equal(result, dialogs.CompressionSource);
        Assert.Equal((result, false), dialogs.Restoration);
        studio.DrawDetailsCommand.Execute(photo);
        Assert.Equal((result, true), dialogs.Restoration);
        studio.CompareFilesCommand.Execute(photo);
        Assert.Equal(new FileComparison(source, result, MediaKind.Image, null), dialogs.Comparison);

        var videoItem = studio.Items[1];
        Assert.False(studio.DrawDetailsCommand.CanExecute(videoItem));
        studio.RemoveCommand.Execute(videoItem);
        Assert.Same(photo, studio.Selected);
        Assert.Single(studio.Items);
        Assert.True(File.Exists(video));

        studio.DrawDetailsCommand.Execute(null);
        Assert.Equal(((string?)null, true), dialogs.Restoration);
    }

    [Fact]
    public async Task RestoresPreferencesAndCompletesAQueuedVideoWithCurrentSettings()
    {
        using var work = new TestDirectory();
        string source = Path.Combine(work.Path, "source.mp4");
        File.WriteAllText(source, "placeholder");
        var settings = new MemorySettings(new("en", 2, 1, work.Path, true));
        var processor = new FakeProcessor();
        var queue = new LocalJsonFile<QueueEntry[]>(work.Path, "queue.json");
        var dialogs = new TestDialogs();
        var studio = new StudioViewModel(processor, new EmptyPreview(), new UnusedVideoPreview(),
            dialogs, new TestLanguage(), settings, queue);
        Assert.Equal(1, studio.ScaleIndex);
        Assert.Equal(2, studio.ModelIndex);
        Assert.True(studio.RemoveAudio);
        Assert.Equal((int)VideoQuality.Balanced, studio.VideoQualityIndex);
        studio.VideoQualityIndex = (int)VideoQuality.High;
        Assert.Equal(VideoQuality.High, settings.Current.VideoQuality);
        studio.ModelIndex = 1;
        Assert.Equal(0, settings.Current.ScaleIndex);
        Assert.Equal(1, settings.Current.ModelIndex);
        studio.AddFiles([source]);
        Assert.True(studio.StartCommand.CanExecute(null));
        studio.StartCommand.Execute(null);
        var running = studio.RunningTask!;
        processor.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new UpscaleRequest(source, MediaKind.Video, 2, ContentProfile.Natural, work.Path,
            Fast: true, RemoveAudio: true, VideoQuality: VideoQuality.High), processor.Request);
        var item = Assert.Single(studio.Items);
        Assert.Equal(JobState.Completed, item.State);
        Assert.NotNull(item.Elapsed);
        Assert.Equal(3, item.OutputSizeBytes);

        var restored = Restart();
        var restoredItem = Assert.Single(restored.Items);
        Assert.Equal(JobState.Completed, restoredItem.State);
        Assert.Equal(source, restoredItem.Path);
        Assert.Equal(item.Output, restoredItem.Output);
        Assert.Equal(item.Elapsed, restoredItem.Elapsed);
        Assert.Equal(item.OutputSizeBytes, restoredItem.OutputSizeBytes);
        Assert.Equal((int)VideoQuality.High, restored.VideoQualityIndex);
        Assert.Same(restoredItem, restored.Selected);
        restored.CompareFilesCommand.Execute(restoredItem);
        Assert.Equal(new FileComparison(source, item.Output!, MediaKind.Video, null), dialogs.Comparison);

        File.Delete(restoredItem.Output!);
        var withoutOutput = Restart();
        Assert.Equal(JobState.Waiting, Assert.Single(withoutOutput.Items).State);
        Assert.Null(withoutOutput.Selected!.Output);
        withoutOutput.RemoveCommand.Execute(null);
        Assert.Empty(Restart().Items);

        StudioViewModel Restart() => new(processor, new EmptyPreview(), new UnusedVideoPreview(),
            dialogs, new TestLanguage(), settings, new(work.Path, "queue.json"));
    }

    private sealed class MemorySettings(AppSettings initial) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = initial;
        public void Update(Func<AppSettings, AppSettings> change) => Current = change(Current);
    }

    private sealed class FakeProcessor : IUpscaleProcessor
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UpscaleRequest? Request { get; private set; }
        public void Release() => gate.SetResult();

        public async Task<string> ProcessAsync(UpscaleRequest request, IProgress<UpscaleProgress> progress,
            CancellationToken cancellationToken)
        {
            await gate.Task.WaitAsync(cancellationToken);
            Request = request;
            string output = Path.Combine(request.DestinationFolder!, "out.mp4");
            File.WriteAllBytes(output, [1, 2, 3]);
            return output;
        }
    }

    private sealed class EmptyPreview : IPreviewProvider
    {
        public Task<PreviewFrame> ReadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new PreviewFrame([], 0, 0));
    }

    private sealed class TestLanguage : IInterfaceLanguage
    {
        public bool IsEnglish { get; private set; } = true;
        public bool Select(bool english) => IsEnglish = english;
    }

    private sealed class UnusedVideoPreview : IVideoPreviewBuilder
    {
        public Task<VideoPreview> BuildAsync(UpscaleRequest request, VideoSegment segment,
            IProgress<UpscaleProgress> progress, CancellationToken token) => throw new NotSupportedException();
        public Task<VideoPreview> CompareAsync(string original, string enhanced, VideoSegment? segment,
            IProgress<UpscaleProgress> progress, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class TestDialogs : IStudioDialogs
    {
        public FileComparison? Comparison { get; private set; }
        public string? CompressionSource { get; private set; }
        public (string? Source, bool DrawDetails) Restoration { get; private set; }
        public string[] PickFiles() => [];
        public string? PickFolder(string? current) => null;
        public VideoSegment? PickSegment(string name, VideoSegment? previous) => null;
        public FileComparison? PickComparisonFiles(FileComparison? initial)
        {
            Comparison = initial;
            return null;
        }
        public void ShowPhotoComparison(ComparisonViewModel comparison, FileComparison files) => Comparison = files;
        public void ShowPhotoCompression(string? source) => CompressionSource = source;
        public void ShowPhotoRestoration(string? source, bool drawDetails) => Restoration = (source, drawDetails);
        public void ShowComparison(ComparisonClip clip, string name) => throw new NotSupportedException();
        public void ShowError(string name, string error) => throw new NotSupportedException();
        public void Reveal(string path) => throw new NotSupportedException();
    }
}
