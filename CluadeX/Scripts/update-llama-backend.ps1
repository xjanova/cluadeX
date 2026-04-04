# update-llama-backend.ps1
# Downloads the latest llama.cpp CUDA 12 release and replaces LLamaSharp backend DLLs
# This enables support for newer model architectures (Gemma 4, etc.)

param(
    [string]$Tag = "b8660",
    [string]$TargetDir = ""
)

$ErrorActionPreference = "Stop"

if (-not $TargetDir) {
    $TargetDir = Join-Path $PSScriptRoot ".." "bin" "Debug" "net8.0-windows" "runtimes" "win-x64" "native"
}

$baseUrl = "https://github.com/ggml-org/llama.cpp/releases/download/$Tag"
$zipName = "llama-$Tag-bin-win-cuda-12.4-x64.zip"
$cudartZip = "cudart-llama-bin-win-cuda-12.4-x64.zip"
$tempDir = Join-Path $env:TEMP "llama-update-$Tag"

Write-Host "=== CluadeX llama.cpp Backend Updater ===" -ForegroundColor Cyan
Write-Host "Release: $Tag"
Write-Host "Target:  $TargetDir"
Write-Host ""

# Download
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
$zipPath = Join-Path $tempDir $zipName

if (-not (Test-Path $zipPath)) {
    Write-Host "Downloading $zipName..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "$baseUrl/$zipName" -OutFile $zipPath
    Write-Host "Downloaded!" -ForegroundColor Green
}

# Extract
$extractDir = Join-Path $tempDir "extracted"
if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
Write-Host "Extracting..."
Expand-Archive -Path $zipPath -DestinationPath $extractDir

# Find the DLLs
$sourceDir = Get-ChildItem -Path $extractDir -Recurse -Filter "llama.dll" | Select-Object -First 1 | Split-Path -Parent
if (-not $sourceDir) {
    Write-Host "ERROR: llama.dll not found in extracted archive!" -ForegroundColor Red
    exit 1
}
Write-Host "Source DLLs: $sourceDir"

# Files to copy
$dllFiles = @("llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cuda.dll")

# Copy to cuda12 backend
$cuda12Dir = Join-Path $TargetDir "cuda12"
if (Test-Path $cuda12Dir) {
    Write-Host ""
    Write-Host "Updating cuda12 backend..." -ForegroundColor Yellow
    foreach ($dll in $dllFiles) {
        $src = Join-Path $sourceDir $dll
        $dst = Join-Path $cuda12Dir $dll
        if (Test-Path $src) {
            Copy-Item $src $dst -Force
            Write-Host "  Replaced: $dll" -ForegroundColor Green
        } else {
            Write-Host "  Not found: $dll (skipped)" -ForegroundColor DarkYellow
        }
    }
}

# Also copy to avx2 backend (CPU fallback)
$avx2Dir = Join-Path $TargetDir "avx2"
if (Test-Path $avx2Dir) {
    # For CPU, we need the non-CUDA build. Skip for now.
    Write-Host "  (avx2 CPU backend kept as-is)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Done! Gemma 4 and other new architectures should now be supported." -ForegroundColor Green
Write-Host "Restart CluadeX to apply changes." -ForegroundColor Cyan
