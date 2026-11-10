namespace UpscaleHelper.Processing;

internal sealed record FluxRuntime(string PythonPath, string ComfyDirectory, string ModelDirectory, string? DependencyDirectory)
{
    public bool IsAvailable =>
        Path.IsPathFullyQualified(PythonPath ?? "") && File.Exists(PythonPath) &&
        ComfyDirectory is { } comfy && Path.IsPathFullyQualified(comfy) && File.Exists(Path.Combine(comfy, "nodes.py")) &&
        ModelDirectory is { } models && Path.IsPathFullyQualified(models) &&
        File.Exists(Path.Combine(models, "diffusion_models", "flux-2-klein-4b-fp8.safetensors")) &&
        File.Exists(Path.Combine(models, "text_encoders", "qwen_3_4b.safetensors")) &&
        File.Exists(Path.Combine(models, "vae", "flux2-vae.safetensors")) &&
        (DependencyDirectory is null || (Path.IsPathFullyQualified(DependencyDirectory) && Directory.Exists(DependencyDirectory)));
}
