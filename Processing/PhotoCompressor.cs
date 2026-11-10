using System.Globalization;

namespace UpscaleHelper.Processing;

internal interface IPhotoCompressor
{
    Task CompressAsync(string source, string target, bool lossless, int quality, CancellationToken token);
}

internal sealed class PhotoCompressor(MediaTools media, ToolPaths paths, ToolRunner runner) : IPhotoCompressor
{
    public async Task CompressAsync(string source, string target, bool lossless, int quality, CancellationToken token)
    {
        if (SupportedMedia.GetKind(source) != MediaKind.Image) throw new InvalidDataException(T("UnsupportedFile"));
        using var work = new TemporaryFolder(Path.GetDirectoryName(target)!);
        string normalized = Path.Combine(work.Path, "normalized.png");
        string encoded = Path.Combine(work.Path, "encoded.webp");
        await media.NormalizeImageAsync(source, normalized, token, preserveBitDepth: true);
        var (width, height) = PngPhotoMetadata.Dimensions(normalized);
        if (width > 16383 || height > 16383 || (long)width * height > 40_000_000)
            throw new InvalidDataException(T("CompressionSizeLimit"));
        if (lossless && PngPhotoMetadata.BitDepth(normalized) > 8)
            throw new InvalidDataException(T("CompressionBitDepthLimit"));

        var (icc, exif) = PngPhotoMetadata.ReadWebpMetadata(normalized, width, height, token);
        await runner.RunAsync(paths.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", normalized,
            "-map", "0:V:0", "-frames:v", "1", "-c:v", "libwebp", "-lossless", lossless ? "1" : "0",
            .. (lossless ? new[] { "-pix_fmt", "bgra" } : []),
            "-compression_level", "4", "-quality", lossless ? "80" : Math.Clamp(quality, 1, 100).ToString(CultureInfo.InvariantCulture),
            encoded], token);

        if (icc is not null || exif is not null)
        {
            string tagged = Path.Combine(work.Path, "tagged.webp");
            await WebpPhotoMetadata.WriteAsync(encoded, tagged, width, height, icc, exif, token);
            encoded = tagged;
        }
        token.ThrowIfCancellationRequested();
        File.Move(encoded, target, overwrite: true);
    }
}
