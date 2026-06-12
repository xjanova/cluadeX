# Hook: posttool-pr-link-logger
# Phase: PostToolUse / matcher run_command
# After a successful `git push`, logs branch & remote.
# If `gh pr view --json url -q .url` works, also logs the PR URL.
param(
  [string]$Command = "",
  [string]$Cwd = ""
)

if (-not ($Command.ToLowerInvariant().Trim().StartsWith('git push'))) { exit 0 }
if (-not $Cwd -or -not (Test-Path (Join-Path $Cwd '.git'))) { exit 0 }

try {
  Push-Location $Cwd
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
