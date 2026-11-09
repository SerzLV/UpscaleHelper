using UpscaleHelper.Infrastructure;
using UpscaleHelper.Localization;
using UpscaleHelper.Presentation;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Tests;

public class StudioViewModelTests
{
    [Fact]
    public async Task RestoresPreferencesAndCompletesAQueuedVideoWithCurrentSettings()
    {
        using var work = new TestDirectory();
        string source = Path.Combine(work.Path, "source.mp4");
        File.WriteAllText(source, "placeholder");
        var settings = new MemorySettings(new("en", 2, 1, work.Path, true));
        var processor = new FakeProcessor();
        var studio = new StudioViewModel(processor, new EmptyPreview(), new UnusedVideoPreview(),
            new UnusedDialogs(), new TestLanguage(), settings);
        Assert.Equal(1, studio.ScaleIndex);
        Assert.Equal(2, studio.ModelIndex);
        Assert.True(studio.RemoveAudio);
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
            Fast: true, RemoveAudio: true), processor.Request);
        var item = Assert.Single(studio.Items);
        Assert.Equal(JobState.Completed, item.State);
        Assert.NotNull(item.Elapsed);
        Assert.Equal(3, item.OutputSizeBytes);
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

    private sealed class UnusedDialogs : IStudioDialogs
    {
        public string[] PickFiles() => [];
        public string? PickFolder(string? current) => null;
        public VideoSegment? PickSegment(string name, VideoSegment? previous) => null;
        public FileComparison? PickComparisonFiles(FileComparison? initial) => null;
        public void ShowPhotoComparison(ComparisonViewModel comparison, FileComparison files) => throw new NotSupportedException();
        public void ShowComparison(ComparisonClip clip, string name) => throw new NotSupportedException();
        public void ShowError(string name, string error) => throw new NotSupportedException();
        public void Reveal(string path) => throw new NotSupportedException();
    }
}
