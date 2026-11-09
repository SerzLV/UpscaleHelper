using UpscaleHelper.Processing;

namespace UpscaleHelper.Presentation;

internal enum JobState { Waiting, Running, Completed, Failed, Canceled }

internal sealed class QueueItem(string path, MediaKind kind) : ObservableModel
{
    private JobState state;
    private string? output;
    private string? error;
    private string progress = "";
    private double? percent;

    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public MediaKind Kind { get; } = kind;
    public string KindLabel => Kind == MediaKind.Image ? T("PhotoTag") : T("VideoTag");
    public string? Output { get => output; set => Set(ref output, value); }
    public string? Error { get => error; set => Set(ref error, value); }
    public TimeSpan? Elapsed { get; set; }
    public long? OutputSizeBytes { get; set; }
    public string Progress { get => progress; set => Set(ref progress, value); }
    public double? Percent
    {
        get => percent;
        set { if (Set(ref percent, value)) Notify(nameof(IsIndeterminate)); }
    }
    public bool IsIndeterminate => Percent is null;
    public JobState State
    {
        get => state;
        set { if (Set(ref state, value)) Notify(nameof(StateLabel)); }
    }

    public string StateLabel => State switch
    {
        JobState.Running => T("Running"),
        JobState.Completed => CompletedLabel(),
        JobState.Failed => T("Error"),
        JobState.Canceled => T("Canceled"),
        _ => T("Queued")
    };

    private string CompletedLabel()
    {
        string elapsed = Elapsed is { } time ? $" · {FormatElapsed(time)}" : "";
        string size = OutputSizeBytes is { } bytes ? $" · {FileSize.Format(bytes)}" : "";
        return $"{T("Done")}{elapsed}{size}";
    }

    public void RefreshLanguage()
    {
        Notify(nameof(KindLabel));
        Notify(nameof(StateLabel));
    }

    private static string FormatElapsed(TimeSpan time)
    {
        double seconds = Math.Round(time.TotalSeconds, 1);
        if (seconds < 60) return T("DurationSeconds", seconds);
        var rounded = TimeSpan.FromSeconds(Math.Round(seconds));
        return rounded.TotalHours >= 1
            ? $"{(long)rounded.TotalHours}:{rounded.Minutes:00}:{rounded.Seconds:00}"
            : $"{rounded.Minutes}:{rounded.Seconds:00}";
    }
}
