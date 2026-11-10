<#
Hover behaviour of the island on the real desktop, on a solid backdrop colour.
  A. pointer in the transparent part of the window box (outside the shape) must NOT expand it
  B. pointer on the hidden strip must expand it after the dwell, without activating or restoring
  C. pointer leaving must collapse it again after the grace period
Crops contain only the probe colour and the island.
#>
param([string]$Color = '202020', [string]$Tag = 'dark')

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
$shots = (Resolve-Path (Join-Path $here '..\artifacts\screens')).Path
$probe = Join-Path $here 'ui-probe.ps1'
$shape = Join-Path $here 'shape-probe.ps1'
$report = New-Object System.Collections.Generic.List[string]

function Probe([string]$verb, [string]$arg = '') { & powershell -NoProfile -ExecutionPolicy Bypass -File $probe $verb $arg }
function Backdrop([string]$name, [double]$after, [int]$seconds) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $shape, '-Color', $Color, '-Seconds', "$seconds",
        '-Crop', (Join-Path $shots "$name.png"), '-CropAfter', "$after")
    Start-Process powershell -ArgumentList $a -WindowStyle Hidden | Out-Null
}
function IslandState { $s = (Probe 'state') | Where-Object { $_ -match "title='Noct island'" } | ForEach-Object { ($_ -replace '^.*visible=', 'visible=') -replace 'title=.*rect=', 'rect=' }; $e = Probe 'has' 'Open Noct'; "$s | $e" }

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = (Join-Path $here '..\artifacts\perfdata-empty')
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 4

Backdrop "island-$Tag-A-outside" 4.0 16
Start-Sleep -Seconds 1.0
[void](Probe 'minimize')
Start-Sleep -Seconds 0.6
# Inside the window box, but below and beside the compact shape.
[void](Probe 'move' '1200,100')
Start-Sleep -Seconds 1.5
$report.Add('A. pointer in transparent corner of the window box: ' + (IslandState))
Start-Sleep -Seconds 3.5
[void](Probe 'move' '200,700')

# island auto-hides ~3 s after minimize; wait it out, then hover the strip
Start-Sleep -Seconds 1.0
$report.Add('   after auto-hide: ' + (IslandState))
[void](Probe 'move' '960,1')
Start-Sleep -Seconds 0.9
$report.Add('B. pointer on the hidden strip (after 0.9 s): ' + (IslandState))
$mainVisible = (Probe 'state') | Where-Object { $_ -match "title='Noct' " -and $_ -match 'visible=True' }
$report.Add('   main window restored by hover? ' + [bool]$mainVisible)
Start-Sleep -Seconds 2.0

[void](Probe 'move' '200,700')
Start-Sleep -Seconds 1.0
$report.Add('C. 1.0 s after the pointer left: ' + (IslandState))
Start-Sleep -Seconds 3.0
$report.Add('   3 s later: ' + (IslandState))

$p.Refresh()
$report.Add("process alive: $(-not $p.HasExited)")
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$report | Set-Content (Join-Path $here "..\artifacts\island-hover-$Tag.txt")
