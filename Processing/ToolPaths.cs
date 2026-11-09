namespace UpscaleHelper.Processing;

internal sealed class ToolPaths
{
    public ToolPaths(string root)
    {
        var tools = Path.Combine(root, "tools");
        Engine = Path.Combine(tools, "realesrgan", "realesrgan-ncnn-vulkan.exe");
        FastEngine = Path.Combine(tools, "waifu2x", "waifu2x-ncnn-vulkan.exe");
        Ffmpeg = FindFfmpeg(tools, "ffmpeg.exe");
        Ffprobe = FindFfmpeg(tools, "ffprobe.exe");
    }

    public string Engine { get; }
    public string FastEngine { get; }
    public string Ffmpeg { get; }
    public string Ffprobe { get; }

    private static string FindFfmpeg(string tools, string name)
    {
        var local = Path.Combine(tools, "ffmpeg", name);
        return File.Exists(local) ? local : name;
    }
}
