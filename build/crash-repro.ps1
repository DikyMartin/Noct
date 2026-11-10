$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
$data = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts\perfdata-crash'))
$out = Join-Path $here '..\artifacts\crash-repro.txt'
if (Test-Path $data) { Remove-Item $data -Recurse -Force }

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = $data
$lines = New-Object System.Collections.Generic.List[string]
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 4
$lines.Add("alive after start: $(-not $p.HasExited)")

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'ui-probe.ps1') minimize | ForEach-Object { $lines.Add("probe: $_") }
for ($i = 1; $i -le 90; $i++) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) {
        $lines.Add(("exited at +{0:F1}s after minimize, exit code {1} (0x{2:X8})" -f ($i * 0.5), $p.ExitCode, $p.ExitCode))
        break
    }
}
if (-not $p.HasExited) { $lines.Add('still running 45 s after minimize'); $p.Kill() }
$logPath = Join-Path $data 'noct.log'
if (Test-Path $logPath) { $lines.Add('--- noct.log ---'); Get-Content $logPath -Tail 20 | ForEach-Object { $lines.Add($_.Substring(0, [Math]::Min(240, $_.Length))) } } else { $lines.Add('no noct.log written') }
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$lines | Set-Content $out
