# Hook: posttool-console-log-warn
# Phase: PostToolUse / matcher write_file
# Warns about forgotten console.log statements after a JS/TS write.
param([string]$Path = "")

if (-not $Path -or -not (Test-Path $Path -PathType Leaf)) { exit 0 }
$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
if ($ext -notin @('.js', '.jsx', '.ts', '.tsx', '.vue', '.svelte')) { exit 0 }

try {
  $content = Get-Content $Path -Raw
} catch { exit 0 }

# Strip comments & strings (best-effort) before searching
$matches = [regex]::Matches($content, '(?<![\w\.])console\.(log|debug|info|warn|error)\s*\(')

if ($matches.Count -gt 0) {
  $logDir = Join-Path $env:USERPROFILE ".cluadex\logs"
  New-Item -ItemType Directory -Force -Path $logDir | Out-Null
  $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Path — $($matches.Count) console.* call(s)"
  Add-Content -Path (Join-Path $logDir "console-log.log") -Value $line
  Write-Output "[warn] $($matches.Count) console.* call(s) in $Path"
}
exit 0
