# Hook: posttool-typescript-check
# Phase: PostToolUse / matcher write_file
# Type-check a .ts/.tsx file after writing. Logs errors to debug log.
param([string]$Path = "")

if (-not $Path -or -not (Test-Path $Path -PathType Leaf)) { exit 0 }
$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
if ($ext -notin @('.ts', '.tsx')) { exit 0 }

$tsc = Get-Command tsc -ErrorAction SilentlyContinue
if (-not $tsc) {
  $npx = Get-Command npx -ErrorAction SilentlyContinue
  if (-not $npx) { exit 0 }
  $out = & npx --no-install tsc --noEmit --pretty false $Path 2>&1
} else {
  $out = & tsc --noEmit --pretty false $Path 2>&1
}

if ($LASTEXITCODE -ne 0) {
  $logDir = Join-Path $env:USERPROFILE ".cluadex\logs"
  New-Item -ItemType Directory -Force -Path $logDir | Out-Null
  $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') tsc errors in $Path`n$out`n---"
  Add-Content -Path (Join-Path $logDir "tsc-errors.log") -Value $line
  Write-Output "[warn] tsc reported errors — see ~/.cluadex/logs/tsc-errors.log"
}
exit 0
