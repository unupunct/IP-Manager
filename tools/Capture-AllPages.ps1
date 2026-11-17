param(
    [string]$OutputDirectory = "screenshots/pages",
    [int]$DelayMilliseconds = 900
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class IPManagerCaptureAll
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@

$process = Get-Process IPManager -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "IPManager is not running." }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$list = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List))
if (-not $list) { throw "The navigation list was not found." }

$names = @(
    "01-dashboard",
    "02-local-addresses",
    "03-discovery",
    "04-devices",
    "05-ip-manager",
    "06-subnet-calculator",
    "07-subnet-manager",
    "08-dhcp",
    "09-dns",
    "10-conflicts",
    "11-network-map",
    "12-monitoring",
    "13-diagnostics",
    "14-alerts",
    "15-reports",
    "16-settings"
)

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
if ($items.Count -lt $names.Count) { throw "Expected $($names.Count) navigation items but found $($items.Count)." }

for ($i = 0; $i -lt $names.Count; $i++) {
    $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds $DelayMilliseconds

    $process.Refresh()
    if ($process.HasExited) { throw "IPManager exited while opening $($names[$i])." }

    $rect = New-Object IPManagerCaptureAll+RECT
    [IPManagerCaptureAll]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw "No visible window bounds for $($names[$i])." }

    $path = Join-Path $OutputDirectory "$($names[$i]).png"
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $ok = [IPManagerCaptureAll]::PrintWindow($process.MainWindowHandle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    if (-not $ok) { throw "PrintWindow failed for $($names[$i])." }
    "Captured $path"
}
