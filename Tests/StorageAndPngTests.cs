using UpscaleHelper.Infrastructure;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Tests;

public class StorageAndPngTests
{
    // Minimal stream fixture for the frame reader. PNG CRC verification belongs to the decoder.
    private static readonly byte[] Png = Convert.FromHexString("89504E470D0A1A0A0000000049454E4400000000");

    [Fact]
    public async Task ReadsTwoConsecutivePngFramesThenEof()
    {
        using var work = new TestDirectory();
        using var stream = new MemoryStream([.. Png, .. Png]);
        var reader = new PngFrameReader(stream);
        string first = Path.Combine(work.Path, "first.png");
        string second = Path.Combine(work.Path, "second.png");
        Assert.True(await reader.ReadAsync(first, CancellationToken.None));
        Assert.True(await reader.ReadAsync(second, CancellationToken.None));
        Assert.False(await reader.ReadAsync(second, CancellationToken.None));
        Assert.Equal(Png, File.ReadAllBytes(first));
        Assert.Equal(Png, File.ReadAllBytes(second));
    }

    [Fact]
    public async Task RejectsBadSignatureAndTruncatedChunk()
    {
        using var work = new TestDirectory();
        string path = Path.Combine(work.Path, "frame.png");
        using var invalid = new MemoryStream(new byte[8]);
        using var truncated = new MemoryStream(Png[..^2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PngFrameReader(invalid).ReadAsync(path, CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() => new PngFrameReader(truncated).ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public void PublicationKeepsExistingFiles()
    {
        using var work = new TestDirectory();
        string staged = Path.Combine(work.Path, "staged.png");
        string existing = Path.Combine(work.Path, "photo_x2.png");
        File.WriteAllText(staged, "new");
        File.WriteAllText(existing, "old");
        string result = UpscaleProcessor.Publish(staged, work.Path, "photo_x2", ".png");
        Assert.Equal("photo_x2 (2).png", Path.GetFileName(result));
        Assert.Equal("old", File.ReadAllText(existing));
        Assert.Equal("new", File.ReadAllText(result));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void SavesAndRestoresPreferences()
    {
        using var work = new TestDirectory();
        var store = new SettingsStore(work.Path);
        store.Update(_ => new("en", 2, 1, work.Path, true));
        Assert.Equal(new("en", 2, 1, work.Path, true), new SettingsStore(work.Path).Current);
        Assert.Single(Directory.EnumerateFiles(work.Path));
    }

    [Fact]
    public void CleanupSkipsLiveAndUnregisteredFolders()
    {
        using var work = new TestDirectory();
        string registry = Path.Combine(work.Path, "registry");
        using var live = new TemporaryFolder(work.Path, registry);
        Assert.True(File.GetAttributes(live.Path).HasFlag(FileAttributes.Hidden));
        string id = Guid.NewGuid().ToString("N");
        string orphan = Directory.CreateDirectory(Path.Combine(work.Path, ".upscale-" + id)).FullName;
        File.WriteAllText(Path.Combine(registry, id + ".work"), orphan);
        string other = Directory.CreateDirectory(Path.Combine(work.Path, ".upscale-" + Guid.NewGuid().ToString("N"))).FullName;
        TemporaryFolder.Cleanup(registry, DateTimeOffset.UtcNow.AddDays(2));
        Assert.True(Directory.Exists(live.Path));
        Assert.True(Directory.Exists(other));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void ExifOrientationIsResetAndThumbnailReferenceIsRemoved()
    {
        byte[] tiff = Convert.FromHexString("49492A000800000001001201030001000000060000001E000000");
        Assert.True(PngPhotoMetadata.NormalizeExif(tiff, 900, 960));
        Assert.Equal(1, tiff[18]);
        Assert.Equal(new byte[4], tiff[^4..]);
        Assert.False(PngPhotoMetadata.NormalizeExif(tiff[..12], 900, 960));
    }
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("UpscaleHelperTests-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
