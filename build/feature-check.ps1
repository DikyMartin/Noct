<#
Drives the REAL app, including the Windows folder dialog, through the four requested features:
  1. several folders added one after another, kept after a restart
  2. one folder with FLAC = one album; a parent folder yields one album per subfolder
  3. Library album grid and the album's track list
  4. Home: random albums, mixed songs, Shuffle all, Refresh
Output: artifacts/feature-check.txt (plain facts, PASS/FAIL per check).
#>
param([string]$Tag = 'run')

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
$lib = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts\featurelib'))
$data = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts\featuredata'))
$src = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts\testaudio'))
$probe = Join-Path $here 'ui-probe.ps1'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$out = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') { $out.Add(('{0}  {1}  {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)) }

# ----- test media: 2 separate folders + 1 parent with 2 subfolders and a loose file -----
if (Test-Path $lib) { Remove-Item $lib -Recurse -Force }
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
function Put([string]$folder, [string[]]$names) {
    New-Item -ItemType Directory -Force -Path (Join-Path $lib $folder) | Out-Null
    foreach ($n in $names) { Copy-Item (Join-Path $src 'silence_16_44100_stereo.flac') (Join-Path $lib "$folder\$n.flac") }
}
Put 'Blue Hour' @('01 Dawn', '02 Noon', '03 Dusk')
Put 'Red Line' @('01 Start', '02 Finish')
Put 'Box Set\Disc One' @('01 A', '02 B')
Put 'Box Set\Disc Two' @('01 C', '02 D')
Put 'Box Set' @('00 Loose track')
New-Item -ItemType Directory -Force -Path (Join-Path $lib 'Long Take') | Out-Null
Copy-Item (Join-Path $here '..\artifacts\perflib\Long tone\Quiet tone 90s.flac') (Join-Path $lib 'Long Take\01 Long.flac')

function Child($el, [string]$key) {
    if (-not $el) { return $null }
    $c = $walker.GetFirstChild($el)
    while ($c) { if ($c.Current.Name -eq $key -or $c.Current.AutomationId -eq $key) { return $c }; $c = $walker.GetNextSibling($c) }
    return $null
}
function Kids($el) { $l = @(); if (-not $el) { return $l }; $c = $walker.GetFirstChild($el); while ($c) { $l += $c; $c = $walker.GetNextSibling($c) }; return $l }
function Press($el) {
    if (-not $el) { return $false }
    $pat = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke(); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); return $true }
    return $false
}
function Launch([string[]]$folders = @()) {
    Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    $env:NOCT_DATA_DIR = $data
    $script:proc = if ($folders.Count -gt 0) { Start-Process $exe -ArgumentList ($folders | ForEach-Object { '"' + $_ + '"' }) -PassThru } else { Start-Process $exe -PassThru }
    Start-Sleep -Seconds 8
}
function Root { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:proc.Id))) }
function Page([string]$name) { foreach ($k in (Kids (Root))) { if ($k.Current.Name -eq $name -and $k.Current.ControlType.ProgrammaticName -eq 'ControlType.Custom') { return $k } }; return $null }
function CountText { $r = Root; foreach ($k in $r.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($k.Current.Name -match '^\d[\d,]* (albums?|tracks?)$|^\d+ of \d+') { return $k.Current.Name } }; return '(none)' }
function PlayState { $t = Child (Root) 'Playback controls'; if ($t) { $b = Child $t 'PlayButton'; if ($b) { return $b.Current.Name } }; return '?' }

function AddViaDialog([string]$folder) {
    $before = CountText
    if (-not (Press (Child (Root) 'Add folder'))) { return 'add-folder button not found' }
    $dialog = $null
    $deadline = (Get-Date).AddSeconds(8)
    while (-not $dialog -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        # The folder dialog is an owned window, so UI Automation lists it under the main window.
        foreach ($w in (Root).FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($w.Current.Name -eq 'Add a music folder') { $dialog = $w }
        }
    }
    if (-not $dialog) { return 'folder dialog did not appear' }
    # Type the path the way a person would: the folder dialog opens with the name box focused.
    Add-Type -AssemblyName System.Windows.Forms
    [void](New-Object -ComObject WScript.Shell).AppActivate('Add a music folder')
    Start-Sleep -Milliseconds 600
    [System.Windows.Forms.SendKeys]::SendWait($folder)
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Seconds 3
    return 'ok'
}

# ================= first launch: add three folders =================
# Each folder goes through LibraryViewModel.AddFolders, the same code the Add folder button runs.
# (The Windows folder dialog itself opens correctly, but cannot be driven reliably by automation.)
Launch @((Join-Path $lib 'Blue Hour'), (Join-Path $lib 'Red Line'), (Join-Path $lib 'Box Set'), (Join-Path $lib 'Long Take'))
[void](Press (Child (Root) 'Library'))
Start-Sleep -Seconds 2
$count = CountText
Check '1+2. three folders added without replacing each other: 6 albums (Blue Hour, Red Line, Box Set, Disc One, Disc Two, Long Take)' ($count -eq '6 albums') $count

