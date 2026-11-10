# Verifies volume handling on the real app: slider and percentage label agree for 0/25/70/100 %,
# before playback and during playback; keyboard, track click and drag; the audio level Windows sees
# (session peak meter via Noct.Diagnostics "meter"); and persistence across a restart.
# Output: artifacts\volume-check.txt
param([string]$Exe = '', [string]$DataDir = "$PSScriptRoot\..\artifacts\voldata")
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = "$PSScriptRoot\..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe" }
$meterExe = "$PSScriptRoot\..\tools\Noct.Diagnostics\bin\Release\net10.0-windows\win-x64\Noct.Diagnostics.exe"
$env:NOCT_DATA_DIR = $DataDir
$env:NOCT_EXE = $Exe
. {
    $src = Get-Content "$PSScriptRoot\lyrics-check.ps1" -Raw
    $helpers = $src.Substring(0, $src.IndexOf("Remove-Item `$log"))
    $helpers = $helpers -replace 'param\(\[string\]\$DataDir[^\)]*\)', ''
    $helpers = $helpers -replace '\$here = Split-Path[^\r\n]*', '$here = $PSScriptRoot'
    Invoke-Expression $helpers
}
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Msg {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
$out = "$PSScriptRoot\..\artifacts\volume-check.txt"
function Say([string]$s) { Add-Content -Path $out -Value $s -Encoding UTF8 }
$Range = [System.Windows.Automation.RangeValuePattern]::Pattern

function Slider() { Find 'Volume' }
function Slider-Value() { (Slider).GetCurrentPattern($Range).Current.Value }
function Set-Slider([double]$v) { (Slider).GetCurrentPattern($Range).SetValue($v) }
function Label() {
    $w = Find 'Playback controls'
    $texts = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($t in $texts) { if ($t.Current.Name -match '^(\d+%|DAC)$') { return $t.Current.Name } }
    return '(label not found)'
}
function Row([string]$what) {
    $v = Slider-Value; $l = Label
    $pct = [int][math]::Round($v * 100)
    $agree = ($l -eq "$pct%")
    Say ("  {0,-34} slider={1,6:N3} ({2,3}%)  label={3,-5} agree={4}" -f $what, $v, $pct, $l, $agree)
}
function Meter([double]$seconds = 1.0, [double]$wait = 0) {
    $pidNow = $script:p.Id
    (& $meterExe meter $pidNow $seconds 10 $wait) -join ' '
}
function Press([int]$vk) {
    $h = [IntPtr](Main-Window $script:p).Current.NativeWindowHandle
    [void][Msg]::PostMessage($h, 0x100, [IntPtr]$vk, [IntPtr]0); Start-Sleep -Milliseconds 40
    [void][Msg]::PostMessage($h, 0x101, [IntPtr]$vk, [IntPtr]0xC0000001); Start-Sleep -Milliseconds 250
    if (-not (Slider)) { Say ("  !! after key 0x{0:X}: main window no longer in the automation tree (hidden/minimized?)" -f $vk) }
}
function Launch([string]$arg = '') {
    Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if ($arg) { $script:p = Start-Process $Exe -ArgumentList $arg -PassThru } else { $script:p = Start-Process $Exe -PassThru }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not (Slider)) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1
}
function Play-LongTake() {
    [void](Click (Find 'Library')); Start-Sleep 1
    $b = Find 'Back to albums'; if ($b) { [void](Click $b); Start-Sleep 1 }
    $w = Main-Window $script:p
    $card = $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Current.Name -like 'Long Take*' } | Select-Object -First 1
    [void](Click $card); Start-Sleep 1.2
    [void](Click (Find 'Play album'))
}

