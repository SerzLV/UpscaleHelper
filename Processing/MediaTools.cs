using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UpscaleHelper.Processing;

internal sealed record VideoInfo(string FrameRate, double FramesPerSecond, double Duration,
    int Width, int Height, int FrameCount);

internal sealed class MediaTools(ToolPaths paths, ToolRunner runner) : IPreviewProvider, IVideoFrameProvider
{
    private static readonly Regex FrameSize = new(@"\bs:(\d+)x(\d+)\b");
    private bool? hasNvenc;

    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        await runner.RunAsync(paths.Ffprobe,
            ["-v", "error", "-select_streams", "V:0", "-show_streams", "-show_format", "-of", "json", path],
            token, readOutput: (pipe, ct) => pipe.CopyToAsync(bytes, ct));
        bytes.Position = 0;
        using var json = await JsonDocument.ParseAsync(bytes, cancellationToken: token);
        var streams = json.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0)
            throw new InvalidDataException(T("NoVideoStream"));
        var video = streams[0];
        if (Value(video, "color_transfer") is "smpte2084" or "arib-std-b67")
            throw new InvalidDataException(T("HdrUnsupported"));

        string frameRate = Value(video, "avg_frame_rate") ?? "0/0";
        if (!TryFrameRate(frameRate, out double fps))
        {
            frameRate = Value(video, "r_frame_rate") ?? "0/0";
            if (!TryFrameRate(frameRate, out fps))
                (frameRate, fps) = ("30", 30);
        }

        double seconds = ReadDuration(video);
        if (seconds == 0 && json.RootElement.TryGetProperty("format", out var format))
            seconds = ReadDuration(format);
        int width = video.GetProperty("width").GetInt32();
        int height = video.GetProperty("height").GetInt32();
        if (TryRatio(Value(video, "sample_aspect_ratio")?.Replace(':', '/') ?? "", out double sar))
            width = (int)Math.Clamp(Math.Round(width * sar), 1, int.MaxValue);
        _ = int.TryParse(Value(video, "nb_frames"), CultureInfo.InvariantCulture, out int frameCount);
        return new VideoInfo(frameRate, fps, seconds, width, height, frameCount);
    }

    public Task<PreviewFrame> ReadAsync(string path, CancellationToken cancellationToken) =>
        ReadPreviewAsync(path, [], "", cancellationToken);

    public async Task<PreviewFrame> ReadFrameAsync(ComparisonClip clip, ComparisonTrack track, int index, CancellationToken token)
    {
        var (seek, relative) = clip.FrameWindow(track, index);
        var frame = await ReadPreviewAsync(track.Path, ["-ss", Seconds(seek.TotalSeconds)],
            $"{NormalizeFrames(clip.FrameRate)},select=eq(n\\,{relative.ToString(CultureInfo.InvariantCulture)}),", token);
        if (frame.Png.Length == 0) throw new InvalidDataException(T("FrameUnavailable"));
        return frame;
    }

    private async Task<PreviewFrame> ReadPreviewAsync(string path, string[] inputOptions, string filterPrefix,
        CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        int width = 0, height = 0;
        const string nativeSize = "lte(iw*ih,40000000)*lte(max(iw,ih),8192)";
        string filter = $"{filterPrefix}showinfo=checksum=0,scale=w='if({nativeSize},iw,min(4096,iw))':h='if({nativeSize},ih,min(4096,ih))':force_original_aspect_ratio=decrease,setsar=1";
        await runner.RunAsync(paths.Ffmpeg, ["-hide_banner", "-loglevel", "info", "-nostdin",
            .. inputOptions, "-i", path, "-map", "0:V:0", "-frames:v", "1", "-vf", filter, "-fps_mode", "passthrough",
            "-c:v", "png", "-threads", "1", "-f", "image2pipe", "pipe:1"], cancellationToken,
            onErrorLine: line =>
            {
                if (width != 0 || !line.Contains("Parsed_showinfo", StringComparison.Ordinal)) return;
                var size = FrameSize.Match(line);
                if (!size.Success) return;
                _ = int.TryParse(size.Groups[1].Value, out width);
                _ = int.TryParse(size.Groups[2].Value, out height);
            },
            readOutput: (pipe, ct) => pipe.CopyToAsync(bytes, ct));
        return new(bytes.ToArray(), width, height);
    }

    public Task NormalizeImageAsync(string source, string target, CancellationToken token) =>
        RunAsync(["-i", source, "-map", "0:V:0", "-frames:v", "1", "-c:v", "png",
            "-pix_fmt", "rgba", "-threads", "1", "-update", "1", target], token);

    public Task ResizeImageAsync(string source, string target, CancellationToken token) =>
        RunAsync(["-i", source, "-frames:v", "1", "-vf", "scale=iw/2:ih/2:flags=lanczos",
            "-threads", "1", "-update", "1", target], token);

    public Task DecodeAsync(string path, VideoInfo info, VideoSegment? segment, CancellationToken token,
        Func<Stream, CancellationToken, Task> readFrames) =>
        RunAsync([.. Seek(segment), "-i", path, "-map", "0:V:0", .. Limit(segment),
            "-vf", NormalizeFrames(info.FrameRate),
            "-c:v", "png", "-pix_fmt", "rgb24", "-threads", "2", "-f", "image2pipe", "pipe:1"],
            token, readOutput: readFrames);

    public async Task EncodeAsync(string source, string target, VideoInfo info, int scale, bool resize,
        VideoSegment? segment, bool removeAudio, VideoQuality quality, CancellationToken token,
        Func<Stream, CancellationToken, Task> writeFrames)
    {
        bool fitsNvenc = Math.Max(info.Width, info.Height) * (long)scale <= 4096 &&
            Math.Min(info.Width, info.Height) * (long)scale >= 256;
        var (crf, cq) = quality switch
        {
            VideoQuality.Compact => ("27", "27"),
            VideoQuality.High => ("14", "15"),
            _ => ("18", "19")
        };
        string[] codec = fitsNvenc && await HasNvencAsync(token)
            ? ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", cq, "-b:v", "0"]
            : ["-c:v", "libx264", "-preset", "fast", "-crf", crf];
        string filters = (resize ? "scale=iw/2:ih/2:flags=lanczos," : "") +
            "pad=ceil(iw/2)*2:ceil(ih/2)*2,scale=out_color_matrix=bt709,setsar=1";
        string[] audioSource = removeAudio ? [] : [.. Seek(segment), .. Limit(segment), "-i", source];
        string[] audio = removeAudio ? ["-an"] : ["-map", "1:a?", "-c:a", "aac", "-b:a", "192k"];

        await RunAsync(["-f", "image2pipe", "-framerate", info.FrameRate, "-c:v", "png", "-i", "pipe:0",
            .. audioSource, "-map", "0:v:0", .. audio, "-map_chapters", "-1",
            "-vf", filters, .. codec, "-pix_fmt", "yuv420p",
            "-colorspace", "bt709", "-movflags", "+faststart", target],
            token, writeInput: writeFrames);
    }

    public async Task<VideoInfo> BuildComparisonAsync(string source, string enhanced, string output,
        VideoSegment? sourceSegment, VideoSegment? enhancedSegment,
        IProgress<UpscaleProgress>? progress, CancellationToken token)
    {
        VideoInfo result = await ProbeAsync(enhanced, token);
        double? duration = null;
        if (progress is not null)
        {
            VideoInfo original = await ProbeAsync(source, token);
            double start = sourceSegment?.Start.TotalSeconds ?? 0;
            if ((original.Duration > 0 && start >= original.Duration) ||
                (result.Duration > 0 && start >= result.Duration))
                throw new InvalidDataException(T("PairSegmentPastEnd"));
            if (original.Duration > 0 && result.Duration > 0)
                duration = Math.Min(Math.Min(original.Duration, result.Duration) - start,
                    sourceSegment?.Length.TotalSeconds ?? double.PositiveInfinity);
        }
        const string fit = "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2:color=0x131622,setsar=1";
        string filter = $"{NormalizeFrames(result.FrameRate)},{fit}";
        string comparison = $"[0:V:0]{filter}[left];[1:V:0]{filter}[right];[left][right]hstack=shortest=1[view]";
        await RunAsync(["-progress", "pipe:1", .. Seek(sourceSegment), "-i", source,
            .. Seek(enhancedSegment), "-i", enhanced, .. Limit(sourceSegment),
            "-filter_complex", comparison, "-map", "[view]", "-map", "1:a:0?",
            "-af", "apad", "-c:a", "aac", "-b:a", "128k", "-shortest",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-g", "60",
            "-r", result.FrameRate, "-fps_mode", "cfr", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", output], token, readOutput: async (pipe, ct) =>
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                const string key = "out_time_us=";
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (progress is null || duration is not { } total ||
                        !line.StartsWith(key, StringComparison.Ordinal) ||
                        !long.TryParse(line.AsSpan(key.Length), CultureInfo.InvariantCulture, out long microseconds))
                        continue;
                    progress.Report(new(T("PreparingComparison"), Math.Clamp(microseconds / 10_000.0 / total, 0, 99)));
                }
            });
        return result;
    }

    private static string NormalizeFrames(string frameRate) =>
        $"bwdif=mode=send_frame:deint=interlaced,scale=w='max(1,trunc(iw*sar+0.5))':h=ih,setsar=1,fps={frameRate}:start_time=0";

    private static string[] Seek(VideoSegment? segment) => segment is null
        ? [] : ["-ss", Seconds(segment.Start.TotalSeconds)];

    private static string[] Limit(VideoSegment? segment) => segment is null
        ? [] : ["-t", Seconds(segment.Length.TotalSeconds)];

    private static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private async Task<bool> HasNvencAsync(CancellationToken token)
    {
        if (hasNvenc is { } supported) return supported;
        try
        {
            await RunAsync(["-f", "lavfi", "-i", "color=s=640x360:r=1", "-frames:v", "1",
                "-c:v", "h264_nvenc", "-f", "null", "-"], token);
            return (hasNvenc = true).Value;
        }
        catch (InvalidOperationException)
        {
            return (hasNvenc = false).Value;
        }
    }

    private Task RunAsync(string[] args, CancellationToken token,
        Func<Stream, CancellationToken, Task>? readOutput = null,
        Func<Stream, CancellationToken, Task>? writeInput = null) =>
        runner.RunAsync(paths.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", .. args],
            token, readOutput: readOutput, writeInput: writeInput);

    private static string? Value(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) ? value.ToString() : null;

    private static double ReadDuration(JsonElement element) =>
        double.TryParse(Value(element, "duration"), CultureInfo.InvariantCulture, out var seconds) &&
        double.IsFinite(seconds) && seconds > 0 ? seconds : 0;

    internal static bool TryFrameRate(string value, out double rate) => TryRatio(value, out rate) && rate <= 240;

    private static bool TryRatio(string value, out double rate)
    {
        var fields = value.Split('/');
        rate = 0;
        if (fields.Length > 2 || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out int numerator) || numerator <= 0)
            return false;
        int denominator = 1;
        if (fields.Length == 2 &&
            (!int.TryParse(fields[1], CultureInfo.InvariantCulture, out denominator) || denominator <= 0))
            return false;
        rate = (double)numerator / denominator;
        return true;
    }
}
