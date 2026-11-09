namespace UpscaleHelper.Infrastructure;

internal static class DiagnosticLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                string folder = Directory.CreateDirectory(Path.Combine(AppStorage.Root, "logs")).FullName;
                string path = Path.Combine(folder, "app.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= MaxBytes)
                    File.Move(path, Path.Combine(folder, "app.previous.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
