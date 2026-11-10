param([string]$Tag = 'baseline')
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
$data = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts\perfdata-empty'))
$out = Join-Path $here "..\artifacts\perf-$Tag-minimized.txt"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$env:NOCT_DATA_DIR = $data
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 5
$null = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'ui-probe.ps1') minimize
Start-Sleep -Seconds 7
$p.Refresh()
$alive = -not $p.HasExited
$line = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'perf-probe.ps1') -Seconds 20 -Label 'minimized (island auto-hidden, empty library)'
$p.Refresh()
$result = @("aliveBeforeMeasure=$alive aliveAfterMeasure=$(-not $p.HasExited)") + $line
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$result | Set-Content $out
