# Hook: precompact-save-snapshot
# Phase: PreCompact
# Writes a marker file noting that compaction is about to run on a session of
# this size. The actual chat content is saved by CluadeX itself; this hook
# gives a paper trail outside the app.
param(
  [string]$Message_count = "0",
  [string]$Token_estimate = "0"
)

$dir = Join-Path $env:USERPROFILE ".cluadex\logs\compaction-snapshots"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$marker = Join-Path $dir "compact-$stamp.txt"

@(
  "timestamp:        $((Get-Date).ToString('o'))"
  "message_count:    $Message_count"
  "token_estimate:   $Token_estimate"
  "trigger:          PreCompact hook"
) | Set-Content -Path $marker

exit 0
