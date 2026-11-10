<#
Samples a freshly launched Noct every 2 seconds for -Seconds, so startup work (loading the index,
rescanning, building rows) can be told apart from true idle. Writes artifacts/startup-<Tag>.txt.
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$DataDir = '',
    [string]$Exe = '',
    [int]$Seconds = 60
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe' }
if (-not $DataDir) { $DataDir = Join-Path $here '..\artifacts\perfdata-lib' }
$Exe = [System.IO.Path]::GetFullPath($Exe)
$DataDir = [System.IO.Path]::GetFullPath($DataDir)
$out = Join-Path $here "..\artifacts\startup-$Tag.txt"

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$env:NOCT_DATA_DIR = $DataDir
$start = Get-Date
$p = Start-Process $Exe -PassThru

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# startup curve: $Tag  dataDir=$DataDir")
$last = 0.0
$lastT = Get-Date
for ($i = 0; $i -lt ($Seconds / 2); $i++) {
    Start-Sleep -Seconds 2
    $p.Refresh()
    if ($p.HasExited) { $lines.Add('process exited'); break }
    $now = Get-Date
    $cpu = $p.TotalProcessorTime.TotalSeconds
    $pct = (($cpu - $last) / ($now - $lastT).TotalSeconds) * 100
    $lines.Add(('t={0,5:F1}s  cpu={1,6:F1}%  workingSet={2,4}MB  threads={3}' -f ($now - $start).TotalSeconds, $pct, [int]($p.WorkingSet64 / 1MB), $p.Threads.Count))
    $last = $cpu
    $lastT = $now
}

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$lines | Set-Content $out
