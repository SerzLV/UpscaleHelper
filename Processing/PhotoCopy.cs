namespace UpscaleHelper.Processing;

internal static class PhotoCopy
{
    public static async Task SaveAsync(string staged, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var folder = new TemporaryFolder(Path.GetDirectoryName(target)!);
        string pending = Path.Combine(folder.Path, "export");
        await using (var input = File.OpenRead(staged))
        await using (var output = File.Create(pending))
            await input.CopyToAsync(output, token);
        token.ThrowIfCancellationRequested();
        File.Move(pending, target);
    }
}
