# build-portable.ps1 — Build CluadeX as portable, self-contained zip
# Usage: powershell -ExecutionPolicy Bypass -File build-portable.ps1

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "CluadeX" "CluadeX.csproj"
$publishDir = Join-Path $root "publish" "CluadeX-Portable"
$zipOutput = Join-Path $root "publish"

# Read version from csproj
[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version
if (-not $version) { $version = "2.1.0" }

Write-Host "=== Building CluadeX v$version Portable ===" -ForegroundColor Cyan
Write-Host ""

# Clean
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

# Publish self-contained
Write-Host "[1/5] Publishing self-contained (win-x64)..." -ForegroundColor Yellow
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED!" -ForegroundColor Red
    exit 1
}

# Create portable marker
Write-Host "[2/5] Creating portable marker..." -ForegroundColor Yellow
"CluadeX Portable Mode - Data stored in Data/ folder" | Out-File (Join-Path $publishDir "portable") -Encoding UTF8

# Create default Data directories
Write-Host "[3/5] Creating Data directories..." -ForegroundColor Yellow
$dataDir = Join-Path $publishDir "Data"
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir "Models") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir "Cache") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir "Logs") | Out-Null

# Copy llama-backend if exists
Write-Host "[4/5] Copying llama-backend..." -ForegroundColor Yellow
$llamaBackend = Join-Path $root "CluadeX" "llama-backend"
if (Test-Path $llamaBackend) {
    $destBackend = Join-Path $publishDir "llama-backend"
    New-Item -ItemType Directory -Force -Path $destBackend | Out-Null
    Copy-Item (Join-Path $llamaBackend "*") $destBackend -Recurse -Force
    Write-Host "  Copied llama-backend (Gemma 4 support)" -ForegroundColor Green
} else {
    Write-Host "  No llama-backend found (skipped)" -ForegroundColor DarkYellow
}

# Create MCP config template
$mcpConfig = Join-Path $dataDir "mcp_servers.json"
if (-not (Test-Path $mcpConfig)) {
    '{"mcpServers":{}}' | Out-File $mcpConfig -Encoding UTF8
}

# Create zip
Write-Host "[5/5] Creating zip..." -ForegroundColor Yellow
$zipName = "CluadeX-v$version-Portable-win-x64.zip"
$zipPath = Join-Path $zipOutput $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath }
Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host ""
Write-Host "=== Done! ===" -ForegroundColor Green
Write-Host "  Output: $zipPath" -ForegroundColor Cyan
Write-Host "  Size:   $([math]::Round($zipSize, 1)) MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "To use: Extract zip, run CluadeX.exe — no installation needed!" -ForegroundColor White
