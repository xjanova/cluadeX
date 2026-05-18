# update-llama-backend.ps1
# Downloads the latest llama.cpp Windows release and extracts it into
# CluadeX\bin\<config>\net8.0-windows\llama-backend\ so newer model architectures
# (Gemma 4, Llama 4, Qwen 3, etc.) can be loaded via the LlamaServer fallback.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File Scripts\update-llama-backend.ps1
#   powershell -ExecutionPolicy Bypass -File Scripts\update-llama-backend.ps1 -Variant cuda  # (default: auto-detect)
#
# Variants: cuda, vulkan, cpu. Pass -Variant to force one.

[CmdletBinding()]
param(
    [ValidateSet('auto', 'cuda', 'vulkan', 'cpu')]
    [string]$Variant = 'auto',

    [string]$Config = 'Debug',

    # Override target dir (default: CluadeX\bin\<Config>\net8.0-windows\llama-backend)
    [string]$TargetDir = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $TargetDir) {
    $TargetDir = Join-Path $repoRoot "CluadeX\bin\$Config\net8.0-windows\llama-backend"
}

Write-Host "==> Target directory: $TargetDir" -ForegroundColor Cyan

# 1. Pick a variant if 'auto'
if ($Variant -eq 'auto') {
    $hasCuda = $false
    try {
        $nv = & nvidia-smi 2>$null
        if ($LASTEXITCODE -eq 0) { $hasCuda = $true }
    } catch { }
    $Variant = if ($hasCuda) { 'cuda' } else { 'cpu' }
    Write-Host "==> Auto-selected variant: $Variant" -ForegroundColor Cyan
}

# 2. Resolve the latest release asset URLs via GitHub API
Write-Host "==> Querying llama.cpp latest release..." -ForegroundColor Cyan
$apiUrl = 'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest'
$release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'CluadeX-Updater' }

# Main bin asset (llama-server, ggml, etc.)
$mainPattern = switch ($Variant) {
    'cuda'   { 'llama-.*-bin-win-cuda-(12|cu12).*x64\.zip' }
    'vulkan' { 'llama-.*-bin-win-vulkan-x64\.zip' }
    'cpu'    { 'llama-.*-bin-win-cpu-x64\.zip' }
}
$mainAsset = $release.assets | Where-Object { $_.name -match $mainPattern } | Select-Object -First 1
if (-not $mainAsset) {
    Write-Host "Could not find $Variant asset in release $($release.tag_name)" -ForegroundColor Red
    Write-Host "Available assets:" -ForegroundColor Yellow
    $release.assets | ForEach-Object { Write-Host "  $($_.name)" }
    exit 1
}

# CUDA runtime asset (cudart, cublas) — only needed for CUDA variant.
# Most users don't have the CUDA Toolkit installed, so we ship cudart64_12.dll +
# cublas64_12.dll alongside ggml-cuda.dll. Without these, ggml-cuda silently
# falls back to CPU-only and you wonder why your GPU isn't being used.
$cudartAsset = $null
if ($Variant -eq 'cuda') {
    $cudartAsset = $release.assets | Where-Object { $_.name -match 'cudart-.*-bin-win-cuda-12.*x64\.zip' } | Select-Object -First 1
    if (-not $cudartAsset) {
        Write-Host "WARN: cudart runtime asset not found — GPU may fall back to CPU" -ForegroundColor Yellow
    }
}

# Helper to download + extract one asset
function Download-And-Extract {
    param([Parameter(Mandatory)] $Asset, [Parameter(Mandatory)] [string] $Dest)
    Write-Host "==> Downloading $($Asset.name) ($([math]::Round($Asset.size / 1MB, 1)) MB)..." -ForegroundColor Cyan
    $tempZip = Join-Path $env:TEMP $Asset.name
    Invoke-WebRequest -Uri $Asset.browser_download_url -OutFile $tempZip -UseBasicParsing
    $tempExtract = Join-Path $env:TEMP "llama-extract-$(Get-Random)"
    Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force
    # Find binaries (could be at root or in a subfolder)
    $binFolder = (Get-ChildItem -Path $tempExtract -Filter '*.dll' -Recurse | Select-Object -First 1).DirectoryName
    if (-not $binFolder) { $binFolder = $tempExtract }
    Copy-Item -Path (Join-Path $binFolder '*') -Destination $Dest -Recurse -Force
    Remove-Item $tempZip -Force
    Remove-Item $tempExtract -Recurse -Force
}

# 3. Ensure dest exists
if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
}

Write-Host "==> Extracting into $TargetDir..." -ForegroundColor Cyan
Download-And-Extract -Asset $mainAsset -Dest $TargetDir
if ($cudartAsset) { Download-And-Extract -Asset $cudartAsset -Dest $TargetDir }

# 4. Verify
$installedExe = Join-Path $TargetDir 'llama-server.exe'
if (Test-Path $installedExe) {
    Write-Host "==> Done!" -ForegroundColor Green
    Write-Host "    Installed: $installedExe" -ForegroundColor Green
    Write-Host "    Release:   $($release.tag_name)" -ForegroundColor Green
    & $installedExe --version 2>&1 | Where-Object { $_ -match 'version:' } | ForEach-Object { Write-Host "    $_" -ForegroundColor Green }
} else {
    Write-Host "Installation verification failed: $installedExe missing" -ForegroundColor Red
    exit 1
}
