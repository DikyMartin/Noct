<#
Does the island ever show a rectangular layer outside its outline, especially in its first frames?
Samples two pixels at ~10 ms for 1.5 s after the island is created. Both lie inside the island's
window box but OUTSIDE its outline and OUTSIDE the main window, so a dark reading there can only be
a stray rectangular layer. The backdrop colour must be light (default FFFFFF).
#>
param([string]$Color = 'FFFFFF')

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Ff {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
}
'@
[void][Ff]::SetProcessDPIAware()
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$cx = [int]($screen.Width / 2)

Start-Process powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'shape-probe.ps1'), '-Color', $Color, '-Seconds', '9') -WindowStyle Hidden | Out-Null
Start-Sleep -Milliseconds 1500
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = (Join-Path $here '..\artifacts\perfdata-empty')
$null = Start-Process $exe
Start-Sleep -Seconds 4

$main = [Ff]::FindWindow([NullString]::Value, 'Noct')
$bmp = New-Object System.Drawing.Bitmap(1, 1)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$points = @(@(($cx + 230), 40), @(($cx - 230), 40), @(($cx + 300), 30), @(($cx - 300), 30), @(($cx + 215), 50))
$ref = 0
$worst = 255
$worstAt = ''
$samples = 0
$dark = New-Object System.Collections.Generic.List[string]
$sw = [System.Diagnostics.Stopwatch]::StartNew()
[void][Ff]::SendMessage($main, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero)
while ($sw.Elapsed.TotalSeconds -lt 1.5) {
    $g.CopyFromScreen(($cx + 340), 20, 0, 0, (New-Object System.Drawing.Size(1, 1)))
    $rc = $bmp.GetPixel(0, 0)
    $ref = [math]::Max($ref, ($rc.R + $rc.G + $rc.B) / 3)
    foreach ($pt in $points) {
        $g.CopyFromScreen($pt[0], $pt[1], 0, 0, (New-Object System.Drawing.Size(1, 1)))
        $c = $bmp.GetPixel(0, 0)
        $lum = ($c.R + $c.G + $c.B) / 3
        if ($lum -lt $worst) { $worst = $lum; $worstAt = ('({0},{1}) at t={2:F2}s' -f $pt[0], $pt[1], $sw.Elapsed.TotalSeconds) }
        if ($lum -lt 200) { $dark.Add(("t={0:F2}s ({1},{2}) lum={3:F0}" -f $sw.Elapsed.TotalSeconds, $pt[0], $pt[1], $lum)) }
        $samples++
    }
    Start-Sleep -Milliseconds 8
}
$g.Dispose(); $bmp.Dispose()
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$verdict = if ($ref -lt 240) { "INVALID RUN: backdrop colour not present (reference pixel $ref)" } elseif ($worst -gt 235) { 'no stray layer seen' } else { 'STRAY DARK PIXELS OUTSIDE THE OUTLINE' }
"dark readings: $($dark.Count) -> " + ($dark | Select-Object -First 8 | Out-String).Replace("`r`n", " | ") + "`r`nsamples=$samples  darkest luminance outside the outline = $worst $worstAt  -> $verdict" | Set-Content (Join-Path $here '..\artifacts\island-firstframe.txt')
