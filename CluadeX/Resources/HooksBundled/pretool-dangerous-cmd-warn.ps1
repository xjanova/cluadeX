# Hook: pretool-dangerous-cmd-warn
# Phase: PreToolUse / matcher run_command
# Refuses obviously destructive commands.
param([string]$Command = "")

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
