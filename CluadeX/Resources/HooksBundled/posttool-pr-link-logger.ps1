# Hook: posttool-pr-link-logger
# Phase: PostToolUse / matcher run_command
# After a successful `git push`, logs branch & remote.
# If `gh pr view --json url -q .url` works, also logs the PR URL.
param(
  [string]$Command = "",
  [string]$Cwd = ""
)

# Exact values from the CluadeX hook contract (no command-line quoting; see HookService).
if ($env:CLUADEX_HOOK_CWD) { $Cwd = $env:CLUADEX_HOOK_CWD }
if ($env:CLUADEX_HOOK_PAYLOAD_FILE -and (Test-Path -LiteralPath $env:CLUADEX_HOOK_PAYLOAD_FILE)) {
  try {
    $payload = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($payload.command) { $Command = [string]$payload.command }
  } catch { }
}

if (-not ($Command.ToLowerInvariant().Trim().StartsWith('git push'))) { exit 0 }
if (-not $Cwd -or -not (Test-Path -LiteralPath (Join-Path $Cwd '.git'))) { exit 0 }

try {
  Push-Location -LiteralPath $Cwd
  $branch = (git rev-parse --abbrev-ref HEAD 2>$null).Trim()
  $remote = (git config --get remote.origin.url 2>$null).Trim()
  $prUrl = ""
  $gh = Get-Command gh -ErrorAction SilentlyContinue
  if ($gh) {
    try { $prUrl = (gh pr view --json url -q .url 2>$null).Trim() } catch { }
  }
  Pop-Location
} catch { exit 0 }

$logDir = Join-Path $env:USERPROFILE ".cluadex\logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`tbranch=$branch`tremote=$remote`tpr=$prUrl"
Add-Content -Path (Join-Path $logDir "pr-links.log") -Value $line
exit 0
