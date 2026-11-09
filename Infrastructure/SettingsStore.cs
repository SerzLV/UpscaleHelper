using UpscaleHelper.Processing;

namespace UpscaleHelper.Infrastructure;

internal sealed record AppSettings(string? Language = null, int ModelIndex = 0, int ScaleIndex = 0,
    string? DestinationFolder = null, bool RemoveAudio = false, VideoQuality VideoQuality = VideoQuality.Balanced);

internal interface ISettingsStore
{
    AppSettings Current { get; }
    void Update(Func<AppSettings, AppSettings> change);
}

internal sealed class SettingsStore : ISettingsStore
{
    private readonly LocalJsonFile<AppSettings> file;

    public SettingsStore(string directory)
    {
        file = new(directory, "settings.json");
        Current = file.Read() ?? new();
    }

    public AppSettings Current { get; private set; }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Current);
        if (next == Current) return;
        Current = next;
        file.Write(next);
    }
}
