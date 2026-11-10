param(
    [ValidateSet('Cpu', 'Nvidia')]
    [string]$Device = 'Cpu',
    [switch]$Generative
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Generative -and $Device -ne 'Nvidia') { throw 'Generative restoration requires -Device Nvidia.' }
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UpscaleHelper\restoration'
$weights = Join-Path $root 'weights'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stage = Join-Path $tempRoot ('UH-r-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$ready = Join-Path $root 'runtime-v1.ready'
$diffbir = Join-Path $root 'diffbir'
$detailReady = Join-Path $diffbir 'runtime-v1.ready'
[IO.Directory]::CreateDirectory($weights) | Out-Null
[IO.Directory]::CreateDirectory($stage) | Out-Null

function Get-VerifiedFile([string]$Url, [string]$Destination, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
    $download = $Destination + '.' + [guid]::NewGuid().ToString('N') + '.download'
    Write-Host "Downloading $([IO.Path]::GetFileName($Destination))..."
    try {
        & curl.exe --fail --progress-bar --show-error --location --retry 2 --output $download $Url
        if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $Sha256) {
            throw "Download or SHA-256 verification failed for $Destination."
        }
        Move-Item -LiteralPath $download -Destination $Destination -Force
    }
    finally {
        [IO.File]::Delete($download)
    }
}

