# Hook: posttool-quality-gate
# Phase: PostToolUse / matcher write_file
# After C# file write, runs `dotnet build --no-restore -nologo` and logs result.
param(
  [string]$Path = "",
  [string]$Cwd = ""
)

if (-not $Path) { exit 0 }
$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
if ($ext -notin @('.cs', '.csproj', '.props', '.targets')) { exit 0 }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { exit 0 }

# Use $Cwd if a .csproj/.sln lives there; otherwise walk up from $Path
$root = if ($Cwd -and (Test-Path $Cwd)) { $Cwd } else { Split-Path $Path -Parent }

try {
  Push-Location $root
  $output = & dotnet build --no-restore -nologo -v quiet 2>&1
  $exit = $LASTEXITCODE
  Pop-Location
} catch { exit 0 }

$logDir = Join-Path $env:USERPROFILE ".cluadex\logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$status = if ($exit -eq 0) { 'OK' } else { 'FAIL' }
$line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $status root=$root file=$Path"
Add-Content -Path (Join-Path $logDir "quality-gate.log") -Value $line

if ($exit -ne 0) {
  $errLog = Join-Path $logDir "quality-gate-errors.log"
  Add-Content -Path $errLog -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Path`n$($output -join "`n")`n---"
  Write-Output "[warn] dotnet build failed — see $errLog"
}
exit 0
