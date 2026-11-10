<#
Runs the same measurement scenarios against a Noct build and writes plain numbers to
artifacts/perf-<Tag>.txt. Run it once before a refactor and once after, with the same machine and
the same synthetic library (build/make-perf-library.ps1), and compare the two files.

All user data is redirected with NOCT_DATA_DIR, so real settings and library are never touched.
Playback uses a 90 s tone at amplitude 0.04 through the default device in WASAPI shared mode at
the app's volume setting 0.5, so it is quiet but real output.

Scenarios
  start-empty      process start to a visible main window, empty library
  idle-empty       main window visible, empty library, no interaction
  minimized        main window hidden, island auto-hidden to its strip
  scan-first       first launch with 3031 files: time until the library index is written
  start-loaded     process start to visible main window with a saved 3031-track library
  idle-loaded      main visible, library loaded, nothing playing
  playing          playback running, main visible
  playing-hidden   playback running, window minimized to the island
  scrolling        continuous programmatic scrolling of the long list
  list-realized    how many list item containers WPF actually created (virtualization check)
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Exe = "",
    [string]$Library = "",
    [int]$Window = 20
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe' }
if (-not $Library) { $Library = Join-Path $here '..\artifacts\perflib' }
$Exe = [System.IO.Path]::GetFullPath($Exe)
$Library = [System.IO.Path]::GetFullPath($Library)
$root = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts'))
$out = Join-Path $root "perf-$Tag.txt"
$probe = Join-Path $here 'perf-probe.ps1'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class PerfW32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$results = New-Object System.Collections.Generic.List[string]
function Log([string]$line) { Write-Output $line; $results.Add($line) }

function Stop-Noct { Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 600 }

function Start-Noct([string]$dataDir) {
    Stop-Noct
    $env:NOCT_DATA_DIR = $dataDir
    $start = Get-Date
    $p = Start-Process $Exe -PassThru
    $deadline = $start.AddSeconds(40)
    $h = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline -and $h -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 20
        $h = [PerfW32]::Find([uint32]$p.Id, 'Noct')
    }
    $ms = ((Get-Date) - $start).TotalMilliseconds
    return [pscustomobject]@{ Process = $p; Handle = $h; StartMs = $ms }
}

function Get-Root($p) {
    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
}

# Navigation uses direct children only. A Descendants search over a 3031-row list makes WPF create
# an automation peer for every row, which is load caused by the test tool, not by the app at rest.
function Get-Children($el) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $list = New-Object System.Collections.Generic.List[object]
    $c = $walker.GetFirstChild($el)
    while ($c) { $list.Add($c); $c = $walker.GetNextSibling($c) }
    return $list
}

function Find-Child($el, [string]$nameOrId) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $c = $walker.GetFirstChild($el)
    while ($c) {
        if ($c.Current.Name -eq $nameOrId -or $c.Current.AutomationId -eq $nameOrId) { return $c }
        $c = $walker.GetNextSibling($c)
    }
    return $null
}

function Find-Path($rootEl, [string[]]$path) {
    $el = $rootEl
    foreach ($step in $path) {
        $el = Find-Child $el $step
        if (-not $el) { return $null }
    }
    return $el
}

function Invoke-Element($el) {
    if (-not $el) { return $false }
    $pattern = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return $true }
    return $false
}
function Measure-State([string]$label) { & powershell -NoProfile -ExecutionPolicy Bypass -File $probe -Seconds $Window -Label $label }

$machine = (Get-CimInstance Win32_Processor | Select-Object -First 1)
Log "# Noct performance run: $Tag"
Log "# $(Get-Date -Format 'yyyy-MM-dd HH:mm')  exe=$Exe"
Log "# cpu=$($machine.Name.Trim()) logicalCores=$($machine.NumberOfLogicalProcessors)  os=$([Environment]::OSVersion.Version)"
Log "# library=$Library  window=${Window}s  cpu% is percent of ONE logical core"
Log ''

# ---------- empty library ----------
$emptyDir = Join-Path $root 'perfdata-empty'
if (Test-Path $emptyDir) { Remove-Item $emptyDir -Recurse -Force }
$app = Start-Noct $emptyDir
Log ("{0,-46} {1:F0} ms" -f 'start-empty (to visible main window)', $app.StartMs)
Start-Sleep -Seconds 6
Log (Measure-State 'idle-empty (main visible)')

