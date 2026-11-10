param(
    [Parameter(Mandatory)][string]$ComfyDirectory,
    [string]$ModelDirectory,
    [string]$PythonPath,
    [string]$DependencyDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runtime = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UpscaleHelper\restoration'
if (!$ModelDirectory) { $ModelDirectory = Join-Path $ComfyDirectory 'models' }
if (!$PythonPath) { $PythonPath = Join-Path $runtime 'venv\Scripts\python.exe' }

$comfy = (Resolve-Path -LiteralPath $ComfyDirectory).ProviderPath
$models = (Resolve-Path -LiteralPath $ModelDirectory).ProviderPath
$python = (Resolve-Path -LiteralPath $PythonPath).ProviderPath
$dependencies = if ($DependencyDirectory) { (Resolve-Path -LiteralPath $DependencyDirectory).ProviderPath } else { $null }
if (!(Test-Path -LiteralPath (Join-Path $comfy 'nodes.py') -PathType Leaf)) { throw 'ComfyUI nodes.py was not found.' }
foreach ($weight in @(
    'diffusion_models\flux-2-klein-4b-fp8.safetensors',
    'text_encoders\qwen_3_4b.safetensors',
    'vae\flux2-vae.safetensors'
)) {
    if (!(Test-Path -LiteralPath (Join-Path $models $weight) -PathType Leaf)) { throw "Missing model: $weight" }
}

& $python -I -B -c 'import sys; sys.path[:0] = sys.argv[1:]; import comfy_aimdo, torch; from PIL import ImageCms; from scipy.ndimage import gaussian_filter; assert torch.cuda.is_available(), "CUDA unavailable"' @($dependencies | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { throw 'The configured Python environment could not load the required CUDA dependencies.' }

[IO.Directory]::CreateDirectory($runtime) | Out-Null
$path = Join-Path $runtime 'flux-runtime.json'
$temporary = "$path.$([guid]::NewGuid().ToString('N')).tmp"
try {
    $config = @{
        PythonPath = $python
        ComfyDirectory = $comfy
        ModelDirectory = $models
        DependencyDirectory = $dependencies
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($temporary, $config)
    [IO.File]::Move($temporary, $path, $true)
} finally {
    [IO.File]::Delete($temporary)
}
Write-Host 'FLUX is registered. Restart UpscaleHelper and open Draw details.'
