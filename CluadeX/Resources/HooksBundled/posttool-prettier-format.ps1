# Hook: posttool-prettier-format
# Phase: PostToolUse / matcher write_file
# After a write, run prettier --write on the file if prettier is installed and
# the file extension is supported. Best-effort, never blocks.
param([string]$Path = "")

if (-not $Path -or -not (Test-Path $Path -PathType Leaf)) { exit 0 }

$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
$ok = @('.js', '.jsx', '.ts', '.tsx', '.css', '.scss', '.less', '.html', '.json', '.md', '.yaml', '.yml', '.vue', '.svelte')
if ($ok -notcontains $ext) { exit 0 }

# Skip if prettier not on PATH
$prettier = Get-Command prettier -ErrorAction SilentlyContinue
if (-not $prettier) {
  $npx = Get-Command npx -ErrorAction SilentlyContinue
  if (-not $npx) { exit 0 }
  try { & npx --no-install prettier --write $Path 2>$null | Out-Null } catch { }
} else {
  try { & prettier --write $Path 2>$null | Out-Null } catch { }
}
exit 0
