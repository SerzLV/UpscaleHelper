param(
    [string]$TargetDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$toolsDirectory = Join-Path ([IO.Path]::GetFullPath($TargetDirectory)) 'tools'

function Install-Engine(
    [string]$Name,
    [string]$Url,
    [string]$Sha256,
    [string[]]$Files
) {
    $destination = Join-Path $toolsDirectory $Name
    $missing = @($Files | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $destination $_) -PathType Leaf)
    })
    if ($missing.Count -eq 0) {
        Write-Host "$Name is already installed."
        return
    }

    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $stage = Join-Path $tempRoot ('UpscaleHelper-engines-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    try {
        Write-Host "Downloading $Name..."
        $archive = Join-Path $stage 'engine.zip'
        Invoke-WebRequest -Uri $Url -UseBasicParsing -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Sha256) {
            throw "SHA-256 verification failed for $Name."
        }

        $unpacked = Join-Path $stage 'unpacked'
        Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
        $launchers = @(Get-ChildItem -LiteralPath $unpacked -Filter $Files[0] -File -Recurse)
        if ($launchers.Count -ne 1) { throw "Unexpected layout in the $Name archive." }
        $engineRoot = $launchers[0].DirectoryName
        foreach ($file in $Files) {
            if (-not (Test-Path -LiteralPath (Join-Path $engineRoot $file) -PathType Leaf)) {
                throw "$Name archive is missing $file."
            }
        }
        foreach ($file in $Files) {
            $target = Join-Path $destination $file
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $engineRoot $file) -Destination $target
        }
        Write-Host "$Name installed in $destination."
    }
    finally {
        $resolved = (Resolve-Path -LiteralPath $stage).Path
        if ([IO.Path]::GetDirectoryName($resolved) -eq $tempRoot -and
            [IO.Path]::GetFileName($resolved).StartsWith('UpscaleHelper-engines-')) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

Install-Engine -Name 'realesrgan' `
    -Url 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip' `
    -Sha256 'ABC02804E17982A3BE33675E4D471E91EA374E65B70167ABC09E31ACB412802D' `
    -Files @(
        'realesrgan-ncnn-vulkan.exe'
        'vcomp140.dll'
        'models/realesrgan-x4plus.bin'
        'models/realesrgan-x4plus.param'
        'models/realesr-animevideov3-x2.bin'
        'models/realesr-animevideov3-x2.param'
        'models/realesr-animevideov3-x4.bin'
        'models/realesr-animevideov3-x4.param'
    )

Install-Engine -Name 'waifu2x' `
    -Url 'https://github.com/nihui/waifu2x-ncnn-vulkan/releases/download/20250915/waifu2x-ncnn-vulkan-20250915-windows.zip' `
    -Sha256 '7425BE94B94E4C8F37A1E433AC0E0100C43790E2C37418F4B65D8235ADFBDC87' `
    -Files @(
        'waifu2x-ncnn-vulkan.exe'
        'models-upconv_7_photo/scale2.0x_model.bin'
        'models-upconv_7_photo/scale2.0x_model.param'
    )
