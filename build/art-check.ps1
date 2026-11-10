# Plays the artwork + picked-lyrics test album and captures the Now Playing page (wide, then narrow).
$ErrorActionPreference = 'Stop'
$env:NOCT_DATA_DIR = "$PSScriptRoot\..\artifacts\artdata"
$env:NOCT_EXE = "$PSScriptRoot\..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe"
$script:p = $null
. {
    $here = $PSScriptRoot
    $src = Get-Content "$here\lyrics-check.ps1" -Raw
    # Reuse only the helper functions (everything before the scenario starts).
    $helpers = $src.Substring(0, $src.IndexOf("Remove-Item `$log"))
    $helpers = $helpers -replace 'param\(\[string\]\$DataDir[^\)]*\)', ''
    $helpers = $helpers -replace '\$here = Split-Path[^\r\n]*', '$here = $PSScriptRoot'
    Invoke-Expression $helpers
}
$log = "$PSScriptRoot\..\artifacts\art-check.txt"
Remove-Item $log -ErrorAction SilentlyContinue
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$script:p = Start-Process $env:NOCT_EXE -PassThru
Start-Sleep 6
[void](Click (Find 'Library')); Start-Sleep 1
$w = Main-Window $script:p
$cards = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
$card = $cards | Where-Object { $_.Current.Name -like 'Art Album*' } | Select-Object -First 1
Add-Content $log "card: $($card.Current.Name)"
[void](Click $card); Start-Sleep 1.2
Add-Content $log ('play album: ' + (Click (Find 'Play album')))
Start-Sleep 1
Add-Content $log ('now playing: ' + (Click (Find 'Now Playing')))
Start-Sleep 2
Add-Content $log ("lines: $(Line-Count) active: [$((Active-Lines) -join ' / ')] spinner: $(Spinner)")
& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" windowshot np-art | Out-Null
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win2 { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int cx, uint f); }
'@
[void][Win2]::SetWindowPos([IntPtr](Main-Window $script:p).Current.NativeWindowHandle, [IntPtr]::Zero, 200, 100, 1440, 900, 0x14)
Start-Sleep 1.5
& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" windowshot np-art-narrow | Out-Null
Add-Content $log 'narrow shot taken (1440x900 px = 960x600 DIP)'
Add-Content $log 'DONE'
