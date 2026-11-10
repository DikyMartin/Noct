<#
Measures the running Noct process. Output is plain numbers with the conditions in the label, meant
to be pasted into docs/BUILD_NOTES.md.

  -Seconds   sampling window (default 20)
  -Label     what state the app is in, e.g. "idle main-visible empty-library"

CPU is reported as percent of ONE logical core (total process CPU time / wall time), which is the
number Task Manager shows divided by core count. GPU is the sum of the process's GPU engine
utilisation counters, when Windows exposes them.
#>
param(
    [int]$Seconds = 20,
    [string]$Label = 'unlabelled'
)

$p = Get-Process Noct -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "$Label : Noct is not running"; return }

$cpu0 = $p.TotalProcessorTime.TotalSeconds
$t0 = Get-Date

$gpu = $null
try {
    $counterPath = "\GPU Engine(pid_$($p.Id)*)\Utilization Percentage"
    $samples = Get-Counter -Counter $counterPath -SampleInterval 1 -MaxSamples $Seconds -ErrorAction Stop
    $perSample = foreach ($s in $samples) { ($s.CounterSamples | Measure-Object CookedValue -Sum).Sum }
    $gpu = ($perSample | Measure-Object -Average).Average
} catch {
    Start-Sleep -Seconds $Seconds
}

$p.Refresh()
$wall = ((Get-Date) - $t0).TotalSeconds
$cpuPct = (($p.TotalProcessorTime.TotalSeconds - $cpu0) / $wall) * 100

$line = "{0,-46} cpu={1,5:F2}%  gpu={2}  workingSet={3,4}MB  private={4,4}MB  handles={5,4}  threads={6,3}  ({7:F0}s)" -f `
    $Label, $cpuPct, $(if ($null -ne $gpu) { '{0,5:F2}%' -f $gpu } else { 'n/a  ' }), `
    [int]($p.WorkingSet64 / 1MB), [int]($p.PrivateMemorySize64 / 1MB), $p.HandleCount, $p.Threads.Count, $wall
Write-Output $line
