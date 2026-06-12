# Hook: stop-desktop-notify
# Phase: Stop
# Pops a Windows toast when the agent finishes. Falls back to a balloon tip if
# BurntToast isn't installed.
param(
  [string]$Model = "",
  [string]$Cost = "0"
)

$body = "Model: $Model"
if ([double]$Cost -gt 0) { $body += "  ·  Cost: `$$Cost" }

# Try BurntToast first
$burnt = Get-Module -ListAvailable -Name BurntToast -ErrorAction SilentlyContinue
if ($burnt) {
  try {
    Import-Module BurntToast -ErrorAction Stop
    New-BurntToastNotification -Text "CluadeX — turn complete", $body | Out-Null
    exit 0
  } catch { }
}

# Fallback: Windows NotifyIcon balloon
try {
  Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
  $bal = New-Object System.Windows.Forms.NotifyIcon
  $bal.Icon = [System.Drawing.SystemIcons]::Information
  $bal.BalloonTipTitle = "CluadeX — turn complete"
  $bal.BalloonTipText = $body
  $bal.Visible = $true
  $bal.ShowBalloonTip(3000)
  Start-Sleep -Seconds 3
  $bal.Dispose()
} catch { }
exit 0
