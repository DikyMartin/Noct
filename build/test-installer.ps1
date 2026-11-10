# Exercises the built installer on this machine and writes plain facts to artifacts\installer-test.txt.
# Uses a separate NOCT_DATA_DIR for every launch; the real %LocalAppData%\Noct is only checked, never written.
# Run after build\build-installer.ps1. Test 7 (data purge) needs -IncludePurge and moves the real data folder
# aside for its duration (it is put back in a finally block).
param([switch]$IncludePurge)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$setup = Get-ChildItem (Join-Path $root 'dist\installer\Noct-Setup-*-x64.exe') | Sort-Object LastWriteTime | Select-Object -Last 1
$out = Join-Path $root 'artifacts\installer-test.txt'
$logDir = Join-Path $root 'artifacts\installer-logs'
$dataDir = Join-Path $root 'artifacts\insttest-data'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Noct'
$realData = Join-Path $env:LOCALAPPDATA 'Noct'
$uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{244C66B4-07CE-415C-AF8B-BD870EB3DCD8}_is1'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Noct.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Noct.lnk'
New-Item -ItemType Directory -Force $logDir | Out-Null
Remove-Item $out -ErrorAction SilentlyContinue
function Say([string]$s) { Add-Content -Path $out -Value $s -Encoding UTF8 }
function Hashes([string]$dir) {
    if (-not (Test-Path $dir)) { return '(folder absent)' }
    (Get-ChildItem $dir -Recurse -File | Sort-Object FullName | ForEach-Object { "{0}={1}" -f $_.FullName.Substring($dir.Length), (Get-FileHash $_.FullName -Algorithm MD5).Hash.Substring(0, 8) }) -join '; '
}
function Run-Setup([string]$name, [string]$extra = '') {
    $log = Join-Path $logDir "$name.log"
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=`"$log`" $extra"
    $p = Start-Process $setup.FullName -ArgumentList $arguments -Wait -PassThru
    return $p.ExitCode
}
function Run-Uninstall([string]$name, [string]$extra = '') {
    $log = Join-Path $logDir "$name.log"
    $p = Start-Process (Join-Path $installDir 'unins000.exe') -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`" $extra" -Wait -PassThru
    return $p.ExitCode
}
function Launch-Noct([string]$target) {
    $env:NOCT_DATA_DIR = $dataDir
    $p = Start-Process $target -PassThru
    $deadline = (Get-Date).AddSeconds(40)
    do { Start-Sleep -Milliseconds 500; $p.Refresh() } while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline -and -not $p.HasExited)
    Remove-Item Env:NOCT_DATA_DIR
    return $p
}
function Seed-Data() {
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    $folder = (Join-Path $root 'artifacts\featurelib\Long Take')
    (@{ schemaVersion = 3; libraryFolders = @($folder); volume = 0.42 } | ConvertTo-Json) | Set-Content (Join-Path $dataDir 'settings.json') -Encoding UTF8
}
function Data-State() {
    $f = Join-Path $dataDir 'settings.json'
    if (-not (Test-Path $f)) { return 'settings.json missing' }
    $s = Get-Content $f -Raw | ConvertFrom-Json
    "volume=$($s.volume) folders=$(@($s.libraryFolders).Count) library.json=$(Test-Path (Join-Path $dataDir 'library.json'))"
}
function Wait-StartApp([bool]$present, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { $found = [bool](Get-StartApps | Where-Object { $_.Name -eq 'Noct' }); if ($found -eq $present) { break }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $deadline)
    return ($found -eq $present)
}function Lnk([string]$path) { $s = (New-Object -ComObject WScript.Shell).CreateShortcut($path); "target=$($s.TargetPath) icon=$($s.IconLocation)" }

Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
$musicBefore = Hashes (Join-Path $root 'artifacts\featurelib')
$realBefore = Hashes $realData
Say "installer: $($setup.FullName)  ($([math]::Round($setup.Length / 1MB, 1)) MB)"
Say "elevated: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"
Say "real user data before: $realBefore"

try {
    Say '=== 1. Fresh install (silent, no desktop shortcut task)'
    if (Test-Path $installDir) { Say '  note: an existing install was present' }
    $code = Run-Setup 'install-1'
    Say "  setup exit code: $code"
    Say "  install folder: $installDir  exists=$(Test-Path $installDir)  files=$((Get-ChildItem $installDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count)"
    Say "  Noct.exe present: $(Test-Path (Join-Path $installDir 'Noct.exe'))   unins000.exe present: $(Test-Path (Join-Path $installDir 'unins000.exe'))"
    Say "  Start Menu shortcut: exists=$(Test-Path $startMenu)  $(if (Test-Path $startMenu) { Lnk $startMenu })"
    Say "  desktop shortcut (not selected): exists=$(Test-Path $desktop)"
    Say "  Noct appears in the Windows Start apps list (All apps / Search source): $(Wait-StartApp $true)"

    Say '=== 2. Installed apps registration'
    if (Test-Path $uninstKey) {
        $k = Get-ItemProperty $uninstKey
        Say "  DisplayName=$($k.DisplayName)  DisplayVersion=$($k.DisplayVersion)  Publisher=$($k.Publisher)"
        Say "  DisplayIcon=$($k.DisplayIcon)  (exists=$(Test-Path ($k.DisplayIcon -replace ',\d+$','')))"
        Say "  InstallLocation=$($k.InstallLocation)"
        Say "  UninstallString=$($k.UninstallString)  (exists=$(Test-Path ($k.UninstallString.Trim('"'))))"
        Say "  registered under HKCU (per-user): True"
    } else { Say '  uninstall key NOT found' }

    Say '=== 3. Launch the installed application (from the Start Menu shortcut)'
    Seed-Data
    $p = Launch-Noct $startMenu
    $p.Refresh()
    Say "  running=$(-not $p.HasExited)  responding=$($p.Responding)  window title='$($p.MainWindowTitle)'  path=$($p.Path)"
    Say "  path is the installed copy: $($p.Path -eq (Join-Path $installDir 'Noct.exe'))"
    Start-Sleep -Seconds 3
    Start-Sleep -Seconds 6
    $before = Data-State
    Say "  user data (seeded volume 0.42 and one library folder) as the app sees it: $before"

    Say '=== 4. Update while Noct is running (window visible)'
    Set-Content (Join-Path $installDir 'stale-from-old-version.txt') 'old'
    $code = Run-Setup 'update-visible'
    Start-Sleep -Seconds 1
    $log = Get-Content (Join-Path $logDir 'update-visible.log') -Raw
    Say "  setup exit code: $code"
    Say "  Noct still running after update: $(@(Get-Process Noct -ErrorAction SilentlyContinue).Count -gt 0)"
    Say "  closed on request (no forced end in log): $(-not ($log -match 'did not close on request'))"
    Say "  stale file from the old version removed: $(-not (Test-Path (Join-Path $installDir 'stale-from-old-version.txt')))"
    Say "  user data after update: $(Data-State)   unchanged: $((Data-State) -eq $before)"
    Say "  still exactly one uninstall entry: $((Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object { $_.PSChildName -like '*244C66B4*' } | Measure-Object).Count -eq 1)"

    Say '=== 5. Update while Noct is hidden in the island'
    $p = Launch-Noct $startMenu
    $env:NOCT_EXE = (Join-Path $installDir 'Noct.exe')
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'ui-probe.ps1') minimize | Out-Null
    Remove-Item Env:NOCT_EXE
    Start-Sleep -Seconds 2
    $code = Run-Setup 'update-island'
    $log = Get-Content (Join-Path $logDir 'update-island.log') -Raw
    Say "  setup exit code: $code"
    Say "  Noct still running after update: $(@(Get-Process Noct -ErrorAction SilentlyContinue).Count -gt 0)"
    Say "  closed on request (no forced end in log): $(-not ($log -match 'did not close on request'))"
    Say "  user data after update: $(Data-State)   unchanged: $((Data-State) -eq $before)"

    Say '=== 6. Reinstall with the desktop shortcut task, then check it'
    $code = Run-Setup 'install-desktop' '/TASKS="desktopicon"'
    Say "  setup exit code: $code  desktop shortcut exists=$(Test-Path $desktop)  $(if (Test-Path $desktop) { Lnk $desktop })"

    Say '=== 7. Uninstall (silent): program removed, data and music kept'
    $p = Launch-Noct $startMenu
    $code = Run-Uninstall 'uninstall-1'
    Start-Sleep -Seconds 1
    Say "  uninstaller exit code: $code"
    Say "  Noct closed by uninstaller: $(@(Get-Process Noct -ErrorAction SilentlyContinue).Count -eq 0)"
    Say "  install folder gone: $(-not (Test-Path (Join-Path $installDir 'Noct.exe')))  (folder exists: $(Test-Path $installDir))"
    Say "  Start Menu shortcut gone: $(-not (Test-Path $startMenu))   desktop shortcut gone: $(-not (Test-Path $desktop))"
    Say "  uninstall registration gone: $(-not (Test-Path $uninstKey))"
    Say "  Noct gone from the Start apps list: $(Wait-StartApp $false)"
    Say "  test user data kept after uninstall: $(Data-State)"
    Say "  music and .lrc files unchanged: $((Hashes (Join-Path $root 'artifacts\featurelib')) -eq $musicBefore)"
    Say "  real user data unchanged: $((Hashes $realData) -eq $realBefore)"

    if ($IncludePurge) {
        Say '=== 8. Uninstall with /PURGEDATA=1 (real data folder moved aside during this test)'
        $aside = "$realData.testaside"
        if (Test-Path $aside) { throw "$aside already exists; refusing to continue." }
        if (Test-Path $realData) { Move-Item $realData $aside }
        try {
            [void](Run-Setup 'install-2')
            New-Item -ItemType Directory -Force $realData | Out-Null
            Set-Content (Join-Path $realData 'sentinel.json') '{}'
            $keep = Join-Path $root 'artifacts\featurelib\Long Take\01 Long.lrc'
            $code = Run-Uninstall 'uninstall-purge' '/PURGEDATA=1'
            Say "  uninstaller exit code: $code  data folder removed: $(-not (Test-Path $realData))  lrc file still there: $(Test-Path $keep)"
        } finally {
            if (Test-Path $realData) { Remove-Item $realData -Recurse -Force }
            if (Test-Path $aside) { Move-Item $aside $realData }
        }
        Say "  real user data restored unchanged: $((Hashes $realData) -eq $realBefore)"
    }
} catch {
    Say ('FAILED: ' + $_.Exception.Message + ' at line ' + $_.InvocationInfo.ScriptLineNumber)
}
Say "real user data after everything: unchanged=$((Hashes $realData) -eq $realBefore)"
Say 'DONE'
