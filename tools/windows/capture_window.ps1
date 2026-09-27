# Captures the client's window -- frame, title bar and all -- while it is up,
# and the taskbar at the end, so the icon a person sees can be checked from
# a script. Used by the brand work to prove the sigil is what the window
# and the taskbar show; nothing in the game depends on it.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File capture_window.ps1 -OutDir <dir> [-Seconds 25] [-Every 200] [-Process GUO]
#
# -Process names one process exactly (GUO for the exported build,
# Godot_v4.7.2-stable_mono_win64 for a run through the engine); without it
# any Godot or GUO window on the desktop is taken, other projects included.
#
# Frames go to <OutDir>\frame_NNN.png every -Every ms while a window of a
# Godot or GUO process exists (up to 40 of them); taskbar.png is the bottom
# 60 px of the primary screen, taken when the window was last seen. The
# frames are rendered from the window itself (PrintWindow), so a window
# on top of ours does not get into them; the taskbar is a screen grab.
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Seconds = 25,
    [int]$Every = 200,
    [int]$MaxFrames = 40,
    [string]$Process = ""
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@
# Without this, window rectangles come back in scaled units on a high-DPI
# display and the capture lands beside the window instead of on it.
[void][Win32]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Save-Window([IntPtr]$hwnd, [int]$w, [int]$h, [string]$path) {
    # PrintWindow with PW_RENDERFULLCONTENT (2) draws the window from its own
    # DWM surface, frame and title bar included, so another window sitting
    # on top of ours (another agent's game, say) does not end up in the shot.
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Win32]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    if ($ok) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    return $ok
}

function Save-Region([int]$x, [int]$y, [int]$w, [int]$h, [string]$path) {
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $true
}

$deadline = (Get-Date).AddSeconds($Seconds)
$n = 0
$seen = $false
while ((Get-Date) -lt $deadline -and $n -lt $MaxFrames) {
    if ($Process) { $procs = Get-Process | Where-Object { $_.ProcessName -eq $Process -and $_.MainWindowHandle -ne 0 } }
    else { $procs = Get-Process | Where-Object { ($_.ProcessName -like "Godot_v*" -or $_.ProcessName -like "GUO*") -and $_.MainWindowHandle -ne 0 } }
    foreach ($p in $procs) {
        $h = $p.MainWindowHandle
        if (-not [Win32]::IsWindowVisible($h)) { continue }
        $r = New-Object Win32+RECT
        [void][Win32]::GetWindowRect($h, [ref]$r)
        $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
        if ($w -lt 50 -or $hh -lt 50) { continue }
        $path = Join-Path $OutDir ("frame_{0:D3}.png" -f $n)
        if (Save-Window $h $w $hh $path) {
            Write-Output ("frame {0}: {1} '{2}' {3}x{4} at {5},{6}" -f $n, $p.ProcessName, $p.MainWindowTitle, $w, $hh, $r.Left, $r.Top)
            $n++
            $seen = $true
        }
    }
    if ($seen -and $procs.Count -eq 0) { break }
    Start-Sleep -Milliseconds $Every
}
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
[void](Save-Region $screen.X ($screen.Bottom - 60) $screen.Width 60 (Join-Path $OutDir "taskbar.png"))
Write-Output ("{0} frame(s); taskbar.png" -f $n)
