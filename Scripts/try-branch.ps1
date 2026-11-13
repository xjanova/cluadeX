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
  It never starts the app unless -Smoke: open the shortcut yourself. Run it again after more changes to rebuild the same trial.
  -Smoke (what a Claude session runs when its work is done - the owner does not try it): no shortcut; opens the trial
     minimised, checks it started on its own data, that the installed CluadeX's connections, Chrome host, settings and
     CluadeX.lnk are untouched and the title bar says 'ตัวลอง', lists errors from its log, then closes it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Scripts\try-branch.ps1 feat/voice-queue-player
  powershell -ExecutionPolicy Bypass -File Scripts\try-branch.ps1 feat/voice-queue-player -Smoke
  powershell -ExecutionPolicy Bypass -File Scripts\try-branch.ps1 feat/voice-queue-player -Remove
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Branch,
    [string]$Repo = 'D:\Code\ClaudeClient',
    [string]$TryRoot = 'D:\CluadeX-Try',
    [switch]$FreshData,   # start the trial with empty settings instead of a copy of yours
    [switch]$Remove,      # delete this trial: its folder, its shortcut and the worktree this script made
    [switch]$Smoke,       # no shortcut: open the trial, check it, close it (exit 1 when a check fails)
    [int]$SmokeSeconds = 25
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
if (-not $Smoke) {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($link)
    $sc.TargetPath = Join-Path $appDir 'CluadeX.exe'
    $sc.WorkingDirectory = $appDir
    $sc.IconLocation = (Join-Path $appDir 'CluadeX.exe') + ',0'
    $sc.Description = "CluadeX trial of $Branch (its own data, does not touch the installed CluadeX)"
    $sc.Save()
    Say "ready  : $link"
    Say "         open it yourself; the title bar shows 'ตัวลอง'. Remove with: -Remove"
    exit 0
}

# 6. -Smoke: open it, check it, close it
$exe = Join-Path $appDir 'CluadeX.exe'
$marker = 'CluadeX-Try'
function FileHash($path) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { '-' } }
function Mentions($path) { (Test-Path -LiteralPath $path) -and ([IO.File]::ReadAllText($path) -match [regex]::Escape($marker)) }

# what the installed CluadeX owns outside its folder
$hostKey = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.xmanstudio.cluadex_chrome'
function HostManifest { try { (Get-ItemProperty -LiteralPath $hostKey -ErrorAction Stop).'(default)' } catch { $null } }
# the same host is registered for Microsoft Edge under its own key
$edgeHostKey = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.xmanstudio.cluadex_chrome'
function EdgeHostManifest { try { (Get-ItemProperty -LiteralPath $edgeHostKey -ErrorAction Stop).'(default)' } catch { $null } }
$vault = $null
foreach ($cfg in (Join-Path $env:USERPROFILE '.claude.json'), (Join-Path $real 'mcp_servers.json')) {
    if (-not $vault -and (Test-Path $cfg)) {
        $m = [regex]::Match([IO.File]::ReadAllText($cfg), '"BRAINX_VAULT"\s*:\s*"([^"]+)"')
        if ($m.Success) { $vault = $m.Groups[1].Value -replace '\\\\', '\' }
    }
}
$bridges = if ($vault) { Join-Path $vault '.obsidianx\mcp-bridges.json' } else { $null }
$links = @((Join-Path $desktop 'CluadeX.lnk'), (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'CluadeX.lnk'))
$before = @{ host = HostManifest; edgeHost = EdgeHostManifest; links = ($links | ForEach-Object { FileHash $_ }) -join ',' }

$results = New-Object System.Collections.Generic.List[object]
function Check([string]$what, [bool]$ok, [string]$detail = '') { $results.Add([pscustomobject]@{ ok = $ok; what = $what; detail = $detail }) }

Say "smoke  : opening $exe (minimised) for $SmokeSeconds s ..."
$started = Get-Date
$p = Start-Process -FilePath $exe -WorkingDirectory $appDir -WindowStyle Minimized -PassThru
$deadline = $started.AddSeconds($SmokeSeconds)
$hwnd = [IntPtr]::Zero
while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($hwnd -eq [IntPtr]::Zero -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle }
}
Check 'still running after the start-up' (-not $p.HasExited) $(if ($p.HasExited) { "exited with $($p.ExitCode)" } else { "pid $($p.Id)" })
Check 'main window opened' ($hwnd -ne [IntPtr]::Zero)