function Invoke-SetupTool([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Setup command failed with exit code $LASTEXITCODE." }
}

function Install-DiffBir([string]$Uv, [string]$Python) {
    [IO.Directory]::CreateDirectory($diffbir) | Out-Null
    [IO.File]::Delete($detailReady)
    Invoke-SetupTool $Python @('-I', '-c', 'import torch; assert torch.cuda.is_available(), "CUDA device unavailable"')
    Invoke-SetupTool $Uv @('pip', 'install', '--python', $Python, '--index', 'https://download.pytorch.org/whl/cu128',
        'torch==2.9.1+cu128', 'torchvision==0.24.1+cu128')
    Invoke-SetupTool $Uv @('pip', 'install', '--python', $Python, 'omegaconf==2.3.0', 'timm==0.9.16', 'ftfy==6.3.1',
        'regex==2024.11.6', 'scipy==1.16.3', 'torchsde==0.2.6', 'tqdm==4.67.1')

    $revision = '5c2d6c177c3f2965e6d33a32931248d242ef7b3d'
    $archive = Join-Path $stage 'diffbir.zip'
    Get-VerifiedFile "https://codeload.github.com/XPixelGroup/DiffBIR/zip/$revision" $archive `
        '43BD953CE002E7D69E057CA43548C10DF2A11570AA67BBA0CC38507E7B58BEA5'
    Expand-Archive -LiteralPath $archive -DestinationPath $stage
    $source = Join-Path $stage "DiffBIR-$revision"
    $upstreamDirectory = Join-Path $diffbir 'upstream'
    [IO.Directory]::CreateDirectory($upstreamDirectory) | Out-Null
    foreach ($part in 'diffbir', 'configs', 'LICENSE') {
        Copy-Item -LiteralPath (Join-Path $source $part) -Destination $upstreamDirectory -Recurse -Force
    }
    # The pinned sampler uses a type annotation that PyTorch does not export.
    $sampler = Join-Path $upstreamDirectory 'diffbir\sampler\edm_sampler.py'
    $code = [IO.File]::ReadAllText($sampler).Replace('torch.Tuple[int]', 'tuple[int, ...]')
    [IO.File]::WriteAllText($sampler, $code)

    $modelDirectory = Join-Path $diffbir 'weights'
    [IO.Directory]::CreateDirectory($modelDirectory) | Out-Null
    $modelBase = 'https://huggingface.co/lxq007/DiffBIR-v2/resolve/fd6263503564bfbfa156665a90832e1b729434f4'
    $checkpoints = @{
        'sd2.1-base-zsnr-laionaes5.ckpt' = '30BDDA1968C2DAE28FF23B2B44B0F2A87058D76EEA5FC4EADFA3448E40E85CF9'
        'DiffBIR_v2.1.pt' = '49477DE737BE9028AC2D9352CF45633324A0090AFDE57A7AA358321DD6A1DE53'
        'realesrgan_s4_swinir_100k.pth' = '577B8F84815D324E5EE17EF3C85D8BC9B7EBD05B20A6D61A5452B66E7E24DCAF'
    }
    foreach ($name in $checkpoints.Keys) {
        Get-VerifiedFile "$modelBase/${name}?download=true" (Join-Path $modelDirectory $name) $checkpoints[$name]
    }
    Invoke-SetupTool $Python @('-I', '-B', '-c', 'import sys; sys.path.insert(0, sys.argv[1]); import omegaconf; from diffbir.pipeline import SwinIRPipeline',
        $upstreamDirectory)
    [IO.File]::WriteAllText($detailReady, $revision)
    Write-Host 'DiffBIR is ready. Open Draw details in UpscaleHelper.'
}

$previousPython = $env:UV_PYTHON_INSTALL_DIR
$previousCache = $env:UV_CACHE_DIR
try {
    $env:UV_PYTHON_INSTALL_DIR = Join-Path $root 'python'
    $env:UV_CACHE_DIR = Join-Path $stage 'c'
    $uvArchive = Join-Path $stage 'uv.zip'
    Get-VerifiedFile 'https://github.com/astral-sh/uv/releases/download/0.13.7/uv-x86_64-pc-windows-msvc.zip' `
        $uvArchive '0CDC5B38FE5A8168ED6A25814F5BA43C21D313DD3187314FCCB37B7CEC46E021'
    $uvDirectory = Join-Path $stage 'uv'
    Expand-Archive -LiteralPath $uvArchive -DestinationPath $uvDirectory
    $uv = (Get-ChildItem -LiteralPath $uvDirectory -Filter 'uv.exe' -File -Recurse | Select-Object -First 1).FullName

    $upstream = 'https://raw.githubusercontent.com/swz30/Restormer/68dc6ac472db26f16361150cb7a96a1bc87da93f'
    Get-VerifiedFile "$upstream/basicsr/models/archs/restormer_arch.py" (Join-Path $root 'restormer_arch.py') `
        '3BE243FA3C8E2CB2C9459EEAC062B04D6084DBCC328292DAE1C3E52BA6F7434A'
    Get-VerifiedFile "$upstream/LICENSE.md" (Join-Path $root 'Restormer-LICENSE.md') `
        '2B93776512924BC095EC5D97A79D76CF1AB401AE641D1AE1F46E94F1C53A2E59'
    $models = @{
        'single_image_defocus_deblurring.pth' = '7DCE451F33F8F5E0FAF7C4E3996E5DCC1BD425ECD1ADA99B0F9750E490FD4C9E'
        'motion_deblurring.pth' = '194E38FB5B607C9DC5A5B3E08E65B2E79EE2BF0EF5048E0612F6B2FF2F79DA31'
        'real_denoising.pth' = '4CAE18BED8A291B9A5DEEEAB48756BBE6F61FCF7F31A4CCA0969B24608601387'
    }
    foreach ($name in $models.Keys) {
        Get-VerifiedFile "https://github.com/swz30/Restormer/releases/download/v1.0/$name" (Join-Path $weights $name) $models[$name]
    }

    [IO.File]::Delete($ready)
    $venv = Join-Path $root 'venv'
    $python = Join-Path $venv 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
        Invoke-SetupTool $uv @('python', 'install', '3.12', '--no-registry', '--no-bin')
        Invoke-SetupTool $uv @('venv', $venv, '--managed-python', '--python', '3.12')
    }
    $backend = if ($Device -eq 'Nvidia') { 'cu128' } else { 'cpu' }
    Invoke-SetupTool $uv @('pip', 'install', '--python', $python, "torch==2.9.1+$backend", '--index',
        "https://download.pytorch.org/whl/$backend", 'Pillow==12.2.0', 'einops==0.8.2', 'numpy==2.3.5')
    Invoke-SetupTool $python @('-I', '-c', 'import torch, PIL, einops, numpy; print(torch.__version__)')
    [IO.File]::WriteAllText($ready, $backend)
    Write-Host 'Photo restoration is ready. Open Restore photo in UpscaleHelper.'
    if ($Generative) { Install-DiffBir $uv $python }
    elseif ($Device -eq 'Cpu' -and [IO.File]::Exists($detailReady)) { [IO.File]::Delete($detailReady) }
}
finally {
    $env:UV_PYTHON_INSTALL_DIR = $previousPython
    $env:UV_CACHE_DIR = $previousCache
    $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolved) -eq $tempRoot -and
        [IO.Path]::GetFileName($resolved) -match '^UH-r-[0-9a-f]{12}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
