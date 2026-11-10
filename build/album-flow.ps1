<#
End-to-end flow through the real UI with UI Automation, written to artifacts/album-flow.txt:
  Library > Albums grid > open an album > Play album > navigate Home / Queue (playback must continue)
  > minimize to the island and restore (playback must continue) > close cleanly.
Reads the Play/Pause button name as the playback signal. Run against a build with NOCT_DATA_DIR set.
#>
param([string]$DataDir = "$PSScriptRoot\..\artifacts\perfdata-migrate")

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
$probe = Join-Path $here 'ui-probe.ps1'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$out = New-Object System.Collections.Generic.List[string]

function Child($el, [string]$key) {
    $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $c = $w.GetFirstChild($el)
    while ($c) {
        if ($c.Current.Name -eq $key -or $c.Current.AutomationId -eq $key) { return $c }
        $c = $w.GetNextSibling($c)
    }
    return $null
}
function Path-To($el, [string[]]$steps) { foreach ($s in $steps) { $el = Child $el $s; if (-not $el) { return $null } }; return $el }
function Click($el) {
    if (-not $el) { return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
    return $false
}
function Root($p) {
    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
}
function PlayName($p) { $t = Path-To (Root $p) @('Playback controls', 'PlayButton'); if ($t) { $t.Current.Name } else { '?' } }
function Dump-Children($el, [int]$max) { $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker; $c = $w.GetFirstChild($el); $n = 0; $names = @(); while ($c -and $n -lt $max) { $names += $c.Current.Name; $c = $w.GetNextSibling($c); $n++ }; $names -join ' | ' }

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = $DataDir
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 7
$root = Root $p
$out.Add("start: play button = '$(PlayName $p)'")

# 1. Library > Albums grid
$out.Add('nav Library: ' + (Click (Child $root 'Library')))
Start-Sleep -Seconds 2
$root = Root $p
$page = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Library')))
$pages = @(); $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker; $c = $w.GetFirstChild($root); while ($c) { if ($c.Current.Name -eq 'Library' -and $c.Current.ControlType.ProgrammaticName -eq 'ControlType.Custom') { $pages += $c }; $c = $w.GetNextSibling($c) }
$libraryPage = $pages | Select-Object -First 1
$search = Child $root 'SearchBox'; $vp = $null; if ($search -and $search.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue('Long tone'); $out.Add('searched: Long tone') }; Start-Sleep -Seconds 1; $root = Root $p
$out.Add('library page children: ' + (Dump-Children $libraryPage 8))

$grid = Child $libraryPage 'Albums list'
if ($grid) {
    $rows = @(); $c = $w.GetFirstChild($grid); while ($c) { $rows += $c; $c = $w.GetNextSibling($c) }
    $out.Add("album grid: $($rows.Count) row containers realized (120 albums + 1 'Long tone' = 121 albums)")
    $firstCard = $null
    foreach ($row in $rows) { $card = $row.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))); if ($card) { $firstCard = $card; break } }
    if ($firstCard) {
        $out.Add("first card: '$($firstCard.Current.Name)'")
        $out.Add('open album: ' + (Click $firstCard))
        Start-Sleep -Seconds 1.5
        $root = Root $p
        $detailList = $null
        $c = $w.GetFirstChild($root); while ($c) { if ($c.Current.Name -eq 'Library') { $d = Path-To $c @('Album tracks'); if ($d) { $detailList = $d } }; $c = $w.GetNextSibling($c) }
        if ($detailList) {
            $items = @(); $c = $w.GetFirstChild($detailList); while ($c) { $items += $c.Current.Name; $c = $w.GetNextSibling($c) }
            $out.Add("album detail: $($items.Count) tracks listed, first 3: $($items | Select-Object -First 3 | ForEach-Object { $_ } | Out-String)".Replace("`r`n", ' | '))
        } else { $out.Add('album detail list not found') }

        $c = $w.GetFirstChild($root); while ($c) { if ($c.Current.Name -eq 'Library') { $play = Path-To $c @('Play album'); if ($play) { $out.Add('Play album: ' + (Click $play)) } }; $c = $w.GetNextSibling($c) }
        Start-Sleep -Seconds 3
        $out.Add("after Play album: play button = '$(PlayName $p)'")
    } else { $out.Add('no card found in realized rows') }
} else { $out.Add('albums grid not found') }

# 2. navigate away: playback must continue
foreach ($section in @('Home', 'Queue', 'Settings', 'Library')) {
    [void](Click (Child (Root $p) $section))
    Start-Sleep -Milliseconds 700
    $out.Add("navigated to $section : play button = '$(PlayName $p)'")
}

# 3. minimize to island and restore
& powershell -NoProfile -ExecutionPolicy Bypass -File $probe minimize | Out-Null
Start-Sleep -Seconds 5
$out.Add('while minimized: ' + ((& powershell -NoProfile -ExecutionPolicy Bypass -File $probe state) -join ' / '))
& powershell -NoProfile -ExecutionPolicy Bypass -File $probe activate | Out-Null
Start-Sleep -Seconds 2
$out.Add("after restore: play button = '$(PlayName $p)'")

# 4. clean close
$out.Add('close: ' + (& powershell -NoProfile -ExecutionPolicy Bypass -File $probe close))
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$out | Set-Content (Join-Path $here '..\artifacts\album-flow.txt')
