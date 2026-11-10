# Walks every page of the running app and writes every UI Automation name (button labels, tooltips are
# not exposed, headings, empty states, notices) to artifacts\english-check.txt for review. Also captures
# Now Playing for four lyric states: found, unsynced, unreadable, none.
$ErrorActionPreference = 'Stop'
$env:NOCT_DATA_DIR = "$PSScriptRoot\..\artifacts\artdata"
$exe = if ($env:NOCT_EXE) { $env:NOCT_EXE } else { "$PSScriptRoot\..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe" }
. {
    $src = Get-Content "$PSScriptRoot\lyrics-check.ps1" -Raw
    $helpers = $src.Substring(0, $src.IndexOf("Remove-Item `$log"))
    $helpers = $helpers -replace 'param\(\[string\]\$DataDir[^\)]*\)', ''
    $helpers = $helpers -replace '\$here = Split-Path[^\r\n]*', '$here = $PSScriptRoot'
    Invoke-Expression $helpers
}
$out = "$PSScriptRoot\..\artifacts\english-check.txt"
Remove-Item $out -ErrorAction SilentlyContinue
function Say([string]$s) { Add-Content -Path $out -Value $s -Encoding UTF8 }
function Names([string]$skipInside = '') {
    $w = Main-Window $script:p
    $all = $w.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $skip = $null; if ($skipInside) { $skip = Find $skipInside }
    $set = New-Object System.Collections.Generic.SortedSet[string]
    foreach ($e in $all) {
        $n = $e.Current.Name
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        if ($skip) {
            $r = $skip.Current.BoundingRectangle; $b = $e.Current.BoundingRectangle
            if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Pane' -and $b.Left -ge $r.Left -and $b.Right -le $r.Right -and $b.Top -ge $r.Top -and $b.Bottom -le $r.Bottom -and $e.Current.AutomationId -ne 'x') { if ($e.Current.ControlType.ProgrammaticName -in 'ControlType.Button','ControlType.Text') { continue } }
        }
        [void]$set.Add($n)
    }
    $set
}
function Page([string]$label, [string]$nav) {
    [void](Click (Find $nav)); Start-Sleep 1.3
    Say "=== $label"
    foreach ($n in (Names)) { Say "  $n" }
}

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$script:p = Start-Process $exe -ArgumentList ('"{0}"' -f (Join-Path $PSScriptRoot '..\artifacts\artlib')) -PassThru
Start-Sleep 9

# Play the "Lyric States" album: A synced, B plain, C unreadable, D none.
[void](Click (Find 'Library')); Start-Sleep 1
$back = Find 'Back to albums'; if ($back) { [void](Click $back); Start-Sleep 1 }
$w = Main-Window $script:p
$card = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Current.Name -like 'Lyric States*' } | Select-Object -First 1
Say "album card: $($card.Current.Name)"
[void](Click $card); Start-Sleep 1.2
[void](Click (Find 'Play album')); Start-Sleep 1.5
[void](Click (Find 'Now Playing')); Start-Sleep 1.5

$states = @('A Synced', 'B Plain', 'C Unreadable', 'D None')
for ($i = 0; $i -lt 4; $i++) {
    if ($i -gt 0) { [void](Click (Find 'Next track')); Start-Sleep 1.8 }
    Say "=== Now Playing: $($states[$i])"
    foreach ($n in (Names)) { Say "  $n" }
    Say "  [lines=$(Line-Count); 'Follow song' present=$([bool](Find 'Follow song')); picker-like button present=$([bool]((Find 'Pilih file .lrc') -or (Find 'Choose lyrics file') -or (Find 'Select .lrc file')))]"
    Shot ("en-np-" + $i)
}
foreach ($pair in @(@('Home', 'Home'), @('Queue', 'Queue'), @('Settings', 'Settings'), @('Library (albums)', 'Library'))) { Page $pair[0] $pair[1] }
Say 'DONE'
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
