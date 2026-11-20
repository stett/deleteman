<#
.SYNOPSIS
    Captures the main window of a running process to a PNG (development helper for reviewing the UI).

.DESCRIPTION
    Finds the first process with the given name that owns a visible main window, brings it to the foreground
    (temporarily topmost, since the foreground lock can refuse SetForegroundWindow), waits briefly, and copies its
    screen rectangle into a PNG. Works on Windows PowerShell 5.1 and PowerShell 7+.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\screenshot-window.ps1 -Process deleteman -Out shot.png
#>
[CmdletBinding()]
param(
    [string]$Process = 'deleteman',
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$SettleMs = 600
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace Win32 -Name Shot -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int attr, out RECT rect, int size);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
'@

$proc = Get-Process -Name $Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw "no process named '$Process' with a visible window" }
$hwnd = $proc.MainWindowHandle
# SetForegroundWindow is refused when this script does not own the foreground (foreground lock), so also make the
# window topmost for the capture; that always puts it above other windows. Restored to non-topmost afterwards.
$HWND_TOPMOST = [System.IntPtr](-1)
$HWND_NOTOPMOST = [System.IntPtr](-2)
$SWP_NOSIZE_NOMOVE_SHOW = 0x43
[void][Win32.Shot]::SetForegroundWindow($hwnd)
[void][Win32.Shot]::SetWindowPos($hwnd, $HWND_TOPMOST, 0, 0, 0, 0, $SWP_NOSIZE_NOMOVE_SHOW)
Start-Sleep -Milliseconds $SettleMs

# DWMWA_EXTENDED_FRAME_BOUNDS (9) excludes the invisible resize borders that GetWindowRect includes.
$rect = New-Object Win32.Shot+RECT
if ([Win32.Shot]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16) -ne 0) {
    [void][Win32.Shot]::GetWindowRect($hwnd, [ref]$rect)
}
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "window has no size (minimised?)" }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
[void][Win32.Shot]::SetWindowPos($hwnd, $HWND_NOTOPMOST, 0, 0, 0, 0, $SWP_NOSIZE_NOMOVE_SHOW)
$full = [System.IO.Path]::GetFullPath($Out)
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "$full (${w}x${h})"
