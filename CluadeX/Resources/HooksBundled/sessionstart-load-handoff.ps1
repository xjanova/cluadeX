# Hook: sessionstart-load-handoff
# Phase: SessionStart
# Copies the most recent Notes/Claude-Sessions/handoff-*.md found under {cwd}
# to ~/.cluadex/logs/last-handoff.md so the AI can read it on next prompt.
param([string]$Cwd = "")

if (-not $Cwd -or -not (Test-Path $Cwd)) { exit 0 }

$candidates = @()
$searchRoots = @(
  Join-Path $Cwd "Notes\Claude-Sessions",
  Join-Path $Cwd ".claude\sessions",
  Join-Path $Cwd "docs\sessions"
)
foreach ($r in $searchRoots) {
  if (Test-Path $r) {
    $candidates += Get-ChildItem -Path $r -Filter "*handoff*.md" -File -ErrorAction SilentlyContinue
  }
}

if ($candidates.Count -eq 0) { exit 0 }
$latest = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1

$dst = Join-Path $env:USERPROFILE ".cluadex\logs"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Path $latest.FullName -Destination (Join-Path $dst "last-handoff.md") -Force

Write-Output "[ok] loaded handoff: $($latest.Name)"
exit 0
