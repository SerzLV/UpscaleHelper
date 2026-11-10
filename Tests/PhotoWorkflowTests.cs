using System.Buffers.Binary;
using UpscaleHelper.Presentation;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Tests;

public class PhotoWorkflowTests
{
    [Fact]
    public void KeepsTenDistinctRecentPairsAndSkipsMissingFiles()
    {
        using var work = new TestDirectory();
        var history = new PhotoComparisonHistory(work.Path);
        string original = Path.Combine(work.Path, "original.png");
        File.WriteAllText(original, "original");
        var pairs = Enumerable.Range(0, 11).Select(index =>
        {
            string result = Path.Combine(work.Path, $"result-{index}.webp");
            File.WriteAllText(result, "result");
            return new FileComparison(original, result, MediaKind.Image, null);
        }).ToArray();
        foreach (var pair in pairs) history.Remember(pair);
        history.Remember(pairs[1]);

        var restored = new PhotoComparisonHistory(work.Path).Available();
        Assert.Equal(10, restored.Length);
        Assert.Equal(pairs[1], restored[0]);
        Assert.Equal(pairs[10], restored[1]);
        Assert.DoesNotContain(pairs[0], restored);
        Assert.Single(restored, pair => pair == pairs[1]);
        File.Delete(pairs[10].Enhanced);
        Assert.DoesNotContain(pairs[10], new PhotoComparisonHistory(work.Path).Available());
    }

    [FfmpegFact]
    public async Task CompressesWithoutChangingPixelsOrResolutionAndCanSaveASmallerCopy()
    {
        using var work = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var paths = new ToolPaths(work.Path);
        var runner = new ToolRunner();
        var media = new MediaTools(paths, runner);
        var compressor = new PhotoCompressor(media, paths, runner);
        string source = Path.Combine(work.Path, "source.png");
        string lossless = Path.Combine(work.Path, "exact.webp");
        string smaller = Path.Combine(work.Path, "smaller.webp");
        await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x180",
            "-frames:v", "1", "-threads", "1", "-update", "1", source], token);

        await compressor.CompressAsync(source, lossless, true, 95, token);
        await compressor.CompressAsync(source, smaller, false, 75, token);
        string reference = await PixelHash(source);
        Assert.Equal(reference, await PixelHash(lossless));
        Assert.NotEqual(reference, await PixelHash(smaller));
        var decoded = await media.ReadAsync(smaller, token);
        Assert.Equal((320, 180), (decoded.NativeWidth, decoded.NativeHeight));
        Assert.True(new FileInfo(smaller).Length < new FileInfo(lossless).Length);

        string tagged = Path.Combine(work.Path, "tagged.webp");
        byte[] exif = Convert.FromHexString("49492A0008000000000000000000");
        await WebpPhotoMetadata.WriteAsync(lossless, tagged, 320, 180, null, exif, token);
        byte[] webp = await File.ReadAllBytesAsync(tagged, token);
        Assert.Equal((uint)(webp.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4)));
        Assert.True(webp.AsSpan(12, 4).SequenceEqual("VP8X"u8));
        Assert.Equal(0x08, webp[20]);
        Assert.True(webp.AsSpan(webp.Length - exif.Length - 8, 4).SequenceEqual("EXIF"u8));
        Assert.Equal(reference, await PixelHash(tagged));

        string highDepth = Path.Combine(work.Path, "16-bit.png");
        await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-i", source, "-frames:v", "1", "-pix_fmt", "rgb48be",
            "-threads", "1", "-update", "1", highDepth], token);
        string unsupported = Path.Combine(work.Path, "16-bit.webp");
        await Assert.ThrowsAsync<InvalidDataException>(() => compressor.CompressAsync(highDepth, unsupported, true, 95, token));
        Assert.False(File.Exists(unsupported));

        async Task<string> PixelHash(string path)
        {
            using var bytes = new MemoryStream();
            await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-i", path, "-frames:v", "1", "-c:v", "rawvideo",
                "-pix_fmt", "rgba", "-threads:v", "1", "-f", "hash", "-hash", "sha256", "pipe:1"], token,
                readOutput: (stream, ct) => stream.CopyToAsync(bytes, ct));
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
