# Hook: stop-pattern-extract
# Phase: Stop
# Appends a JSONL row to ~/.cluadex/instincts-inbox.jsonl that InstinctService
# can later ingest as an observation. v1 just records the end-of-turn fact;
# v2 will mine the transcript for actual patterns.
param(
  [string]$Model = "",
  [string]$Cost = "0",
  [string]$Tokens = "0",
  [string]$Turn_count = "0"
)

$dir = Join-Path $env:USERPROFILE ".cluadex"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$inbox = Join-Path $dir "instincts-inbox.jsonl"

$row = [ordered]@{
  ts        = (Get-Date).ToString("o")
  model     = $Model
  costUsd   = [double]$Cost
  tokens    = [int]$Tokens
  turns     = [int]$Turn_count
  source    = "stop-hook-v1"
}
($row | ConvertTo-Json -Compress) | Add-Content -Path $inbox
exit 0
