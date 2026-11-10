# Samples Noct's CPU use while a song plays on Now Playing (disc spinning) and on Home (no spin).
$ErrorActionPreference = 'Stop'
$env:NOCT_DATA_DIR = "$PSScriptRoot\..\artifacts\featuredata"
$env:NOCT_EXE = "$PSScriptRoot\..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe"
. {
    $src = Get-Content "$PSScriptRoot\lyrics-check.ps1" -Raw
    $helpers = $src.Substring(0, $src.IndexOf("Remove-Item `$log"))
    $helpers = $helpers -replace 'param\(\[string\]\$DataDir[^\)]*\)', ''
    $helpers = $helpers -replace '\$here = Split-Path[^\r\n]*', '$here = $PSScriptRoot'
    Invoke-Expression $helpers
}
$log = "$PSScriptRoot\..\artifacts\spin-cpu.txt"
Remove-Item $log -ErrorAction SilentlyContinue
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$script:p = Start-Process $env:NOCT_EXE -PassThru
Start-Sleep 6
function Sample([string]$label, [int]$seconds) {
    $p = Get-Process -Id $script:p.Id
    $t0 = $p.TotalProcessorTime; $w0 = Get-Date
    Start-Sleep $seconds
    $p.Refresh()
    $cpu = ($p.TotalProcessorTime - $t0).TotalSeconds / ((Get-Date) - $w0).TotalSeconds / [Environment]::ProcessorCount * 100
    Add-Content $log ("{0,-34} {1,5:N1} % of total CPU ({2} logical cores)" -f $label, $cpu, [Environment]::ProcessorCount)
}
[void](Click (Find 'Library')); Start-Sleep 1
$back = Find 'Back to albums'; if ($back) { [void](Click $back); Start-Sleep 1 }
$w = Main-Window $script:p
$card = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Current.Name -like 'Long Take*' } | Select-Object -First 1
[void](Click $card); Start-Sleep 1.2
[void](Click (Find 'Play album')); Start-Sleep 2
[void](Click (Find 'Home')); Start-Sleep 1.5
Sample 'playing, Home page (no disc)' 10
[void](Click (Find 'Now Playing')); Start-Sleep 1.5
Add-Content $log ("disc: " + (Spinner))
Sample 'playing, Now Playing (disc turning)' 10
[void](Click (Find 'Pause')); Start-Sleep 1
Sample 'paused, Now Playing' 8
Add-Content $log 'DONE'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" close | Out-Null
