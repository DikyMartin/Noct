param([int]$Seconds = 5)
$p = Get-Process Noct -ErrorAction SilentlyContinue | Select-Object -First 1
$out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\artifacts\thread-probe.txt'
if (-not $p) { 'not running' | Set-Content $out; return }

function Snap {
    $p.Refresh()
    $h = @{}
    foreach ($t in $p.Threads) {
        try { $h[[string]$t.Id] = $t.TotalProcessorTime.TotalMilliseconds } catch { }
    }
    return $h
}

$a = Snap
Start-Sleep -Seconds $Seconds
$b = Snap
$lines = foreach ($id in $b.Keys) {
    $prev = 0
    if ($a.ContainsKey($id)) { $prev = $a[$id] }
    '{0,8}  {1,8:F0} ms' -f $id, ($b[$id] - $prev)
}
$lines | Sort-Object { [double](($_ -split '\s+')[2]) } -Descending | Select-Object -First 8 | Set-Content $out
