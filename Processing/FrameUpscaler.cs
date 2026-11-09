using System.Globalization;

namespace UpscaleHelper.Processing;

internal sealed record UpscaleModel(string Executable, string ModelFolder, string WeightName,
    int NativeScale, string[] Arguments, bool ProgressOnStandardOutput = false);

internal sealed class FrameUpscaler(ToolPaths paths, ToolRunner runner)
{
    public UpscaleModel Select(UpscaleRequest request)
    {
        if (request.Profile == ContentProfile.Animation)
        {
            const string name = "realesr-animevideov3";
            return new(paths.Engine, "models", $"{name}-x{request.Scale}", request.Scale, ["-n", name]);
        }
        if (request.Fast && request.Scale == 2)
            return new(paths.FastEngine, "models-upconv_7_photo", "scale2.0x_model", 2, ["-n", "-1"], true);
        const string natural = "realesrgan-x4plus";
        return new(paths.Engine, "models", natural, 4, ["-n", natural]);
    }

    public Task RunAsync(UpscaleModel model, string input, string output,
        CancellationToken token, Action? frameDone = null)
    {
        string executable = model.Executable;
        if (!File.Exists(executable))
            throw new InvalidOperationException(T("EngineNotFound", Path.GetFileName(executable)));
        string workingDirectory = Path.GetDirectoryName(executable)!;
        string weights = Path.Combine(workingDirectory, model.ModelFolder, model.WeightName);
        if (!File.Exists(weights + ".bin") || !File.Exists(weights + ".param"))
            throw new InvalidOperationException(T("WeightsNotFound", model.WeightName));

        return runner.RunAsync(executable,
            ["-i", input, "-o", output, "-m", model.ModelFolder, "-s",
                model.NativeScale.ToString(CultureInfo.InvariantCulture), "-j", "2:1:2", "-f", "png", "-v", .. model.Arguments],
            token, workingDirectory, onErrorLine: model.ProgressOnStandardOutput ? null : Observe,
            readOutput: model.ProgressOnStandardOutput ? ReadProgressAsync : null);

        void Observe(string line)
        {
            if (line.EndsWith(" done", StringComparison.Ordinal)) frameDone?.Invoke();
        }

        async Task ReadProgressAsync(Stream pipe, CancellationToken ct)
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { } line)
                Observe(line);
        }
    }
}
