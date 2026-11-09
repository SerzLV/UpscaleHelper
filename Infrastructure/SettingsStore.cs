using System.Text.Json;

namespace UpscaleHelper.Infrastructure;

internal sealed record AppSettings(string? Language = null, int ModelIndex = 0, int ScaleIndex = 0,
    string? DestinationFolder = null, bool RemoveAudio = false);

internal interface ISettingsStore
{
    AppSettings Current { get; }
    void Update(Func<AppSettings, AppSettings> change);
}

internal sealed class SettingsStore : ISettingsStore
{
    private readonly string path;

    public SettingsStore(string directory)
    {
        path = Path.Combine(directory, "settings.json");
        Current = Read();
    }

    public AppSettings Current { get; private set; }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Current);
        if (next == Current) return;
        Current = next;
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException ex) { DiagnosticLog.Write($"Settings write failed ({ex.HResult:X8})."); }
        catch (UnauthorizedAccessException ex) { DiagnosticLog.Write($"Settings write failed ({ex.HResult:X8})."); }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private AppSettings Read()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new(); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException ex) { DiagnosticLog.Write($"Settings read failed ({ex.HResult:X8})."); }
        catch (UnauthorizedAccessException ex) { DiagnosticLog.Write($"Settings read failed ({ex.HResult:X8})."); }
        catch (JsonException) { DiagnosticLog.Write("Settings JSON was invalid."); }
        return new();
    }
}
