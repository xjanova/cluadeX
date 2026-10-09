# Hook: pretool-git-push-confirm
# Phase: PreToolUse / matcher run_command|task_create|powershell|run_build|run_tests
# Blocks `git push` to main/master/production unless an override marker is present.
# Override: create file ~/.cluadex/allow-push-main OR include "--force-confirmed" in the command.
# Exit 0 = allow, exit 2 = block. The refusal never quotes the (model-written) command.
param([string]$Command = "")

# The exact command from the CluadeX hook contract (no command-line quoting; see HookService).
# run_command / task_create / run_build / run_tests send "command"; powershell sends "command" or "script".
if ($env:CLUADEX_HOOK_PAYLOAD_FILE -and (Test-Path -LiteralPath $env:CLUADEX_HOOK_PAYLOAD_FILE)) {
  try {
    $payload = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($payload.command) { $Command = [string]$payload.command }
    elseif ($payload.script) { $Command = [string]$payload.script }
  } catch { }
}

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

[Console]::Error.WriteLine("Push to a protected branch (main / master / production / prod / release) blocked. Add '--force-confirmed' to the command or create ~/.cluadex/allow-push-main to proceed.")
exit 2
