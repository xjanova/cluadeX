# Hook: pretool-dangerous-cmd-warn
# Phase: PreToolUse / matcher run_command
# Refuses obviously destructive commands.
param([string]$Command = "")

# The exact command from the CluadeX hook contract. Through the command line it arrived
# with $ and % stripped ("rm -rf $HOME" reached this script as "rm -rf HOME" and slipped
# past the pattern below), and long commands could not be passed at all.
if ($env:CLUADEX_HOOK_PAYLOAD_FILE -and (Test-Path -LiteralPath $env:CLUADEX_HOOK_PAYLOAD_FILE)) {
  try {
    $payload = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($payload.command) { $Command = [string]$payload.command }
  } catch { }
}

$cmd = $Command.ToLowerInvariant().Trim()

$patterns = @(
  'rm\s+-rf\s+[/\\]\s*$',
  'rm\s+-rf\s+[/\\]\s+',
  'rm\s+-rf\s+~\s*$',
  'rm\s+-rf\s+\$home',
  'del\s+/[sf]\s+c:\\',
  'format\s+c:',
  'mkfs',
  'dd\s+if=.*of=/dev/[sh]d',
  'drop\s+(database|table)\s+',
  ':\(\)\{\s*:\|:&\s*\};:',          # fork bomb
  '>\s*/dev/sda',
  'git\s+push\s+.*--force\s+.*--no-verify',
  'shutdown\s+/[fs]',
  'reboot\s+now'
)

foreach ($p in $patterns) {
  if ($cmd -match $p) {
    Write-Error "Refusing dangerous command: $Command"
    exit 1
  }
}

exit 0
