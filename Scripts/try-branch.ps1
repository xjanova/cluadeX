<#
.SYNOPSIS
  Build a branch of CluadeX and put a trial copy on the desktop - no release, no installer.

.DESCRIPTION
  1. Finds the branch's worktree (or makes one under D:\Code\ClaudeClient-worktrees\try-<name>).
  2. dotnet build -c Debug (about a minute; seconds when little changed).
  3. Copies the build to D:\CluadeX-Try\<name>\app with portable.txt + trial.txt next to the exe:
     its own data folder (app\Data), and it never writes outside it - no BrainX bridge or Chrome host
     re-pointed at itself, no self-update, no CluadeX.lnk. The installed CluadeX and its icon stay as they are.
  4. First time only: copies settings.json (keys, license, choices) and mcp_servers.json from ~\.cluadex,
     so the trial behaves like your CluadeX. Your real ~\.cluadex is only read, never written.
  5. Makes a separate desktop shortcut "CluadeX ตัวลอง - <name>.lnk" (never CluadeX.lnk).
  It never starts the app: open the shortcut yourself. Run it again after more changes to rebuild the same trial.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Scripts\try-branch.ps1 feat/voice-queue-player
  powershell -ExecutionPolicy Bypass -File Scripts\try-branch.ps1 feat/voice-queue-player -Remove
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Branch,
    [string]$Repo = 'D:\Code\ClaudeClient',
    [string]$TryRoot = 'D:\CluadeX-Try',
    [switch]$FreshData,   # start the trial with empty settings instead of a copy of yours
    [switch]$Remove       # delete this trial: its folder, its shortcut and the worktree this script made
)
$ErrorActionPreference = 'Stop'

function Say($text) { Write-Host $text }
function Fail($text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

if ($TryRoot -match '^[Ee]:' -or $Repo -match '^[Ee]:') { Fail 'E: is the USB disk - use D:.' }

$name = ($Branch -replace '^(origin/)', '' -replace '[^A-Za-z0-9._-]', '-').Trim('-')
$trialDir = Join-Path $TryRoot $name
$appDir = Join-Path $trialDir 'app'
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$link = Join-Path $desktop ("CluadeX ตัวลอง - $name.lnk")
$ownWorktree = Join-Path (Split-Path $Repo -Parent) ("ClaudeClient-worktrees\try-$name")

function Find-Worktree([string]$branch) {
    $current = $null
    foreach ($line in (git -C $Repo worktree list --porcelain)) {
        if ($line -like 'worktree *') { $current = $line.Substring(9) }
        elseif ($line -eq "branch refs/heads/$branch") { return $current }
    }
    return $null
}

if ($Remove) {
    if (Get-Process -Name CluadeX -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) }) {
        Fail "the trial is still open - close it first."
    }
    if (Test-Path $link) { Remove-Item -LiteralPath $link -Force; Say "removed shortcut: $link" }
    if (Test-Path $trialDir) { [IO.Directory]::Delete("\\?\$trialDir", $true); Say "removed trial folder: $trialDir" }
    if (Test-Path $ownWorktree) { git -C $Repo worktree remove --force $ownWorktree; Say "removed worktree: $ownWorktree" }
    exit 0
}

# 1. the branch's source
git -C $Repo fetch -q origin
$src = Find-Worktree $Branch
if (-not $src) {
    $hasLocal = (git -C $Repo branch --list $Branch)
    if ($hasLocal) { git -C $Repo worktree add -q $ownWorktree $Branch }
    else {
        git -C $Repo show-ref --verify --quiet "refs/remotes/origin/$Branch"
        if ($LASTEXITCODE -ne 0) { Fail "no branch '$Branch' here or on origin." }
        git -C $Repo worktree add -q --track -b $Branch $ownWorktree "origin/$Branch"
    }
    if ($LASTEXITCODE -ne 0) { Fail "could not make a worktree for $Branch." }
    $src = $ownWorktree
}
$src = $src -replace '/', '\'
Say "source : $src ($(git -C $src log -1 --format='%h %s'))"

# A branch from before trial mode would re-point the installed CluadeX's connections at the copy - refuse it.
$settingsCs = Join-Path $src 'CluadeX\Services\SettingsService.cs'
if (-not (Select-String -LiteralPath $settingsCs -Pattern 'IsTrialCopy' -Quiet)) {
    Fail "$Branch is older than trial mode (v3.0.306). Merge main into it first, or the copy could take over the installed CluadeX's connections."
}

# 2. build
$trialOpen = Get-Process -Name CluadeX -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) }
if ($trialOpen) { Fail "this trial is open - close it, then run again." }
Say 'build  : dotnet build -c Debug ...'
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet build (Join-Path $src 'CluadeX\CluadeX.csproj') -c Debug -v q -nologo -clp:ErrorsOnly | Out-Host
if ($LASTEXITCODE -ne 0) { Fail 'the build failed - see above.' }
$out = Join-Path $src 'CluadeX\bin\DebugBuild\net8.0-windows'
if (-not (Test-Path (Join-Path $out 'CluadeX.exe'))) { Fail "no CluadeX.exe in $out" }
Say ("build  : done in {0:N0} s" -f $sw.Elapsed.TotalSeconds)

# 3. copy next to its own data (Data is never mirrored away)
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
& robocopy $out $appDir /MIR /XD Data /XF portable.txt trial.txt /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "copying the build failed (robocopy $LASTEXITCODE)." }
Set-Content -LiteralPath (Join-Path $appDir 'portable.txt') -Value 'portable' -Encoding ascii
Set-Content -LiteralPath (Join-Path $appDir 'trial.txt') -Value "trial copy of $Branch - see Scripts\try-branch.ps1" -Encoding ascii

# 4. first run: your settings, read-only copy
$data = Join-Path $appDir 'Data'
$real = Join-Path $env:USERPROFILE '.cluadex'
if (-not (Test-Path (Join-Path $data 'settings.json')) -and -not $FreshData -and (Test-Path (Join-Path $real 'settings.json'))) {
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    foreach ($f in 'settings.json', 'mcp_servers.json') {
        if (Test-Path (Join-Path $real $f)) { Copy-Item -LiteralPath (Join-Path $real $f) -Destination (Join-Path $data $f) }
    }
    Say "data   : copied your settings.json + mcp_servers.json into $data (your ~\.cluadex is not touched)"
} else { Say "data   : $data (kept from the last run)" }

# 5. its own shortcut - CluadeX.lnk is the installed app's and is never written
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($link)
$sc.TargetPath = Join-Path $appDir 'CluadeX.exe'
$sc.WorkingDirectory = $appDir
$sc.IconLocation = (Join-Path $appDir 'CluadeX.exe') + ',0'
$sc.Description = "CluadeX trial of $Branch (its own data, does not touch the installed CluadeX)"
$sc.Save()
Say "ready  : $link"
Say "         open it yourself; the title bar shows 'ตัวลอง'. Remove with: -Remove"
