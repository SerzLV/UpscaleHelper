using System.Globalization;
using System.Security.Cryptography;
using UpscaleHelper.Processing;

namespace UpscaleHelper.Tests;

public class MediaComparisonTests
{
    [FfmpegFact]
    public async Task PausedFramesUseBothNativeSourcesAndTheClipTimeline()
    {
        using var work = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var paths = new ToolPaths(work.Path);
        var runner = new ToolRunner();
        var media = new MediaTools(paths, runner);
        string source = Path.Combine(work.Path, "original.mp4");
        string result = Path.Combine(work.Path, "enhanced.mp4");
        await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=24:d=7",
            "-vf", "select='not(eq(mod(n,5),1))'", "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "ultrafast", source], token);
        await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-i", source, "-vf", "scale=1280:720,fps=30000/1001",
            "-c:v", "libx264", "-preset", "ultrafast", result], token);

        var builder = new VideoPreviewBuilder(new UnusedUpscaler(), media);
        foreach (double start in new[] { 1, 1.037 })
        {
            var offset = TimeSpan.FromSeconds(start);
            using var comparison = await builder.CompareAsync(source, result,
                new(offset, TimeSpan.FromSeconds(5)), new InlineProgress<UpscaleProgress>(_ => { }), token);
            var clip = comparison.Clip;
            Assert.Equal(offset, clip.Original.Start);
            Assert.Equal(clip.Original.Start, clip.Enhanced.Start);

            foreach (int index in new[] { 0, 20, 60, 75, 120, clip.FrameCount - 1 })
            {
                var left = await media.ReadFrameAsync(clip, clip.Original, index, token);
                var right = await media.ReadFrameAsync(clip, clip.Enhanced, index, token);
                Assert.Equal((640, 360), (left.NativeWidth, left.NativeHeight));
                Assert.Equal((1280, 720), (right.NativeWidth, right.NativeHeight));
                AssertFrame(await SequentialFrame(clip, clip.Original, index), left.Png, start, index, "original");
                AssertFrame(await SequentialFrame(clip, clip.Enhanced, index), right.Png, start, index, "enhanced");
            }
        }

        async Task<byte[]> SequentialFrame(ComparisonClip clip, ComparisonTrack track, int index)
        {
            using var bytes = new MemoryStream();
            // Reference decoder starts at the beginning of the segment and counts output frames directly.
            string filter = $"bwdif=mode=send_frame:deint=interlaced,setsar=1,fps={clip.FrameRate}:start_time=0,select=eq(n\\,{index})";
            await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-ss",
                track.Start.TotalSeconds.ToString(CultureInfo.InvariantCulture), "-i", track.Path, "-map", "0:V:0",
                "-vf", filter, "-frames:v", "1", "-fps_mode", "passthrough", "-c:v", "png", "-threads", "1",
                "-f", "image2pipe", "pipe:1"], token, readOutput: (stream, ct) => stream.CopyToAsync(bytes, ct));
            return bytes.ToArray();
        }
    }

    private static void AssertFrame(byte[] expected, byte[] actual, double start, int index, string side) =>
        Assert.True(SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(actual)),
            $"The {side} frame at index {index} for the segment starting at {start} s differs from sequential decoding.");

    private sealed class UnusedUpscaler : IUpscaleProcessor
    {
        public Task<string> ProcessAsync(UpscaleRequest request, IProgress<UpscaleProgress> progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

internal sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        if (!new[] { "ffmpeg.exe", "ffprobe.exe" }.All(name =>
            directories.Any(directory => File.Exists(Path.Combine(directory.Trim('"'), name)))))
            Skip = "FFmpeg and FFprobe are required for this integration test.";
    }
}
