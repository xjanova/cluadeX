# Hook: pretool-dangerous-cmd-warn
# Phase: PreToolUse / matcher run_command|task_create|powershell|run_build|run_tests
# Refuses obviously destructive commands.
# Exit 0 = allow, exit 2 = block (CluadeX/Claude Code contract: 2 is never mistaken for a
# broken script). The refusal names the rule, NEVER the command: the command is written by
# the model, and stderr text that looked like a PowerShell error once turned a block into an allow.
param([string]$Command = "")

# The exact command from the CluadeX hook contract. Through the command line it arrived
# with $ and % stripped ("rm -rf $HOME" reached this script as "rm -rf HOME" and slipped
# past the pattern below), and long commands could not be passed at all.
# run_command / task_create / run_build / run_tests send "command"; powershell sends "command" or "script".
if ($env:CLUADEX_HOOK_PAYLOAD_FILE -and (Test-Path -LiteralPath $env:CLUADEX_HOOK_PAYLOAD_FILE)) {
  try {
    $payload = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($payload.command) { $Command = [string]$payload.command }
    elseif ($payload.script) { $Command = [string]$payload.script }
  } catch { }
}

$cmd = $Command.ToLowerInvariant().Trim()

$rules = @(
  @{ Name = 'rm -rf /';                  Re = 'rm\s+-rf\s+[/\\]\s*$' },
  @{ Name = 'rm -rf /';                  Re = 'rm\s+-rf\s+[/\\]\s+' },
  @{ Name = 'rm -rf ~';                  Re = 'rm\s+-rf\s+~\s*$' },
  @{ Name = 'rm -rf $HOME';              Re = 'rm\s+-rf\s+\$home' },
  @{ Name = 'del /s c:\';                Re = 'del\s+/[sf]\s+c:\\' },
  @{ Name = 'format c:';                 Re = 'format\s+c:' },
  @{ Name = 'Format-Volume';             Re = 'format-volume' },
  @{ Name = 'Clear-Disk';                Re = 'clear-disk' },
  @{ Name = 'mkfs';                      Re = 'mkfs' },
  @{ Name = 'dd to a disk device';       Re = 'dd\s+if=.*of=/dev/[sh]d' },
  @{ Name = 'drop database/table';       Re = 'drop\s+(database|table)\s+' },
  @{ Name = 'fork bomb';                 Re = ':\(\)\{\s*:\|:&\s*\};:' },
  @{ Name = 'write to /dev/sda';         Re = '>\s*/dev/sda' },
  @{ Name = 'git push --force --no-verify'; Re = 'git\s+push\s+.*--force\s+.*--no-verify' },
  @{ Name = 'shutdown';                  Re = 'shutdown\s+/[fs]' },
  @{ Name = 'reboot now';                Re = 'reboot\s+now' }
)

foreach ($r in $rules) {
  if ($cmd -match $r.Re) {
    [Console]::Error.WriteLine("Refusing dangerous command (rule: " + $r.Name + ").")
    exit 2
  }
}

exit 0
