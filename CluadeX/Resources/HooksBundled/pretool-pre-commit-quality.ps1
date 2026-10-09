# Hook: pretool-pre-commit-quality
# Phase: PreToolUse / matcher run_command|task_create|powershell|run_build|run_tests
# Before `git commit`, refuses if .env / *.key / *.pem are staged.
# Exit 0 = allow, exit 2 = block. The refusal gives a count, not the file names: names come
# from the repository, and stderr must never carry text an attacker chose.
param(
  [string]$Command = "",
  [string]$Cwd = ""
)

# Exact values from the CluadeX hook contract (no command-line quoting; see HookService).
# run_command / task_create / run_build / run_tests send "command"; powershell sends "command" or "script".
if ($env:CLUADEX_HOOK_CWD) { $Cwd = $env:CLUADEX_HOOK_CWD }
if ($env:CLUADEX_HOOK_PAYLOAD_FILE -and (Test-Path -LiteralPath $env:CLUADEX_HOOK_PAYLOAD_FILE)) {
  try {
    $payload = [System.IO.File]::ReadAllText($env:CLUADEX_HOOK_PAYLOAD_FILE, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($payload.command) { $Command = [string]$payload.command }
    elseif ($payload.script) { $Command = [string]$payload.script }
  } catch { }
}

if (-not ($Command.ToLowerInvariant().Trim().StartsWith("git commit"))) { exit 0 }
if (-not $Cwd -or -not (Test-Path -LiteralPath (Join-Path $Cwd ".git"))) { exit 0 }

try {
  Push-Location -LiteralPath $Cwd
  $staged = git diff --cached --name-only 2>$null
  Pop-Location
} catch { exit 0 }

$bad = @()
$blockExt = @('.env', '.key', '.pem', '.pfx', '.p12')
$blockName = @('id_rsa', 'id_ed25519', 'credentials.json', 'service-account.json')

foreach ($f in $staged) {
  if (-not $f) { continue }
  $fname = [System.IO.Path]::GetFileName($f).ToLowerInvariant()
  foreach ($ext in $blockExt) {
    if ($fname.EndsWith($ext) -and $fname -ne '.env.example') { $bad += $f }
  }
  foreach ($n in $blockName) { if ($fname -eq $n) { $bad += $f } }
}

if ($bad.Count -gt 0) {
  $n = @($bad | Select-Object -Unique).Count
  [Console]::Error.WriteLine("Refusing commit: " + $n + " sensitive file(s) staged (.env / .key / .pem / .pfx / .p12 / private keys / credentials). Unstage them first - 'git diff --cached --name-only' lists them.")
  exit 2
}
exit 0
