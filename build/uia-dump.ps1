param([int]$Depth = 4, [string]$DataDir = "$PSScriptRoot\..\artifacts\perfdata-lib")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\artifacts\uia-dump.txt'
$p = Get-Process Noct -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { 'not running' | Set-Content $out; return }

$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$windows = $rootEl.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("top-level elements for pid $($p.Id): $($windows.Count)")
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Walk($el, [int]$level, [int]$max) {
    if ($level -gt $max) { return }
    $name = $el.Current.Name
    if ($name.Length -gt 60) { $name = $name.Substring(0, 60) }
    $lines.Add(('{0}{1} "{2}" id={3}' -f ('  ' * $level), $el.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $name, $el.Current.AutomationId))
    $count = 0
    $child = $walker.GetFirstChild($el)
    while ($child -and $count -lt 14) {
        Walk $child ($level + 1) $max
        $child = $walker.GetNextSibling($child)
        $count++
    }
}

foreach ($w in $windows) { Walk $w 0 $Depth }
$lines | Set-Content $out
