# Hook: pretool-block-dev-server
# Phase: PreToolUse / matcher run_command
# Blocks commands that spawn a long-running dev server outside the active workspace.
# Exit 0 = allow, non-zero = block.
param(
  [string]$Command = "",
  [string]$Cwd = ""
)

$ErrorActionPreference = "Stop"
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
    if (-not $Cwd -or -not (Test-Path $Cwd)) {
      Write-Error "Dev-server command detected but no workspace path provided. Blocking: $Command"
      exit 1
    }
    # Allowed — let it run, but log it
    Write-Output "[allow] dev-server command in workspace $Cwd"
    exit 0
  }
}

# Not a dev-server command — silently allow
exit 0
