using System.Collections.ObjectModel;
using System.Diagnostics;
using UpscaleHelper.Infrastructure;
using UpscaleHelper.Localization;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

internal sealed class StudioViewModel : ObservableModel
{
    private readonly IUpscaleProcessor processor;
    private readonly IPreviewProvider previews;
    private readonly IVideoPreviewBuilder videoPreviews;
    private readonly IStudioDialogs dialogs;
    private readonly IInterfaceLanguage language;
    private readonly ISettingsStore settings;
    private readonly LocalJsonFile<QueueEntry[]> queue;
    private CancellationTokenSource? batch;
    private QueueItem? selected;
    private QueueItem? activeItem;
    private string? folder;
    private Func<string> status = static () => T("Ready");
    private double? percent = 0;
    private string metrics = "";
    private int scaleIndex;
    private int modelIndex;
    private bool removeAudio;
    private VideoQuality videoQuality;
    private bool canceling;
    private bool isComparisonRunning;
    private VideoSegment? lastSegment;

    public StudioViewModel(IUpscaleProcessor processor, IPreviewProvider previews,
        IVideoPreviewBuilder videoPreviews, IStudioDialogs dialogs, IInterfaceLanguage language, ISettingsStore settings,
        LocalJsonFile<QueueEntry[]> queue)
    {
        this.processor = processor;
        this.previews = previews;
        this.videoPreviews = videoPreviews;
        this.dialogs = dialogs;
        this.language = language;
        this.settings = settings;
        this.queue = queue;
        var saved = settings.Current;
        modelIndex = Math.Clamp(saved.ModelIndex, 0, 2);
        scaleIndex = saved.ScaleIndex == 1 && modelIndex != 1 ? 1 : 0;
        folder = Path.IsPathFullyQualified(saved.DestinationFolder ?? "") ? saved.DestinationFolder : null;
        removeAudio = saved.RemoveAudio;
        videoQuality = Enum.IsDefined(saved.VideoQuality) ? saved.VideoQuality : VideoQuality.Balanced;
        Comparison = new ComparisonViewModel(previews);
        AddCommand = new(() => AddFiles(dialogs.PickFiles()), () => CanEdit);
        CompareFilesCommand = new(async parameter => await CompareFilesAsync(parameter as QueueItem), _ => CanEdit);
        CompressPhotoCommand = new(parameter => OpenPhoto(parameter, dialogs.ShowPhotoCompression), CanOpenPhoto);
        RestorePhotoCommand = new(parameter => OpenPhoto(parameter, source => dialogs.ShowPhotoRestoration(source, drawDetails: false)), CanOpenPhoto);
        DrawDetailsCommand = new(parameter => OpenPhoto(parameter, source => dialogs.ShowPhotoRestoration(source, drawDetails: true)), CanOpenPhoto);
        RemoveCommand = new(parameter => RemoveFile(parameter as QueueItem ?? Selected),
            parameter => CanEdit && (parameter is QueueItem || Selected is not null));
        FolderCommand = new(ChooseFolder, () => CanEdit);
        ResetFolderCommand = new(() => Folder = null, () => CanEdit && Folder is not null);
        RevealCommand = new(parameter =>
        {
            if (parameter is QueueItem { Output: { } path }) Reveal(path);
        });
        DetailsCommand = new(parameter =>
        {
            if (parameter is QueueItem { Error: { } error } item) dialogs.ShowError(item.Name, error);
        });
        RetryCommand = new(async parameter =>
        {
            if (!CanEdit || parameter is not QueueItem { State: JobState.Failed or JobState.Canceled } item) return;
            Selected = item;
            await RunAsync(token => RunQueueAsync(token, item));
        }, _ => CanEdit);
        StartCommand = new(async () => await RunAsync(token => RunQueueAsync(token)),
            () => CanEdit && (Selected is not null || Items.Any(item => item.State != JobState.Completed)));
        PreviewCommand = new(async () => await PreviewVideoAsync(), () => CanEdit && CanPreviewVideo);
        CancelCommand = new(Cancel, () => IsBusy && !canceling);
        RestoreQueue();
    }