if (-not $p.HasExited -and $hwnd -ne [IntPtr]::Zero) {
    $p.Refresh()
    $hwnd = $p.MainWindowHandle   # the first handle seen can be the splash, gone by now
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $names = @($root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ })
        $chip = $names | Where-Object { $_ -like '*ตัวลอง*' } | Select-Object -First 1
        Check "version chip says 'ตัวลอง'" ([bool]$chip) $(if ($chip) { $chip } else { "$($names.Count) names read, none with it" })
    } catch { Check "version chip says 'ตัวลอง'" $false "UI Automation: $($_.Exception.Message)" }
}

$installJson = Join-Path $data 'install.json'
Check 'runs on its own data (app\Data\install.json names this exe)' ((Test-Path $installJson) -and ([IO.File]::ReadAllText($installJson) -match [regex]::Escape($marker)))
Check 'installed CluadeX data untouched (~\.cluadex has no trial path)' (-not ((Mentions (Join-Path $real 'install.json')) -or (Mentions (Join-Path $real 'settings.json')) -or (Mentions (Join-Path $real 'mcp_servers.json'))))
if ($bridges -and (Test-Path $bridges)) { Check 'BrainX bridges still point at the installed CluadeX' (-not (Mentions $bridges)) $bridges }
else { Check 'BrainX bridges still point at the installed CluadeX' $true 'no bridges file found - nothing to take over' }
$hostNow = HostManifest
Check 'Chrome host still the installed one' (($hostNow -eq $before.host) -and -not ($hostNow -and (Mentions $hostNow))) $(if ($hostNow) { $hostNow } else { 'not registered' })
$edgeHostNow = EdgeHostManifest
Check 'Edge host still the installed one' (($edgeHostNow -eq $before.edgeHost) -and -not ($edgeHostNow -and (Mentions $edgeHostNow))) $(if ($edgeHostNow) { $edgeHostNow } else { 'not registered' })
Check 'CluadeX.lnk unchanged' ((($links | ForEach-Object { FileHash $_ }) -join ',') -eq $before.links)
Check 'no trial shortcut left on the desktop' (-not (Test-Path $link))

# errors the trial logged during this run
# the daily log keeps earlier runs: only lines stamped at or after this start count
$logs = @(Get-ChildItem -LiteralPath $data -Recurse -Filter *.log -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started })
$since = $started.AddSeconds(-1).ToString('yyyy-MM-dd HH:mm:ss')
$runLines = New-Object System.Collections.Generic.List[string]
foreach ($log in $logs) {
    $inRun = $false
    foreach ($line in [IO.File]::ReadAllLines($log.FullName)) {
        if ($line -match '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d') { $inRun = $line.Substring(0, 19) -ge $since }
        if ($inRun) { $runLines.Add($line) }
    }
}
$errLines = @($runLines | Where-Object { $_ -cmatch '\[(ERROR|CRITICAL)\s*\]|Unhandled' } | ForEach-Object { $_.Trim() })
Check 'its log was written' ($runLines.Count -gt 0) "$($runLines.Count) line(s) this run"
Check 'no unhandled exception or critical error in its log' (-not ($errLines | Where-Object { $_ -match 'Unhandled|\[CRITICAL' })) "$($errLines.Count) error line(s)"
Check 'did not check for updates (a release must not replace the trial)' (-not ($runLines | Where-Object { $_ -match '\[Update\s*\] check start' }))

# close it (only this trial's process tree)
if (-not $p.HasExited) {
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(8000)) { & taskkill /PID $p.Id /T /F | Out-Null }
}
Get-Process -Name CluadeX -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { & taskkill /PID $_.Id /T /F | Out-Null }

Say ''
foreach ($r in $results) {
    $tag = if ($r.ok) { 'PASS' } else { 'FAIL' }
    $color = if ($r.ok) { 'Green' } else { 'Red' }
    Write-Host ("{0}  {1}{2}" -f $tag, $r.what, $(if ($r.detail) { "  [$($r.detail)]" } else { '' })) -ForegroundColor $color
}
if ($errLines.Count) { Say ''; Say 'log errors (first 8):'; $errLines | Select-Object -First 8 | ForEach-Object { Say "  $_" } }
$failed = @($results | Where-Object { -not $_.ok }).Count
Say ''
Say ("smoke  : {0} pass / {1} fail - trial kept at {2} (-Remove deletes it)" -f ($results.Count - $failed), $failed, $trialDir)
exit $(if ($failed) { 1 } else { 0 })