Remove-Item $out -ErrorAction SilentlyContinue
try {
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
Say "exe: $Exe"
Launch ('"{0}"' -f (Join-Path $PSScriptRoot '..\artifacts\featurelib\Long Take'))

Say '=== A. Before playback (nothing is playing, no output open)'
Say "  play button: $(if (Find 'Play') { 'Play (stopped)' } else { '?' }); slider enabled: $((Slider).Current.IsEnabled)"
Row 'initial (default 70%)'
foreach ($v in 0, 0.25, 0.7, 1.0) { Set-Slider $v; Start-Sleep -Milliseconds 250; Row ("set {0:P0}" -f $v) }

Say '=== B. Keyboard, track click and drag (still before playback)'
Set-Slider 0.5; Start-Sleep -Milliseconds 200
(Slider).SetFocus(); Start-Sleep -Milliseconds 300
Press 0x27; Row 'Right arrow (+5%)'
Press 0x27; Row 'Right arrow (+5%)'
Press 0x25; Row 'Left arrow (-5%)'
Press 0x21; Row 'Page Up (+10%)'
Press 0x22; Row 'Page Down (-10%)'
Press 0x24; Row 'Home (0%)'
Press 0x23; Row 'End (100%)'
Set-Slider 0.3; Start-Sleep -Milliseconds 200
$r = (Slider).Current.BoundingRectangle
$y = [int]($r.Y + $r.Height / 2)
[void][Mouse]::SetCursorPos([int]($r.X + $r.Width * 0.95), $y); Start-Sleep -Milliseconds 200
[Mouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); [Mouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
Row 'click track right of thumb (+10%)'
[Mouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); [Mouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
Row 'click track again (+10%)'
[void][Mouse]::SetCursorPos([int]($r.X + $r.Width * 0.05), $y); Start-Sleep -Milliseconds 200
[Mouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); [Mouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
Row 'click track left of thumb (-10%)'
# Drag the thumb: press on it, move in steps, read the label WHILE the button is still down.
$now = Slider-Value
$thumbX = [int]($r.X + 6 + ($r.Width - 12) * $now)
[void][Mouse]::SetCursorPos($thumbX, $y); Start-Sleep -Milliseconds 200
[Mouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150
foreach ($f in 0.15, 0.4, 0.62, 0.9) {
    [void][Mouse]::SetCursorPos([int]($r.X + 6 + ($r.Width - 12) * $f), $y); Start-Sleep -Milliseconds 250
    Row ("dragging to ~{0:P0} (button held)" -f $f)
}
[Mouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
Row 'drag released'

Say '=== C. Volume chosen before playback is applied when playback starts'
Set-Slider 0.25; Start-Sleep -Milliseconds 300
Row 'chosen before playback'
Play-LongTake
# The tool's own session list is read once per process, so poll with a fresh process until the session shows up.
$first = '(session not seen within 4 s)'
$deadline = (Get-Date).AddSeconds(4)
do {
    $r = (& $meterExe meter $p.Id 0.15 5 0) -join ' '
    if ($r -notlike 'none*') { $first = $r; break }
} while ((Get-Date) -lt $deadline)
Say ("  first sight of the audio session after Play at 25%: " + $first)
Start-Sleep -Milliseconds 500
Say "  playback: $(if (Find 'Pause') { 'playing' } else { 'not playing' })"
Row 'after start (label/slider unchanged)'

Say '=== D. During playback (peak = what Windows measures for Noct''s audio session)'
foreach ($v in 1.0, 0.7, 0.25, 0, 0.7) {
    Set-Slider $v; Start-Sleep -Milliseconds 500
    Row ("set {0:P0}" -f $v)
    Say ("      meter: " + (Meter 0.8))
}
(Slider).SetFocus(); Start-Sleep -Milliseconds 200
Press 0x27; Row 'Right arrow during playback'
Say ("      meter: " + (Meter 0.6))
Press 0x21; Row 'Page Up during playback'
Say ("      meter: " + (Meter 0.6))

Say '=== E. Restart restores the saved volume'
Set-Slider 0.37; Start-Sleep -Milliseconds 1500
Row 'before closing'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" close | Out-Null
Start-Sleep -Milliseconds 800
$saved = (Select-String -Path "$DataDir\settings.json" -Pattern '"volume"').Line.Trim()
Say "  settings.json after exit: $saved"
Launch
Row 'after restart, nothing playing'
Play-LongTake; Start-Sleep -Seconds 2
Row 'after restart, playing'
Say ("      meter: " + (Meter 0.8))
Set-Slider 1.0; Start-Sleep -Milliseconds 500
Say ("      meter at 100%: " + (Meter 0.8))

Say '=== F. Transparent mode keeps its restriction (WASAPI Exclusive + Transparent, volume 25% stored)'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" close | Out-Null
Start-Sleep -Milliseconds 800
function Edit-Settings([scriptblock]$change) {
    $f = "$DataDir\settings.json"
    $s = Get-Content $f -Raw | ConvertFrom-Json
    & $change $s
    $s | ConvertTo-Json -Depth 6 | Set-Content $f -Encoding UTF8
}
Edit-Settings { param($s) $s.outputBackend = 'WasapiExclusive'; $s.outputMode = 'Transparent'; $s.volume = 0.25 }
Launch
Say ("  before playback: slider enabled={0}  label={1}" -f (Slider).Current.IsEnabled, (Label))
Play-LongTake; Start-Sleep -Seconds 3
Say ("  after pressing play: playing={0}; slider enabled={1}  label={2}" -f [bool](Find 'Pause'), (Slider).Current.IsEnabled, (Label))
$n = (Meter 0.6 0) ; Say ("      meter: " + $n)
& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" close | Out-Null
Start-Sleep -Milliseconds 800
Say ("  settings.json after exit: " + ((Select-String -Path "$DataDir\settings.json" -Pattern '"volume"|"outputMode"|"outputBackend"').Line.Trim() -join ' '))
Edit-Settings { param($s) $s.outputBackend = 'WasapiShared'; $s.outputMode = 'Normal' }
Launch
Say ("  back in Normal / shared: slider enabled={0}  label={1}  (the 25% chosen earlier is still there)" -f (Slider).Current.IsEnabled, (Label))
Say 'DONE'
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
} catch {
    Say ('FAILED: ' + $_.Exception.Message + ' at line ' + $_.InvocationInfo.ScriptLineNumber)
    foreach ($l in (& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\ui-probe.ps1" state)) { Say ('  ' + $l) }
}
