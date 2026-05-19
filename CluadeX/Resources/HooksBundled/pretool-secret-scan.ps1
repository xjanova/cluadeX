# Hook: pretool-secret-scan
# Phase: PreToolUse / matcher write_file
# Scans the *target file path* (after write — peek the new contents from the
# tool args JSON) for common secret patterns. Blocks the write if any hit.
# This is best-effort: we cannot see the new content before the write happens
# unless it's in {arguments}, so we scan that JSON blob.
param(
  [string]$Path = "",
  [string]$Arguments = "{}"
)

$ErrorActionPreference = "Stop"

# Skip binary-ish extensions outright
$binExt = '.png|.jpg|.jpeg|.gif|.ico|.pdf|.zip|.exe|.dll|.gguf|.bin'
if ($Path -match $binExt) { exit 0 }

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
  Write-Error ("Secret pattern(s) detected in write target — blocked: " + ($findings -join ', '))
  exit 1
}
exit 0
