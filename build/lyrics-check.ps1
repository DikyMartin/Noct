# Drives the real Now Playing page with UI Automation and writes plain facts to artifacts\lyrics-check.txt.
# Needs Release build + test lyrics beside "Long Take\01 Long.flac" (see docs). Uses NOCT_DATA_DIR=featuredata.
param([string]$DataDir = "$PSScriptRoot\..\artifacts\featuredata")
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
if ($env:NOCT_EXE) { $exe = $env:NOCT_EXE }
$probe = Join-Path $here 'ui-probe.ps1'
$log = Join-Path $here '..\artifacts\lyrics-check.txt'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
}
'@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Out-Line([string]$s) { Add-Content -Path $log -Value $s -Encoding UTF8; }
function Main-Window($p) { $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id); $AE::RootElement.FindAll($TS::Children, $c) | Where-Object { $_.Current.Name -eq 'Noct' } | Select-Object -First 1 }
function Find([string]$name) { $w = Main-Window $script:p; if (-not $w) { return $null }; $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function Click($el) {
    if (-not $el) { return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
    return $false
}
function Elapsed() {
    $w = Find 'Playback controls'
    if (-not $w) { return -1 }
    $texts = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -match '^(\d+):(\d{2})$') { return [int]$Matches[1] * 60 + [int]$Matches[2] } }
    return -1
}
function Active-Lines() {
    $list = Find 'Lyric lines'
    if (-not $list) { return @() }
    $btns = $list.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    @($btns | Where-Object { $_.Current.ItemStatus -eq 'active' } | ForEach-Object { $_.Current.Name })
}
function Line-Count() { $list = Find 'Lyric lines'; if (-not $list) { return 0 }; $list.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))).Count }
function Spinner() { $s = Find 'Vinyl record'; if ($s) { $s.Current.ItemStatus } else { 'spinner not found' } }
function Play-State() { $b = Find 'Pause'; if ($b) { 'playing' } else { $b = Find 'Play'; if ($b) { 'paused/stopped' } else { '?' } } }
function Sleep-S([double]$s) { Start-Sleep -Milliseconds ([int]($s * 1000)) }
function Shot([string]$n) { & powershell -NoProfile -ExecutionPolicy Bypass -File $probe windowshot $n | Out-Null }
function Expected-Line([int]$sec) {
    # Lines are at 3 + 5k seconds (k = 0..16), plus a second line at 8 s and a blank at 85 s.
    if ($sec -lt 3) { return '(none)' }
    if ($sec -ge 85) { return '(blank)' }
    $k = [int][math]::Floor(($sec - 3) / 5)
    return "line k=$k at $(3 + 5 * $k)s"
}

Remove-Item $log -ErrorAction SilentlyContinue
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = $DataDir
$script:p = Start-Process $exe -PassThru
Sleep-S 6
Out-Line "started pid $($p.Id)"

function Open-Album([string]$prefix) {
    [void](Click (Find 'Library')); Sleep-S 1.2
    $back = Find 'Back to albums'
    if ($back) { [void](Click $back); Sleep-S 1.0 }
    $w = Main-Window $script:p
    $cards = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $card = $cards | Where-Object { $_.Current.Name -like "$prefix*" } | Select-Object -First 1
    if (-not $card) { Out-Line "  album card '$prefix' not found"; return $false }
    [void](Click $card); Sleep-S 1.2
    return (Click (Find 'Play album'))
}

Out-Line ('play Long Take: ' + (Open-Album 'Long Take'))
Sleep-S 1.5
Out-Line ('nav Now Playing: ' + (Click (Find 'Now Playing')))
Sleep-S 1.5
Out-Line "playback: $(Play-State); lyric lines in page: $(Line-Count); spinner: $(Spinner)"
Shot 'np-playing'

Out-Line '--- active line vs playback position (6 samples)'
foreach ($i in 1..6) {
    $e = Elapsed; $a = Active-Lines
    Out-Line ("  elapsed {0}s -> active: [{1}]   expected {2}" -f $e, ($a -join ' / '), (Expected-Line $e))
    Sleep-S 2.2
}

