# Hook: pretool-pre-commit-quality
# Phase: PreToolUse / matcher run_command
# Before `git commit`, refuses if .env / *.key / *.pem are staged.
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
  Write-Error ("Refusing commit — sensitive files staged: " + (($bad | Select-Object -Unique) -join ', '))
  exit 1
}
exit 0
