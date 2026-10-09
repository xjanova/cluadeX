# Hook: pretool-block-dev-server
# Phase: PreToolUse / matcher run_command|task_create|powershell|run_build|run_tests
# Blocks commands that spawn a long-running dev server outside the active workspace.
# Exit 0 = allow, exit 2 = block (never mistaken for a broken script). The refusal never
# quotes the (model-written) command.
param(
  [string]$Command = "",
  [string]$Cwd = ""
)

$ErrorActionPreference = "Stop"

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

$cmd = $Command.ToLowerInvariant()

$patterns = @(
  'npm run dev', 'pnpm dev', 'yarn dev', 'bun dev',
  'next dev', 'vite', 'nuxt dev', 'astro dev',
  'flask run', 'rails server', 'rails s',
  'python -m http.server', 'python3 -m http.server',
  'dotnet watch run', 'live-server'
)

foreach ($p in $patterns) {
  if ($cmd -match [regex]::Escape($p)) {
    if (-not $Cwd -or -not (Test-Path -LiteralPath $Cwd)) {
      # Not Write-Error: under $ErrorActionPreference = "Stop" it throws, and the script would end
      # with exit 1 instead of the deliberate-block code.
      [Console]::Error.WriteLine("Dev-server command (" + $p + ") detected but no workspace folder is open - blocked.")
      exit 2
    }
    # Allowed — let it run, but log it
    Write-Output "[allow] dev-server command in workspace $Cwd"
    exit 0
  }
}

# Not a dev-server command — silently allow
exit 0
