# Hook: sessionstart-detect-pkg-mgr
# Phase: SessionStart
# Detects which package manager the workspace uses and writes it to
# ~/.cluadex/logs/last-package-manager.txt.
param([string]$Cwd = "")

if (-not $Cwd -or -not (Test-Path $Cwd)) { exit 0 }

$detected = @()
if (Test-Path (Join-Path $Cwd 'pnpm-lock.yaml')) { $detected += 'pnpm' }
if (Test-Path (Join-Path $Cwd 'yarn.lock'))       { $detected += 'yarn' }
if (Test-Path (Join-Path $Cwd 'bun.lockb'))       { $detected += 'bun' }
if (Test-Path (Join-Path $Cwd 'package-lock.json')) { $detected += 'npm' }
if (Test-Path (Join-Path $Cwd 'poetry.lock'))     { $detected += 'poetry' }
if (Test-Path (Join-Path $Cwd 'Pipfile.lock'))    { $detected += 'pipenv' }
if (Test-Path (Join-Path $Cwd 'requirements.txt')) { $detected += 'pip' }
if (Test-Path (Join-Path $Cwd 'Cargo.toml'))      { $detected += 'cargo' }
if (Test-Path (Join-Path $Cwd 'go.mod'))          { $detected += 'go-mod' }
if (Get-ChildItem -Path $Cwd -Filter '*.csproj' -File -Recurse -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 1) {
  $detected += 'dotnet'
}
if (Get-ChildItem -Path $Cwd -Filter 'pubspec.yaml' -File -Recurse -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 1) {
  $detected += 'flutter-pub'
}

$dir = Join-Path $env:USERPROFILE ".cluadex\logs"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$line = if ($detected.Count -gt 0) { ($detected -join ',') } else { 'unknown' }
Set-Content -Path (Join-Path $dir "last-package-manager.txt") -Value "$line`n# detected at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n# cwd: $Cwd"
exit 0
