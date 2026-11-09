namespace UpscaleHelper.Processing;

internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder(string parent) =>
        Path = Directory.CreateDirectory(System.IO.Path.Combine(parent, $".upscale-{Guid.NewGuid():N}")).FullName;

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
