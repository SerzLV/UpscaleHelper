using System.Text.Json;

namespace UpscaleHelper.Infrastructure;

internal sealed class LocalJsonFile<T>(string directory, string name) where T : class
{
    private readonly string path = Path.Combine(directory, name);

    public T? Read()
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write($"Could not read {name} ({ex.HResult:X8}).");
        }
        return null;
    }

    public void Write(T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Could not write {name} ({ex.HResult:X8}).");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