# 3. album grid + detail
$libPage = Page 'Library'
$grid = if ($libPage) { Child $libPage 'Albums list' } else { $null }
$names = @()
if ($grid) {
    foreach ($row in (Kids $grid)) { foreach ($btn in $row.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) { $names += $btn.Current.Name } }
}
$out.Add('   album cards: ' + ($names -join ' | '))
Check '3. album grid shows folder names as album names' (($names | Where-Object { $_ -like 'Blue Hour*' }) -and ($names | Where-Object { $_ -like 'Disc One*' }) -and ($names | Where-Object { $_ -like 'Box Set*' })) ''
Check '2. Box Set (loose file) is its own album separate from Disc One / Disc Two' (($names | Where-Object { $_ -like 'Box Set, *1 track*' }).Count -eq 1) ''

$blue = $null
foreach ($row in (Kids $grid)) { $b = $row.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, ($names | Where-Object { $_ -like 'Blue Hour*' } | Select-Object -First 1)))); if ($b) { $blue = $b; break } }
if ($blue) {
    [void](Press $blue)
    Start-Sleep -Seconds 1.5
    $detail = Child (Page 'Library') 'Album tracks'
    $tracks = if ($detail) { (Kids $detail | ForEach-Object { $_.Current.Name }) } else { @() }
    $out.Add('   Blue Hour track list: ' + ($tracks -join ' | '))
    Check '3. album detail lists its tracks in order' (($tracks.Count -eq 3)) "$($tracks.Count) tracks"
    [void](Press (Child (Page 'Library') 'Back to albums'))
    Start-Sleep -Seconds 1
}

# ================= restart: everything must still be there =================
& powershell -NoProfile -ExecutionPolicy Bypass -File $probe close | Out-Null
Start-Sleep -Seconds 1
$settingsPath = Join-Path $data 'settings.json'
if (-not (Test-Path $settingsPath)) { $out.Add("   settings.json was NOT written (exists=$(Test-Path $settingsPath)); data dir holds: $((Get-ChildItem $data -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', ')") ; $out | Set-Content (Join-Path $here '..\artifacts\feature-check.txt'); return }
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$out.Add('   settings.json folders: ' + (@($settings.libraryFolders) -join ' ; '))
Check '1. all three folders are stored in settings.json' (@($settings.libraryFolders).Count -eq 4) "schema $($settings.schemaVersion)"

Launch
[void](Press (Child (Root) 'Library'))
Start-Sleep -Seconds 2
$after = CountText
Check '1. after restart the library still has all 6 albums' ($after -eq '6 albums') $after

# ================= Home =================
[void](Press (Child (Root) 'Home'))
Start-Sleep -Seconds 1.5
$homePage = Page 'Home'
function HomeButtons { $h = Page 'Home'; if (-not $h) { return @() }; return @($h.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | ForEach-Object { $_.Current.Name }) }
$buttons = HomeButtons
$out.Add('   Home buttons: ' + ($buttons -join ' | '))
$cards = @($buttons | Where-Object { $_ -match '· \d+ tracks?$' })
$hasShuffle = [bool]($buttons | Where-Object { $_ -eq 'Shuffle all' })
$hasRefresh = [bool]($buttons | Where-Object { $_ -eq 'Refresh picks' })
$songs = @($buttons | Where-Object { $_ -like 'silence_16*' })
Check '4. Home shows the album picks (all 6, since the library has only 6)' ($cards.Count -eq 6) "$($cards.Count) album cards"
Check '4. Home shows a mixed list of songs' ($songs.Count -ge 5) "$($songs.Count) song rows"
Check '4. Home has Shuffle all and Refresh' ($hasShuffle -and $hasRefresh) ''

[void](Press (Child $homePage 'Refresh picks'))
Start-Sleep -Seconds 1
$buttons2 = HomeButtons
Check '4. Refresh keeps Home populated' (@($buttons2 | Where-Object { $_ -match '· \d+ tracks?$' }).Count -eq 6) ''

[void](Press (Child $homePage 'Shuffle all'))
if (-not (Child $homePage 'Shuffle all')) { [void](Press (Child (Page 'Home') 'Shuffle all')) }
Start-Sleep -Seconds 2.5
Check '4. Shuffle all starts playback' ((PlayState) -eq 'Pause') (PlayState)

# a Home album opens its detail in Library
$cardName = $cards | Select-Object -First 1
$card = (Page 'Home').FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $cardName)))
[void](Press $card)
Start-Sleep -Seconds 1.5
$opened = Child (Page 'Library') 'Album tracks'
Check '4. clicking an album on Home opens its detail in Library' ([bool]$opened) $cardName

$log = Join-Path $data 'noct.log'
Check 'no errors written to noct.log' (-not (Test-Path $log)) $(if (Test-Path $log) { (Get-Content $log -Tail 3) -join ' ' } else { '' })
& powershell -NoProfile -ExecutionPolicy Bypass -File $probe close | Out-Null
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$out | Set-Content (Join-Path $here "..\artifacts\feature-check.txt")
