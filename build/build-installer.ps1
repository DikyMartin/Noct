<#
Builds the Noct installer from source, start to finish:
  1. build\publish.ps1 (Release, win-x64, self-contained) into dist\Noct
  2. compile installer\Noct.iss with the official Inno Setup compiler (ISCC.exe)
  3. report the result: dist\installer\Noct-Setup-<version>-x64.exe

The version comes from Directory.Build.props. Inno Setup 6 is found via -IsccPath, $env:ISCC_PATH, PATH, or its
usual install folders (winget install JRSoftware.InnoSetup --scope user).

  powershell -ExecutionPolicy Bypass -File build\build-installer.ps1
  powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -SkipPublish   # reuse dist\Noct from build\publish.ps1

Use build\package.ps1 for a ZIP, installer and SHA256SUMS.txt together.
#>
param(
    [string]$IsccPath = '',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishDir = [IO.Path]::GetFullPath((Join-Path $root 'dist\Noct'))
$installerDir = [IO.Path]::GetFullPath((Join-Path $root 'dist\installer'))

function Fail([string]$message) { Write-Error $message; exit 1 }

# Validate all output parents even with -SkipPublish, which bypasses publish.ps1.
foreach ($generatedDir in @((Join-Path $root 'dist'), $publishDir, $installerDir)) {
    $absoluteDir = [IO.Path]::GetFullPath($generatedDir)
    if (-not $absoluteDir.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Fail 'Installer paths must stay inside the repository.'
    }
    if (Test-Path -LiteralPath $absoluteDir) {
        $directory = Get-Item -LiteralPath $absoluteDir
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Fail 'Refusing to build an installer through a linked directory or non-directory path.'
        }
    }
}

# --- version -----------------------------------------------------------------------------------
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+$') { Fail "Could not read a x.y.z <Version> from Directory.Build.props (got '$version')." }
Write-Output "version: $version"

# --- Inno Setup compiler ------------------------------------------------------------------------
if (-not $IsccPath) { $IsccPath = $env:ISCC_PATH }
if (-not $IsccPath) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | ForEach-Object Source),
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $IsccPath = $candidates | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) { Fail 'ISCC.exe not found. Install it with: winget install JRSoftware.InnoSetup --scope user' }
Write-Output "compiler: $IsccPath"

# --- publish --------------------------------------------------------------------------------------
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1')
}
foreach ($required in 'Noct.exe', 'Noct.dll', 'Noct.Core.dll', 'NAudio.Wasapi.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'README.md', 'THIRD_PARTY_NOTICES.md') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { Fail "Publish output is incomplete: $required is missing from $publishDir." }
}
$assemblyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDir 'Noct.dll')).FileVersion
if ($assemblyVersion -ne "$version.0") { Fail "Publish version $assemblyVersion differs from source version $version. Run build\publish.ps1 first." }
$files = Get-ChildItem $publishDir -Recurse -File
Write-Output ("publish: {0} files, {1:N1} MB in {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $publishDir)

# --- compile --------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $installerDir | Out-Null
$output = Join-Path $installerDir "Noct-Setup-$version-x64.exe"
if (Test-Path $output) { Remove-Item $output -Force }
& $IsccPath "/DAppVersion=$version" "/DSourceDir=$publishDir" (Join-Path $root 'installer\Noct.iss')
if ($LASTEXITCODE -ne 0) { Fail "ISCC failed (exit $LASTEXITCODE)." }
if (-not (Test-Path $output)) { Fail "ISCC reported success but $output does not exist." }

$item = Get-Item $output
Write-Output ''
Write-Output "installer: $($item.FullName)"
Write-Output ("size:      {0:N1} MB" -f ($item.Length / 1MB))
Write-Output "sha256:    $((Get-FileHash $output -Algorithm SHA256).Hash)"
