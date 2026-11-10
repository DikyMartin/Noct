param([string]$DataDir = "$PSScriptRoot\..\artifacts\perfdata-lib")
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Child($el, [string]$key) {
    $c = $walker.GetFirstChild($el)
    while ($c) { if ($c.Current.Name -eq $key -or $c.Current.AutomationId -eq $key) { return $c }; $c = $walker.GetNextSibling($c) }
    return $null
}
function Invoke-It($el) {
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    return $false
}

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = $DataDir
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 7
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
[void](Invoke-It (Child $root 'Library'))
Start-Sleep -Seconds 1
$page = $null
$c = $walker.GetFirstChild($root)
while ($c) { if ($c.Current.Name -eq 'Library' -and $c.Current.ControlType.ProgrammaticName -eq 'ControlType.Custom') { $page = $c; break }; $c = $walker.GetNextSibling($c) }
$result = New-Object System.Collections.Generic.List[string]
[void](Invoke-It (Child $page 'All songs'))
Start-Sleep -Seconds 2
$list = Child $page 'TrackList'
if ($list) {
    $n = 0
    $c = $walker.GetFirstChild($list)
    while ($c) { if ($c.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $n++ }; $c = $walker.GetNextSibling($c) }
    $result.Add("All songs list: $n ListItem containers realized for 3031 rows")

    $sp = $null
    $canScroll = $list.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$sp)
    $result.Add("ScrollPattern available: $canScroll")
    if ($canScroll) {
        $p.Refresh(); $cpu0 = $p.TotalProcessorTime.TotalSeconds; $t0 = Get-Date
        for ($i = 0; $i -lt 150; $i++) { $sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement); Start-Sleep -Milliseconds 40 }
        $wall = ((Get-Date) - $t0).TotalSeconds; $p.Refresh()
        $result.Add(('scrolling: cpu={0:F1}% of one core over {1:F1}s (150 page scrolls)' -f ((($p.TotalProcessorTime.TotalSeconds - $cpu0) / $wall) * 100), $wall))
    }
} else { $result.Add('TrackList not found') }
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$result | Set-Content (Join-Path $here '..\artifacts\songs-check.txt')
