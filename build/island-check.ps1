<#
End-to-end island check on the real desktop. For one backdrop colour it:
  launches Noct, minimizes it, crops the compact island, waits for auto-hide and crops the strip,
  then restores through the single-instance route and reports what is visible.
Crops contain only the solid probe colour and the island.
#>
param([string]$Color = 'FFFFFF', [string]$Tag = 'white')

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
$shots = Join-Path $here '..\artifacts\screens'
New-Item -ItemType Directory -Force -Path $shots | Out-Null
$shots = (Resolve-Path $shots).Path
$probe = Join-Path $here 'ui-probe.ps1'
$shape = Join-Path $here 'shape-probe.ps1'
$report = New-Object System.Collections.Generic.List[string]

function Run-Shape([string]$name, [double]$after) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $shape, '-Color', $Color, '-Seconds', ([int]($after + 3)),
        '-Crop', (Join-Path $shots "$name.png"), '-CropAfter', "$after")
    Start-Process powershell -ArgumentList $args -WindowStyle Hidden | Out-Null
}

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = (Join-Path $here '..\artifacts\perfdata-empty')
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 4

# 1. compact island (crop at 1.2 s after minimize, well before the 3 s idle hide)
Run-Shape "island-$Tag-compact" 2.2
Start-Sleep -Seconds 1.0
$report.Add('minimize: ' + (& powershell -NoProfile -ExecutionPolicy Bypass -File $probe minimize))
Start-Sleep -Seconds 1.2
$report.Add('--- state during compact ---')
(& powershell -NoProfile -ExecutionPolicy Bypass -File $probe state) | ForEach-Object { $report.Add($_) }
Start-Sleep -Seconds 2.5

# 2. hidden strip (after 3 s idle + 220 ms slide)
Run-Shape "island-$Tag-hidden" 1.0
Start-Sleep -Seconds 2.5
$report.Add('--- state after auto-hide ---')
(& powershell -NoProfile -ExecutionPolicy Bypass -File $probe state) | ForEach-Object { $report.Add($_) }
Start-Sleep -Seconds 2

# 3. restore via the second-instance route
$report.Add('restore: ' + (& powershell -NoProfile -ExecutionPolicy Bypass -File $probe activate))
Start-Sleep -Seconds 2
$report.Add('--- state after restore ---')
(& powershell -NoProfile -ExecutionPolicy Bypass -File $probe state) | ForEach-Object { $report.Add($_) }
$p.Refresh()
$report.Add("process alive: $(-not $p.HasExited)")

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$report | Set-Content (Join-Path $here "..\artifacts\island-check-$Tag.txt")
