# Hook: stop-cost-summary-toast
# Phase: Stop
# Appends a CSV row with this session's cost summary.
param(
  [string]$Model = "",
  [string]$Cost = "0",
  [string]$Tokens = "0",
  [string]$Turn_count = "0"
)

$dir = Join-Path $env:USERPROFILE ".cluadex\logs"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$csv = Join-Path $dir "session-costs.csv"

if (-not (Test-Path $csv)) {
  "timestamp,model,cost_usd,tokens,turns" | Set-Content -Path $csv
}
"$((Get-Date).ToString('o')),$Model,$Cost,$Tokens,$Turn_count" | Add-Content -Path $csv
exit 0