Out-Line '--- pause / resume: disc holds its angle'
[void](Click (Find 'Pause')); Sleep-S 0.8
$s1 = Spinner; Sleep-S 2.0; $s2 = Spinner
Out-Line "  paused: $s1  | 2 s later: $s2   (playback: $(Play-State))"
Shot 'np-paused'
[void](Click (Find 'Play')); Sleep-S 1.0
Out-Line "  resumed: $(Spinner)  (playback: $(Play-State))"

Out-Line '--- click a line to seek'
$target = Find 'Kita pulang dengan tenang'
Out-Line ('  click "Kita pulang dengan tenang" (stamp 01:13): ' + (Click $target))
Sleep-S 0.4
Out-Line ("  elapsed {0}s -> active: [{1}]" -f (Elapsed), ((Active-Lines) -join ' / '))
Shot 'np-after-seek'

Out-Line '--- the shared-timestamp group (08.00) lights together'
[void](Click (Find 'Aku berjalan pelan di tepi jalan')); Sleep-S 0.5
Out-Line ("  elapsed {0}s -> active: [{1}]" -f (Elapsed), ((Active-Lines) -join ' / '))

Out-Line '--- manual scroll pauses following, "Follow song" returns'
Out-Line ("  before scroll: Follow song present = {0}" -f [bool](Find 'Follow song'))
$r = (Find 'Lyrics').Current.BoundingRectangle
[void][Mouse]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
Sleep-S 0.3
[Mouse]::mouse_event(0x0800, 0, 0, 360, [UIntPtr]::Zero); Sleep-S 0.8
Out-Line ("  after wheel: Follow song present = {0}" -f [bool](Find 'Follow song'))
Shot 'np-scrolled'
Sleep-S 5
Out-Line ("  5 s later (song kept playing, view must not jump back): Follow song present = {0}; active [{1}]" -f [bool](Find 'Follow song'), ((Active-Lines) -join ' / '))
Out-Line ('  click Follow song: ' + (Click (Find 'Follow song')))
Sleep-S 0.8
Out-Line ("  after: Follow song present = {0}" -f [bool](Find 'Follow song'))
Shot 'np-followed'

Out-Line '--- page switches do not touch playback'
foreach ($section in @('Home', 'Queue', 'Library', 'Now Playing')) {
    [void](Click (Find $section)); Sleep-S 0.7
    Out-Line ("  {0,-12}: playback {1}, elapsed {2}s" -f $section, (Play-State), (Elapsed))
}

Out-Line '--- minimize to the island and back'
& powershell -NoProfile -ExecutionPolicy Bypass -File $probe minimize | Out-Null
Sleep-S 3
$e1 = Elapsed
& powershell -NoProfile -ExecutionPolicy Bypass -File $probe activate | Out-Null
Sleep-S 2.5
Out-Line ("  after restore: playback {0}, elapsed {1}s (was {2}s), page shows lines: {3}, spinner: {4}" -f (Play-State), (Elapsed), $e1, (Line-Count), (Spinner))

Out-Line '--- track change clears the previous lyrics'
Out-Line ('  play Blue Hour (no .lrc): ' + (Open-Album 'Blue Hour'))
Sleep-S 1.0
[void](Click (Find 'Now Playing')); Sleep-S 1.2
Out-Line ("  lines in page: {0}; 'Lyrics unavailable' shown: {1}" -f (Line-Count), [bool](Find 'Lyrics unavailable'))
Shot 'np-missing'
Out-Line ('  Next track: ' + (Click (Find 'Next track'))); Sleep-S 1.0
Out-Line ("  after Next: lines {0}; missing text shown {1}" -f (Line-Count), [bool](Find 'Lyrics unavailable'))
Out-Line ('  back to Long Take: ' + (Open-Album 'Long Take')); Sleep-S 1.0
[void](Click (Find 'Now Playing')); Sleep-S 1.5
Out-Line ("  lines in page: {0}; elapsed {1}s; active [{2}]" -f (Line-Count), (Elapsed), ((Active-Lines) -join ' / '))

Out-Line '--- close'
& powershell -NoProfile -ExecutionPolicy Bypass -File $probe close | Out-Null
Sleep-S 1
Out-Line "process exited: $($p.HasExited)"
Out-Line 'DONE'
