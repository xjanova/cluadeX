# Hook: pretool-secret-scan
# Phase: PreToolUse / matcher write_file|edit_file|multi_edit
# Scans the content about to be written (write_file content, edit_file/multi_edit
# replacement text) for common secret patterns. Blocks the write if any hit.
#
# Input comes from the CluadeX hook contract, NOT the command line:
#   CLUADEX_HOOK_PAYLOAD_FILE  JSON file with every tool argument (UTF-8)
#   CLUADEX_HOOK_PATH          the target path
# The old version received the whole argument JSON as -Arguments on the command
# line; past cmd.exe's 8191-char limit that failed with "The command line is too
# long." (exit 1), which blocked every write larger than ~8 KB.
param(
  [string]$Path = "",
  [string]$Arguments = "{}"
)

$ErrorActionPreference = "Stop"

if ($env:CLUADEX_HOOK_PATH) { $Path = $env:CLUADEX_HOOK_PATH }

# Skip binary-ish extensions outright
$binExt = '\.(png|jpg|jpeg|gif|ico|pdf|zip|exe|dll|gguf|bin)$'
if ($Path -match $binExt) { exit 0 }

# Decoded argument VALUES, not raw JSON: in JSON a quote is \" and a newline is \n,
# so patterns like  password = "..."  never matched the escaped text. multi_edit's
# "edits" (itself a JSON list) is decoded too. The text being REPLACED ("find") is not
# scanned: an edit that removes a leaked key from the code must not be blocked for it.
$skipNames = @('find', 'old_string', 'old_text', 'search')
$script:values = @()
function Add-Values([string]$name, $v) {
  if ($null -eq $v) { return }
  if ($skipNames -contains $name) { return }
  if ($v -is [string]) {
    if ($name -eq 'edits') {
      try { Add-Values '' ($v | ConvertFrom-Json); return } catch { }
    }
    $script:values += $v
    return
  }
  if ($v -is [System.Management.Automation.PSCustomObject]) {
    foreach ($p in $v.PSObject.Properties) { Add-Values $p.Name $p.Value }
    return
  }
  if ($v -is [System.Collections.IEnumerable]) {
    foreach ($x in $v) { Add-Values '' $x }
    return
  }
  $script:values += [string]$v
}

$payloadFile = $env:CLUADEX_HOOK_PAYLOAD_FILE
if ($payloadFile -and (Test-Path -LiteralPath $payloadFile)) {
  $raw = [System.IO.File]::ReadAllText($payloadFile, [System.Text.Encoding]::UTF8)
  try {
    $obj = $raw | ConvertFrom-Json
    foreach ($p in $obj.PSObject.Properties) { Add-Values $p.Name $p.Value }
  } catch {
    $script:values += $raw
  }
} else {
  $script:values += $Arguments
}
$Arguments = ($script:values -join "`n")

# Patterns
$rules = @(
  @{ Name = 'AWS Access Key';      Re = 'AKIA[0-9A-Z]{16}' },
  @{ Name = 'AWS Secret Key';      Re = '(?i)aws_secret_access_key\s*[:=]\s*["'']?[a-z0-9/+=]{40}' },
  @{ Name = 'GitHub Token';        Re = 'ghp_[a-zA-Z0-9]{36}|gho_[a-zA-Z0-9]{36}|github_pat_[A-Za-z0-9_]{82}' },
  @{ Name = 'Anthropic API Key';   Re = 'sk-ant-[A-Za-z0-9_\-]{30,}' },
  @{ Name = 'OpenAI API Key';      Re = 'sk-[A-Za-z0-9]{32,}' },
  @{ Name = 'Google API Key';      Re = 'AIza[0-9A-Za-z\-_]{35}' },
  @{ Name = 'Slack Token';         Re = 'xox[bpoa]-[a-zA-Z0-9\-]{10,}' },
  @{ Name = 'JWT';                 Re = 'eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}' },
  @{ Name = 'Private Key Block';   Re = '-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----' },
  @{ Name = 'Generic high-entropy';Re = '(?i)(secret|password|api[_-]?key|token)\s*[:=]\s*["''][A-Za-z0-9+/]{32,}["'']' }
)

$blob = "$Arguments`n$Path"
$findings = @()
foreach ($r in $rules) {
  $m = [regex]::Matches($blob, $r.Re)
  if ($m.Count -gt 0) { $findings += $r.Name }
}

if ($findings.Count -gt 0) {
  Write-Error ("Secret pattern(s) detected in write target - blocked: " + ($findings -join ', '))
  exit 1
}
exit 0
