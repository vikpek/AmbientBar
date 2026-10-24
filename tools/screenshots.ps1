# Captures README/site screenshots of a running AmbientBar build against a clean backdrop.
# Usage: powershell -File tools/screenshots.ps1 -Exe <path to AmbientBar.exe> -Out <dir>
param([string]$Exe, [string]$Out, [string]$Query = "ambient 10 hours")

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force $Out | Out-Null

# Full-screen gradient backdrop so the screenshots don't show whatever is on the desktop.
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$backdrop = New-Object System.Windows.Forms.Form
$backdrop.FormBorderStyle = 'None'
$backdrop.StartPosition = 'Manual'
$backdrop.Bounds = $screen.Bounds
$backdrop.ShowInTaskbar = $false
$backdrop.Add_Paint({
    param($s, $e)
    $rect = $s.ClientRectangle
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 34, 40, 64), [System.Drawing.Color]::FromArgb(255, 14, 15, 20), 60.0)
    $e.Graphics.FillRectangle($brush, $rect)
})
$backdrop.Show()
[System.Windows.Forms.Application]::DoEvents()

$p = Start-Process $Exe -PassThru
Start-Sleep 3
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', $cond)

function Snap([string]$name) {
    [System.Windows.Forms.Application]::DoEvents()
    $r = $win.Current.BoundingRectangle
    $pad = 28
    $x = [int]$r.X - $pad; $y = [int]$r.Y - $pad
    $w = [int]$r.Width + 2 * $pad; $h = [int]$r.Height + 2 * $pad
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
}

$buttons = $win.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
# Bar buttons in order: play, next, expand, quit (queue item buttons are hidden while collapsed).
$play = $buttons[0]
$expand = $buttons[$buttons.Count - 2]

# 1) Collapsed bar, playing.
$play.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep 12
Snap "bar"

# 2) Expanded with search results.
$expand.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep 1
$box = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Query)
$box.SetFocus()
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep 12
Snap "expanded"

$p | Stop-Process
$backdrop.Close()
