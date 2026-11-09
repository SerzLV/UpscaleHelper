using UpscaleHelper.Infrastructure;
using IOPath = System.IO.Path;

namespace UpscaleHelper.Processing;

internal sealed class TemporaryFolder : IDisposable
{
    private const string Prefix = ".upscale-";
    private static string Registry => IOPath.Combine(AppStorage.Root, "work");
    private readonly string ticket;
    private FileStream? lease;

    public TemporaryFolder(string parent, string? registry = null)
    {
        string id = Guid.NewGuid().ToString("N");
        Path = IOPath.GetFullPath(IOPath.Combine(parent, Prefix + id));
        ticket = IOPath.Combine(registry ?? Registry, id + ".work");
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(ticket)!);
            lease = new FileStream(ticket, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(lease, leaveOpen: true);
            writer.Write(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lease?.Dispose();
            lease = null;
            DiagnosticLog.Write($"Could not register temporary work ({ex.HResult:X8}).");
        }

        try { Directory.CreateDirectory(Path); }
        catch
        {
            Dispose();
            throw;
        }
        try { File.SetAttributes(Path, File.GetAttributes(Path) | FileAttributes.Hidden); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Could not hide temporary work ({ex.HResult:X8}).");
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        bool removed = TryRemoveWork(Path);
        lease?.Dispose();
        lease = null;
        if (removed) TryDeleteTicket(ticket);
    }

    internal static void Cleanup(string? registry = null, DateTimeOffset? now = null)
    {
        registry ??= Registry;
        if (!Directory.Exists(registry)) return;
        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime.AddDays(-1);
        try
        {
            foreach (string ticket in Directory.EnumerateFiles(registry, "*.work"))
            {
                string id = IOPath.GetFileNameWithoutExtension(ticket);
                if (!Guid.TryParseExact(id, "N", out _) || File.GetLastWriteTimeUtc(ticket) > cutoff) continue;
                try
                {
                    bool removed;
                    using (var handle = new FileStream(ticket, FileMode.Open, FileAccess.Read, FileShare.None))
                    using (var reader = new StreamReader(handle))
                    {
                        if (handle.Length is <= 0 or > 131_072) continue;
                        string folder = reader.ReadToEnd();
                        if (!IOPath.IsPathFullyQualified(folder)) continue;
                        folder = IOPath.GetFullPath(folder);
                        if (!string.Equals(IOPath.GetFileName(folder), Prefix + id, StringComparison.Ordinal))
                            continue;
                        removed = TryRemoveWork(folder);
                    }
                    if (removed) TryDeleteTicket(ticket);
                }
                catch (IOException) { } // An active instance can still hold this ticket.
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Temporary work cleanup failed ({ex.HResult:X8}).");
        }
    }

    private static bool TryRemoveWork(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists) return true;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            info.Delete(recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Temporary work could not be removed ({ex.HResult:X8}).");
            return false;
        }
    }

    private static void TryDeleteTicket(string ticket)
    {
        try { File.Delete(ticket); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
