namespace UpscaleHelper.Processing;

public enum MediaKind { Image, Video }
public enum ContentProfile { Natural, Animation }
public enum VideoQuality { Compact, Balanced, High }

public sealed record UpscaleRequest(string Source, MediaKind Kind, int Scale, ContentProfile Profile,
    string? DestinationFolder, bool Fast = false, VideoSegment? Segment = null, bool RemoveAudio = false,
    VideoQuality VideoQuality = VideoQuality.Balanced);

public sealed record VideoSegment(TimeSpan Start, TimeSpan Length);

public sealed record FrameProgress(int Done, int? EstimatedTotal, double? FramesPerSecond, TimeSpan? Remaining);

public sealed record UpscaleProgress(string Stage, double? Percent, FrameProgress? Frames = null);

public interface IUpscaleProcessor
{
    Task<string> ProcessAsync(UpscaleRequest request, IProgress<UpscaleProgress> progress,
        CancellationToken cancellationToken);
}

public interface IPreviewProvider
{
    Task<PreviewFrame> ReadAsync(string path, CancellationToken cancellationToken);
}

public sealed record PreviewFrame(byte[] Png, int NativeWidth, int NativeHeight);

public static class SupportedMedia
{
    public static string PhotoFilter => $"{T("PhotoComparison")}|*.jpg;*.jpeg;*.png;*.webp";

    public static string Filter =>
        $"{T("PhotosAndVideos")}|*.jpg;*.jpeg;*.png;*.webp;*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v|{T("AllFiles")}|*.*";

    public static MediaKind? GetKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" => MediaKind.Image,
        ".mp4" or ".mkv" or ".mov" or ".webm" or ".avi" or ".m4v" => MediaKind.Video,
        _ => null
    };
}
