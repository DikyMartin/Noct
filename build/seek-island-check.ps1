# Focused re-check of two suspected problems from the audit: seeking with the mouse/keys, and the island hover reveal.
param([string]$Exe = '')
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = "$PSScriptRoot\..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe" }
$env:NOCT_DATA_DIR = "$PSScriptRoot\..\artifacts\auditdata"
$env:NOCT_EXE = $Exe
. {
    $src = Get-Content "$PSScriptRoot\lyrics-check.ps1" -Raw
    $helpers = $src.Substring(0, $src.IndexOf("Remove-Item `$log"))
    $helpers = $helpers -replace 'param\(\[string\]\$DataDir[^\)]*\)', ''
    $helpers = $helpers -replace '\$here = Split-Path[^\r\n]*', '$here = $PSScriptRoot'
    Invoke-Expression $helpers
}
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class Msg2 { [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }'
$out = "$PSScriptRoot\..\artifacts\seek-island.txt"
Remove-Item $out -ErrorAction SilentlyContinue
function Say([string]$s) { Add-Content $out $s -Encoding UTF8 }
function Click-At([double]$fraction, [string]$name) {
    $r = (Find $name).Current.BoundingRectangle
    [void][Mouse]::SetCursorPos([int]($r.X + $r.Width * $fraction), [int]($r.Y + $r.Height / 2)); Start-Sleep -Milliseconds 250
    [Mouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); [Mouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 700
}
function Key([int]$vk) { $h = [IntPtr](Main-Window $script:p).Current.NativeWindowHandle; [void][Msg2]::PostMessage($h, 0x100, [IntPtr]$vk, [IntPtr]0); Start-Sleep -Milliseconds 40; [void][Msg2]::PostMessage($h, 0x101, [IntPtr]$vk, [IntPtr]0xC0000001); Start-Sleep -Milliseconds 500 }
try {
    Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 600
    $script:p = Start-Process $Exe -PassThru
    $deadline = (Get-Date).AddSeconds(40); while ((Get-Date) -lt $deadline -and -not (Find 'Library')) { Start-Sleep -Milliseconds 500 }; Start-Sleep 3
    [void](Click (Find 'Library')); Start-Sleep 1; $b = Find 'Back to albums'; if ($b) { [void](Click $b); Start-Sleep 1 }
    [void](Click (Find 'Albums')); Start-Sleep 1
    $w = Main-Window $script:p
    Say ("albums shown after restart with 3 folders saved: " + (@($w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '\d+ albums?$' }) -join ' / '))
    $card = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Current.Name -like 'Long Take*' } | Select-Object -First 1
    [void](Click $card); Start-Sleep 1.2; [void](Click (Find 'Play album')); Start-Sleep 2.5
    Say "playing: elapsed $(Elapsed)s"

    Click-At 0.5 'Seek';   Say "mouse click at 50% of the seek bar (playing): elapsed $(Elapsed)s (expect about 45)"
    Click-At 0.1 'Seek';   Say "mouse click at 10%: elapsed $(Elapsed)s (expect about 9-10)"
    (Find 'Seek').SetFocus(); Start-Sleep -Milliseconds 300
    $before = Elapsed; Key 0x27; Say "Right arrow: elapsed $before s -> $(Elapsed)s (expect about +2 s of 90, plus playback)"
    [void](Click (Find 'Pause')); Start-Sleep 0.6
    Click-At 0.8 'Seek';   Say "mouse click at 80% while PAUSED: elapsed $(Elapsed)s (expect about 72); paused=$([bool](Find 'Play'))"
    [void](Click (Find 'Play')); Start-Sleep 1.2; Say "resume: elapsed $(Elapsed)s, playing=$([bool](Find 'Pause'))"

    # Drag the thumb across the bar and release.
    $r = (Find 'Seek').Current.BoundingRectangle; $y = [int]($r.Y + $r.Height / 2)
    $thumbX = [int]($r.X + $r.Width * 0.8)
    [void][Mouse]::SetCursorPos($thumbX, $y); Start-Sleep -Milliseconds 300
    [Mouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
    foreach ($f in 0.6, 0.4, 0.3) { [void][Mouse]::SetCursorPos([int]($r.X + $r.Width * $f), $y); Start-Sleep -Milliseconds 250 }
    Say "while dragging at 30% (button held): elapsed label $(Elapsed)s (expect about 27)"
    [Mouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep 0.9
    Say "after release: elapsed $(Elapsed)s, playing=$([bool](Find 'Pause'))"

    Say '--- island: minimize, wait for it to hide, hover the top edge, restore'
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" minimize | Out-Null
    Start-Sleep 6   # compact for 3 s, then it slides above the screen
    Say ("island state facts: " + ((& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" state) -join ' / '))
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" move '960,1' | Out-Null
    Start-Sleep 1.5
    Say ("after hovering (960,1) physical: " + (& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" has 'Open Noct'))
    Say ("island window now: " + ((& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" state) -join ' / '))
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" move '960,600' | Out-Null
} catch { Say ('FAILED: ' + $_.Exception.Message + ' at line ' + $_.InvocationInfo.ScriptLineNumber) }
Say 'DONE'
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
