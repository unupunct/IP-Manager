param(
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$NavIndex = -1,
    [int]$DelaySeconds = 2,
    [int]$TabIndex = -1
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class IPManagerCaptureWin32
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@

$process = Get-Process IPManager -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "IPManager is not running." }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if (-not $root) { throw "The IP Manager window is not available through UI Automation." }

if ($NavIndex -ge 0) {
    $list = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::List))
    if (-not $list) { throw "The navigation list was not found." }
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    if ($NavIndex -ge $items.Count) { throw "Navigation index $NavIndex is out of range." }
    $selection = $items[$NavIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Seconds $DelaySeconds
}

if ($TabIndex -ge 0) {
    $tabs = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::TabItem))
    if ($TabIndex -ge $tabs.Count) { throw "Tab index $TabIndex is out of range." }
    $tabs[$TabIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 1
}

$rect = New-Object IPManagerCaptureWin32+RECT
[IPManagerCaptureWin32]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw "The IP Manager window has no visible bounds." }

$directory = Split-Path -Parent $Out
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
$ok = [IPManagerCaptureWin32]::PrintWindow($process.MainWindowHandle, $hdc, 2)
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

if (-not $ok) { throw "PrintWindow failed." }
"Saved $Out (${width}x${height})"