    public ObservableCollection<QueueItem> Items { get; } = [];
    public ComparisonViewModel Comparison { get; }
    public StudioCommand AddCommand { get; }
    public StudioCommand CompareFilesCommand { get; }
    public StudioCommand CompressPhotoCommand { get; }
    public StudioCommand RestorePhotoCommand { get; }
    public StudioCommand DrawDetailsCommand { get; }
    public StudioCommand RemoveCommand { get; }
    public StudioCommand FolderCommand { get; }
    public StudioCommand ResetFolderCommand { get; }
    public StudioCommand RevealCommand { get; }
    public StudioCommand DetailsCommand { get; }
    public StudioCommand RetryCommand { get; }
    public StudioCommand StartCommand { get; }
    public StudioCommand CancelCommand { get; }
    public StudioCommand PreviewCommand { get; }
    public bool IsBusy => batch is not null;
    public bool CanEdit => !IsBusy;
    public bool HasItems => Items.Count > 0;
    public bool HasVideos => Items.Any(item => item.Kind == MediaKind.Video);
    public bool RemoveAudio
    {
        get => removeAudio;
        set { if (Set(ref removeAudio, value)) SaveSettings(); }
    }
    public int VideoQualityIndex
    {
        get => (int)videoQuality;
        set
        {
            if (Enum.IsDefined((VideoQuality)value) && Set(ref videoQuality, (VideoQuality)value)) SaveSettings();
        }
    }
    public string StartLabel => HasItems && !IsBusy && Items.All(item => item.State == JobState.Completed)
        ? T("RepeatSelected") : T("StartProcessing");
    public string ProgressLabel => Percent is { } value ? $"{value:0}%" : "—";
    public bool IsIndeterminate => Percent is null;
    public bool IsComparisonRunning { get => isComparisonRunning; private set => Set(ref isComparisonRunning, value); }
    public Task? RunningTask { get; private set; }
    public QueueItem? ActiveItem { get => activeItem; private set => Set(ref activeItem, value); }

    public int LanguageIndex
    {
        get => language.IsEnglish ? 1 : 0;
        set
        {
            if (!CanEdit || value is not (0 or 1) || !language.Select(value == 1)) return;
            Notify();
            Notify(nameof(StartLabel));
            Notify(nameof(FolderLabel));
            Notify(nameof(ModelHint));
            Notify(nameof(Status));
            Comparison.RefreshLanguage();
            foreach (var item in Items) item.RefreshLanguage();
        }
    }

    public int ScaleIndex
    {
        get => scaleIndex;
        set { if (Set(ref scaleIndex, value == 1 && CanUseFourTimes ? 1 : 0)) SaveSettings(); }
    }
    public int ModelIndex
    {
        get => modelIndex;
        set
        {
            if (value is < 0 or > 2 || !Set(ref modelIndex, value)) return;
            if (!CanUseFourTimes) Set(ref scaleIndex, 0, nameof(ScaleIndex));
            Notify(nameof(ModelHint));
            Notify(nameof(CanUseFourTimes));
            SaveSettings();
        }
    }

    public bool CanUseFourTimes => ModelIndex != 1;
    public string ModelHint => ModelIndex switch
    {
        1 => T("FastHint"),
        2 => T("AnimationHint"),
        _ => T("DetailHint")
    };

    public string? Folder
    {
        get => folder;
        private set
        {
            if (!Set(ref folder, value)) return;
            Notify(nameof(FolderLabel));
            ResetFolderCommand.Refresh();
            SaveSettings();
        }
    }

    public string FolderLabel => Folder ?? T("DefaultOutputFolder");
    private void SaveSettings() => settings.Update(value => value with
    {
        ModelIndex = modelIndex,
        ScaleIndex = scaleIndex,
        DestinationFolder = folder,
        RemoveAudio = removeAudio,
        VideoQuality = videoQuality
    });
    public string Status => status();
    public string Metrics { get => metrics; private set => Set(ref metrics, value); }
    public double? Percent
    {
        get => percent;
        private set
        {
            if (!Set(ref percent, value)) return;
            Notify(nameof(ProgressLabel));
            Notify(nameof(IsIndeterminate));
        }
    }

