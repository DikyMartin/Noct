<#
Deterministic island timeline on the real desktop, one process so timing is not distorted.
Samples every ~50 ms: the island window height (thin strip vs full box) and one screen pixel inside
the expanded-only area (dark = expanded). A solid probe colour sits behind the island.

Schedule (seconds after Minimize):
  0.0  minimize
  1.0  pointer into the TRANSPARENT corner of the window box (outside the shape)  -> must stay compact
  2.6  pointer away
  3.3+ island auto-hides to the strip
  5.0  pointer onto the strip                                                     -> must expand
  7.0  pointer away                                                              -> collapse, then hide
#>
param([string]$Color = '202020', [string]$Tag = 'dark')

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Tl {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[void][Tl]::SetProcessDPIAware()
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$cx = [int]($screen.Width / 2)

# backdrop window behind the island
$shape = Join-Path $here 'shape-probe.ps1'
Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $shape, '-Color', $Color, '-Seconds', '16') -WindowStyle Hidden | Out-Null
Start-Sleep -Milliseconds 1500

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = (Join-Path $here '..\artifacts\perfdata-empty')
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 4
[Tl]::SetCursorPos(200, 700) | Out-Null

$main = [Tl]::FindWindow([NullString]::Value, 'Noct')

$bmp = New-Object System.Drawing.Bitmap(1, 1)
$g = [System.Drawing.Graphics]::FromImage($bmp)
function Pixel([int]$x, [int]$y) {
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size(1, 1)))
    $c = $bmp.GetPixel(0, 0)
    return ($c.R + $c.G + $c.B) / 3
}
$backdropLevel = [System.Drawing.ColorTranslator]::FromHtml("#$Color")
$backdropLevel = ($backdropLevel.R + $backdropLevel.G + $backdropLevel.B) / 3

$log = New-Object System.Collections.Generic.List[string]
$sw = [System.Diagnostics.Stopwatch]::new()
$last = ''
$done = @{}
$sw.Start()
[void][Tl]::SendMessage($main, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero)
$minimizedAt = $sw.Elapsed.TotalSeconds
$log.Add(('t=0.00 minimize sent to hwnd {0} (visible={1})' -f $main, [Tl]::IsWindowVisible($main)))

while ($sw.Elapsed.TotalSeconds -lt 10.5) {
    $t = $sw.Elapsed.TotalSeconds
    if ($t -ge 1.0 -and -not $done['corner']) { [Tl]::SetCursorPos($cx + 200, 100) | Out-Null; $done['corner'] = 1; $log.Add(('t={0:F2} pointer -> transparent corner of the box (outside the shape)' -f $t)) }
    if ($t -ge 2.6 -and -not $done['away1']) { [Tl]::SetCursorPos(200, 700) | Out-Null; $done['away1'] = 1; $log.Add(('t={0:F2} pointer away' -f $t)) }
    if ($t -ge 5.0 -and -not $done['strip']) { [Tl]::SetCursorPos($cx, 1) | Out-Null; $done['strip'] = 1; $log.Add(('t={0:F2} pointer -> hidden strip' -f $t)) }
    if ($t -ge 7.0 -and -not $done['away2']) { [Tl]::SetCursorPos(200, 700) | Out-Null; $done['away2'] = 1; $log.Add(('t={0:F2} pointer away' -f $t)) }

    $h = [Tl]::FindWindow([NullString]::Value, 'Noct island')
    $label = 'no-island-window'
    if ($h -ne [IntPtr]::Zero -and [Tl]::IsWindowVisible($h)) {
        $r = New-Object Tl+RECT
        [void][Tl]::GetWindowRect($h, [ref]$r)
        $height = $r.B - $r.T
        if ($height -le 6) {
            $label = "strip(${height}px)"
        } else {
            $deep = Pixel $cx 150
            $label = if ([math]::Abs($deep - $backdropLevel) -gt 10) { 'expanded' } else { 'compact' }
        }
    }
    if ($label -ne $last) { $log.Add(('t={0:F2} state -> {1}' -f $t, $label)); $last = $label }
    Start-Sleep -Milliseconds 40
}

$fg = [Tl]::GetForegroundWindow()
$log.Add("island has foreground at end: $($fg -eq [Tl]::FindWindow([NullString]::Value, 'Noct island'))")
$g.Dispose(); $bmp.Dispose()
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$log | Set-Content (Join-Path $here "..\artifacts\island-timeline-$Tag.txt")
