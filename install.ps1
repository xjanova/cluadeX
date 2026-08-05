<#
.SYNOPSIS
    CluadeX bootstrap installer — downloads the latest release, installs it, registers it for discovery,
    and launches it. Designed so an orchestrator (BrainX) can bring CluadeX up on a machine that doesn't
    have it yet, then coordinate over the named pipe (\\.\pipe\cluadex-mcp).

.DESCRIPTION
    One-liner install (PowerShell):
        irm https://raw.githubusercontent.com/xjanova/cluadeX/main/install.ps1 | iex

    Silent / no-launch (for a launcher to call):
        powershell -ExecutionPolicy Bypass -File install.ps1 -NoLaunch

    After install, CluadeX writes ~/.cluadex/install.json (exe path + pipe name + token file) and this
    script writes HKCU\Software\CluadeX\InstallPath — either is enough for a launcher to find + start it.

.PARAMETER InstallDir
    Where to install. Default: %LOCALAPPDATA%\Programs\CluadeX (per-user, no admin required).

.PARAMETER NoLaunch
    Install only; don't start CluadeX afterwards.

.PARAMETER NoShortcut
    Skip creating the Start-menu shortcut.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\CluadeX"),
    [switch]$NoLaunch,
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"
$ProgressPreference   = "SilentlyContinue"   # makes Invoke-WebRequest fast (no per-chunk progress redraw)

$Repo  = "xjanova/cluadeX"
$Asset = "CluadeX-win-x64.zip"

function Write-Step($msg) { Write-Host "  $msg" -ForegroundColor Gray }

Write-Host "CluadeX installer" -ForegroundColor Cyan

# 1. Resolve the latest release's download URL from the GitHub API.
$headers = @{ "User-Agent" = "CluadeX-Installer"; "Accept" = "application/vnd.github+json" }
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}
catch {
    throw "Could not reach the GitHub releases API for $Repo. $($_.Exception.Message)"
}

$dlUrl = ($release.assets | Where-Object { $_.name -eq $Asset } | Select-Object -First 1).browser_download_url
if (-not $dlUrl) {
    # Fall back to the first .zip asset if the exact name changed.
    $dlUrl = ($release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1).browser_download_url
}
if (-not $dlUrl) { throw "No downloadable .zip asset found in the latest release of $Repo." }
Write-Step "Latest version: $($release.tag_name)"

# 2. Download to a temp file.
$tmpZip = Join-Path $env:TEMP "CluadeX-download-$([System.Guid]::NewGuid().ToString('N')).zip"
Write-Step "Downloading $Asset ..."
Invoke-WebRequest -Uri $dlUrl -OutFile $tmpZip -UseBasicParsing -Headers @{ "User-Agent" = "CluadeX-Installer" }

# (Integrity note: the GitHub download is over HTTPS. A published SHA-256/code-signature check is the
#  next hardening step — tracked on the CluadeX side.)

# 3. Extract into the install dir (replace any existing install).
Write-Step "Installing to $InstallDir ..."
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Expand-Archive -Path $tmpZip -DestinationPath $InstallDir -Force
Remove-Item $tmpZip -Force

# 4. Locate CluadeX.exe (it may sit one folder deep inside the zip).
$exe = Get-ChildItem -Path $InstallDir -Filter "CluadeX.exe" -Recurse -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $exe) { throw "CluadeX.exe was not found after extraction in $InstallDir." }
$exePath = $exe.FullName

# 5. Record the install path so any launcher/orchestrator can discover it without guessing.
try {
    New-Item -Path "HKCU:\Software\CluadeX" -Force | Out-Null
    Set-ItemProperty -Path "HKCU:\Software\CluadeX" -Name "InstallPath" -Value $exePath
}
catch { Write-Step "(registry registration skipped: $($_.Exception.Message))" }

# 6. Start-menu shortcut (optional).
if (-not $NoShortcut) {
    try {
        $programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut((Join-Path $programs "CluadeX.lnk"))
        $lnk.TargetPath = $exePath
        $lnk.WorkingDirectory = (Split-Path $exePath)
        $lnk.Description = "CluadeX — AI coding assistant"
        $lnk.Save()
    }
    catch { Write-Step "(shortcut skipped: $($_.Exception.Message))" }
}

Write-Host "Installed: $exePath" -ForegroundColor Green

# 7. Launch (CluadeX then writes ~/.cluadex/install.json and starts its named-pipe MCP host).
if (-not $NoLaunch) {
    Write-Host "Launching CluadeX ..." -ForegroundColor Cyan
    Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath)
}
else {
    Write-Host "Run it any time:  `"$exePath`"" -ForegroundColor Gray
}