    public QueueItem? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            RemoveCommand.Refresh();
            StartCommand.Refresh();
            PreviewCommand.Refresh();
            lastSegment = null;
            Notify(nameof(CanPreviewVideo));
            _ = Comparison.LoadAsync(value?.Path, value?.Output);
        }
    }

    public bool CanPreviewVideo => Selected?.Kind == MediaKind.Video;

    public void AddFiles(IEnumerable<string> paths)
    {
        if (!CanEdit) return;
        QueueItem? firstAdded = null;
        int skipped = 0;
        foreach (string path in paths)
        {
            if (SupportedMedia.GetKind(path) is not { } kind || !File.Exists(path))
            {
                skipped++;
                continue;
            }
            if (Items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            var entry = new QueueItem(path, kind);
            Items.Add(entry);
            firstAdded ??= entry;
        }
        if (firstAdded is not null) Selected = firstAdded;
        if (skipped > 0) SetStatus("SkippedFiles", skipped);
        else if (firstAdded is not null) SetStatus("FilesReady");
        if (firstAdded is not null)
        {
            SaveQueue();
            Percent = 0;
            Metrics = "";
        }
        RefreshQueue();
    }

    public void Cancel()
    {
        if (batch is null || canceling) return;
        canceling = true;
        SetStatus("Canceling");
        CancelCommand.Refresh();
        batch.Cancel();
    }

    public void StopPreview() => Comparison.Cancel();

    private bool CanOpenPhoto(object? parameter) => CanEdit && (parameter is null or QueueItem { Kind: MediaKind.Image });

    private void OpenPhoto(object? parameter, Action<string?> open)
    {
        if (parameter is null)
        {
            open(null);
            return;
        }
        if (parameter is not QueueItem { Kind: MediaKind.Image } item) return;
        string? output = item.State == JobState.Completed ? item.Output : null;
        string source = output ?? item.Path;
        if (File.Exists(source)) open(source);
        else SetStatus(output is null ? "MissingSource" : "MissingResult");
    }

    private void RemoveFile(QueueItem? item)
    {
        if (item is null) return;
        int index = Items.IndexOf(item);
        if (index < 0) return;
        bool wasSelected = Selected == item;
        Items.RemoveAt(index);
        SaveQueue();
        if (wasSelected) Selected = Items.Count > 0 ? Items[Math.Min(index, Items.Count - 1)] : null;
        if (!HasItems)
        {
            SetStatus("Ready");
            Percent = 0;
            Metrics = "";
        }
        RefreshQueue();
    }

    private void ChooseFolder()
    {
        if (dialogs.PickFolder(Folder) is { } picked) Folder = picked;
    }

    private void Reveal(string output)
    {
        try { dialogs.Reveal(output); }
        catch (Exception ex) { SetLiteralStatus(ex.Message); }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        using var tokenSource = new CancellationTokenSource();
        batch = tokenSource;
        SetBusy();
        RunningTask = operation(tokenSource.Token);
        try { await RunningTask; }
        finally
        {
            batch = null;
            canceling = false;
            RunningTask = null;
            ActiveItem = null;
            SetBusy();
        }
    }

    private async Task RunQueueAsync(CancellationToken token, QueueItem? retry = null)
    {
        QueueItem[] pending = retry is null ? Items.Where(item => item.State != JobState.Completed).ToArray() : [retry];
        if (pending.Length == 0 && Selected is { } current) pending = [current];
        int completed = 0;
        int failed = 0;
        foreach (var item in pending)
        {
            if (token.IsCancellationRequested) break;
            item.Error = null;
            item.Progress = T("Preparing");
            item.Percent = null;
            item.State = JobState.Running;
            ActiveItem = item;
            var request = RequestFor(item);
            SetStatus("RunningFile", item.Name);
            var progress = new Progress<UpscaleProgress>(update =>
            {
                if (item.State != JobState.Running || token.IsCancellationRequested) return;
                item.Progress = update.Frames is { } frames ? FormatFrames(frames) : update.Stage;
                item.Percent = update.Percent;
            });
            try
            {
                if (!File.Exists(item.Path)) throw new FileNotFoundException(T("MissingSource"));
                long started = Stopwatch.GetTimestamp();
                string output = await Task.Run(() => processor.ProcessAsync(request, progress, token), token);
                item.Elapsed = Stopwatch.GetElapsedTime(started);
                item.Output = output;
                item.OutputSizeBytes = FileSize.TryRead(output);
                item.State = JobState.Completed;
                completed++;
                if (Selected == item)
                {
                    _ = Comparison.LoadAsync(item.Path, output, resetZoom: false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                item.State = JobState.Canceled;
                break;
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                item.State = JobState.Failed;
                failed++;
            }
            finally { SaveQueue(); }
        }
        if (token.IsCancellationRequested) SetStatus("QueueStopped", completed, pending.Length);
        else if (failed > 0) SetStatus("QueueErrors", completed, failed);
        else SetStatus("QueueCompleted", completed);
    }

    private async Task PreviewVideoAsync()
    {
        if (Selected is not { Kind: MediaKind.Video } item) return;
        if (dialogs.PickSegment(item.Name, lastSegment) is not { } segment) return;
        lastSegment = segment;
        var request = RequestFor(item);
        await PrepareComparisonAsync(item.Name, T("PreviewClip"),
            (progress, token) => videoPreviews.BuildAsync(request, segment, progress, token));
    }

    private async Task CompareFilesAsync(QueueItem? item)
    {
        if (item is { State: JobState.Completed, Output: { } output })
        {
            if (!File.Exists(item.Path) || !File.Exists(output))
            {
                SetStatus("ComparisonFileMissing");
                return;
            }
            var pair = new FileComparison(item.Path, output, item.Kind, null);
            if (item.Kind == MediaKind.Image)
            {
                dialogs.ShowPhotoComparison(new ComparisonViewModel(previews), pair);
                return;
            }
            if (dialogs.PickComparisonFiles(pair) is { } selected) await ComparePairAsync(selected);
        }
        else if (item is null && dialogs.PickComparisonFiles(null) is { } files) await ComparePairAsync(files);
    }

    private async Task ComparePairAsync(FileComparison files)
    {
        if (files.Kind == MediaKind.Image)
        {
            dialogs.ShowPhotoComparison(new ComparisonViewModel(previews), files);
            return;
        }
        await PrepareComparisonAsync(files.Caption, T("ReadyVideoComparison"),
            (progress, token) => videoPreviews.CompareAsync(files.Original, files.Enhanced, files.Segment, progress, token));
    }

    private Task PrepareComparisonAsync(string name, string stage,
        Func<IProgress<UpscaleProgress>, CancellationToken, Task<VideoPreview>> build) =>
        RunAsync(async token =>
        {
            IsComparisonRunning = true;
            SetLiteralStatus($"{stage} · {name}");
            Percent = null;
            Metrics = "";
            bool building = true;
            var progress = new Progress<UpscaleProgress>(update =>
            {
                if (!building || token.IsCancellationRequested) return;
                SetLiteralStatus($"{update.Stage}  /  {name}");
                Percent = update.Percent;
                if (update.Frames is { } frames) Metrics = FormatFrames(frames);
            });
            try
            {
                using var video = await Task.Run(() => build(progress, token), token);
                token.ThrowIfCancellationRequested();
                building = false;
                SetStatus("ComparisonReady");
                Percent = 100;
                dialogs.ShowComparison(video.Clip, name);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SetStatus("ComparisonCanceled");
                Percent = 0;
            }
            catch (Exception ex)
            {
                SetStatus("ComparisonFailed", ex.Message);
                Percent = 0;
            }
            finally
            {
                building = false;
                IsComparisonRunning = false;
                Metrics = "";
            }
        });

    private UpscaleRequest RequestFor(QueueItem item) => new(item.Path, item.Kind, ScaleIndex == 1 ? 4 : 2,
        ModelIndex == 2 ? ContentProfile.Animation : ContentProfile.Natural, Folder, ModelIndex == 1,
        RemoveAudio: item.Kind == MediaKind.Video && RemoveAudio, VideoQuality: videoQuality);

    private void RestoreQueue()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in queue.Read() ?? [])
        {
            if (entry?.Path is not { } path || !Path.IsPathFullyQualified(path) ||
                SupportedMedia.GetKind(path) is not { } kind || !paths.Add(path)) continue;
            string? output = entry.Output;
            long? size = output is not null && Path.IsPathFullyQualified(output) ? FileSize.TryRead(output) : null;
            bool hasOutput = size is > 0;
            Items.Add(new QueueItem(path, kind)
            {
                Output = hasOutput ? output : null,
                OutputSizeBytes = hasOutput ? size : null,
                Elapsed = hasOutput ? entry.Elapsed : null,
                Error = entry.State == JobState.Failed ? entry.Error : null,
                State = entry.State switch
                {
                    JobState.Completed when hasOutput => JobState.Completed,
                    JobState.Failed => JobState.Failed,
                    JobState.Canceled => JobState.Canceled,
                    _ => JobState.Waiting
                }
            });
        }
        Selected = Items.FirstOrDefault();
        RefreshQueue();
    }

    private void SaveQueue() => queue.Write(Items.Select(item =>
        new QueueEntry(item.Path, item.State, item.Output, item.Elapsed, item.Error)).ToArray());

    private static string FormatFrames(FrameProgress frames)
    {
        string total = frames.EstimatedTotal is { } count
            ? $" / {(count == frames.Done ? "" : "≈")}{count.ToString("N0", Culture)}" : "";
        string rate = frames.FramesPerSecond is { } fps ? T("Rate", fps) : "";
        string eta = frames.Remaining is { } remaining ? T("Remaining", FormatTime(remaining)) : "";
        return T("FrameMetrics", frames.Done, total, rate, eta);
    }

    private void SetStatus(string key, params object[] args)
    {
        status = () => T(key, args);
        Notify(nameof(Status));
    }

    private void SetLiteralStatus(string message)
    {
        status = () => message;
        Notify(nameof(Status));
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    private void RefreshQueue()
    {
        Notify(nameof(HasItems));
        Notify(nameof(HasVideos));
        Notify(nameof(StartLabel));
        StartCommand.Refresh();
    }

    private void SetBusy()
    {
        Notify(nameof(IsBusy));
        Notify(nameof(CanEdit));
        Notify(nameof(StartLabel));
        AddCommand.Refresh();
        CompareFilesCommand.Refresh();
        CompressPhotoCommand.Refresh();
        RestorePhotoCommand.Refresh();
        DrawDetailsCommand.Refresh();
        RemoveCommand.Refresh();
        FolderCommand.Refresh();
        ResetFolderCommand.Refresh();
        StartCommand.Refresh();
        CancelCommand.Refresh();
        PreviewCommand.Refresh();
        RetryCommand.Refresh();
    }
}
