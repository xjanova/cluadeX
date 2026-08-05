# Hook: pretool-git-push-confirm
# Phase: PreToolUse / matcher run_command
# Blocks `git push` to main/master/production unless an override marker is present.
# Override: create file ~/.cluadex/allow-push-main OR include "--force-confirmed" in the command.
param([string]$Command = "")

$cmd = $Command.ToLowerInvariant().Trim()
if (-not $cmd.StartsWith("git push")) { exit 0 }

$danger = @('main', 'master', 'production', 'prod', 'release')
$hit = $false
foreach ($b in $danger) {
  if ($cmd -match "\b$b\b") { $hit = $true; break }
}
if (-not $hit) { exit 0 }

if ($cmd -match '--force-confirmed') {
  Write-Output "[allow] explicit --force-confirmed token present"
  exit 0
}

$marker = Join-Path $env:USERPROFILE ".cluadex\allow-push-main"
if (Test-Path $marker) {
  Write-Output "[allow] marker file ~/.cluadex/allow-push-main present"
  exit 0
}

Write-Error "Push to protected branch blocked. Add '--force-confirmed' or touch ~/.cluadex/allow-push-main to proceed: $Command"
exit 1