[void][PerfW32]::SendMessage($app.Handle, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero)
Start-Sleep -Seconds 7
Log (Measure-State 'minimized (island auto-hidden)')
Stop-Noct

# ---------- loaded library ----------
$libDir = Join-Path $root 'perfdata-lib'
if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $libDir | Out-Null
$long = Join-Path $Library 'Long tone\Quiet tone 90s.flac'
$settings = [ordered]@{
    schemaVersion = 1
    libraryFolders = @($Library)
    outputBackend = 'WasapiShared'
    outputMode = 'Normal'
    volume = 0.5
    islandEnabled = $true
    gaplessEnabled = $true
    queuePaths = @($long)
    lastTrackPath = $long
}
$settings | ConvertTo-Json | Set-Content (Join-Path $libDir 'settings.json') -Encoding UTF8

$first = Get-Date
$app = Start-Noct $libDir
$indexFile = Join-Path $libDir 'library.json'
$deadline = (Get-Date).AddSeconds(120)
while (-not (Test-Path $indexFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
$scanMs = ((Get-Date) - $first).TotalMilliseconds
Log ("{0,-46} {1:F0} ms (3031 files; includes process start)" -f 'scan-first (to library index written)', $scanMs)
Start-Sleep -Seconds 2
Stop-Noct

$app = Start-Noct $libDir
Log ("{0,-46} {1:F0} ms" -f 'start-loaded (to visible main window)', $app.StartMs)
Start-Sleep -Seconds 8
Log (Measure-State 'idle-loaded (main visible, 3031 tracks)')

$rootEl = Get-Root $app.Process
$libraryNav = Find-Child $rootEl 'Library'
if ($libraryNav) { [void](Invoke-Element $libraryNav); Start-Sleep -Milliseconds 800 }
$songsToggle = Find-Child $rootEl 'All songs'
if ($songsToggle) { [void](Invoke-Element $songsToggle); Start-Sleep -Seconds 2 }

$libraryPage = Find-Child $rootEl 'Library'
$libraryPage = Get-Children $rootEl | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Custom' -and $_.Current.Name -eq 'Library' } | Select-Object -First 1
$list = $null
if ($libraryPage) { $list = Find-Child $libraryPage 'TrackList' }

if ($list) {
    $kids = Get-Children $list
    $realized = ($kids | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' }).Count
    Log ("{0,-46} {1} ListItem containers (of {2} children reported by UIA) for 3031 rows" -f 'list-realized (virtualization check)', $realized, $kids.Count)

    $pattern = $null
    if ($list.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
        $p = $app.Process
        $p.Refresh()
        $cpu0 = $p.TotalProcessorTime.TotalSeconds
        $t0 = Get-Date
        for ($i = 0; $i -lt 150; $i++) {
            $pattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::LargeIncrement)
            Start-Sleep -Milliseconds 40
        }
        $wall = ((Get-Date) - $t0).TotalSeconds
        $p.Refresh()
        $cpu = (($p.TotalProcessorTime.TotalSeconds - $cpu0) / $wall) * 100
        Log ("{0,-46} cpu={1:F2}%  (150 page scrolls over {2:F1}s)  workingSet={3}MB" -f 'scrolling (3031-row list)', $cpu, $wall, [int]($p.WorkingSet64 / 1MB))
        [void]$pattern.SetScrollPercent(-1, 0)
    }
} else {
    Log 'list-realized / scrolling: track list not found by UI Automation'
}

$rootEl = Get-Root $app.Process
$transport = Find-Child $rootEl 'Playback controls'
$play = $null
if ($transport) { $play = Find-Child $transport 'PlayButton' }
if ($play -and (Invoke-Element $play)) {
    Start-Sleep -Seconds 3
    Log (Measure-State 'playing (main visible, WASAPI shared)')
    [void][PerfW32]::SendMessage($app.Handle, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero)
    Start-Sleep -Seconds 7
    Log (Measure-State 'playing-hidden (island auto-hidden)')
} else {
    Log 'playing: could not find the Play button through UI Automation'
}
Stop-Noct
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$results | Set-Content $out -Encoding UTF8
Write-Output "written: $out"
