<#
Creates release assets from current source (always publishes again):
  dist\release\Noct-<version>-win-x64.zip
  dist\release\Noct-Setup-<version>-x64.exe
  dist\release\SHA256SUMS.txt

Use -ZipOnly if Inno Setup is unavailable. ZIP and installer contain the same app.
#>
[CmdletBinding()]
param(
    [string]$IsccPath = '',
    [switch]$ZipOnly
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishDir = Join-Path $root 'dist\Noct'
$releaseDir = [IO.Path]::GetFullPath((Join-Path $root 'dist\release'))
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Could not read an x.y.z Version from Directory.Build.props.' }

& (Join-Path $PSScriptRoot 'publish.ps1')
if (-not $ZipOnly) { & (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipPublish -IsccPath $IsccPath }

# Keep a release folder containing only this run, so old packages cannot be uploaded by accident.
if (-not $releaseDir.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must be inside the repository.'
}
$distDir = Join-Path $root 'dist'
if ((Get-Item -LiteralPath $distDir).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing to package through a linked dist directory.'
}
if (Test-Path -LiteralPath $releaseDir) {
    if ((Get-Item -LiteralPath $releaseDir).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to clear a linked release directory.'
    }
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$zipPath = Join-Path $releaseDir "Noct-$version-win-x64.zip"
$expected = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File | Sort-Object FullName)

# .NET Framework's CreateFromDirectory writes backslashes on Windows. Create
# entries explicitly so nested paths use the ZIP format's forward slash separator.
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $expected) {
        $relative = $file.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally {
    $archive.Dispose()
}

# Open the archive and verify that every expected file is present with the right length.
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($archive.Entries.Count -ne $expected.Count) { throw 'ZIP file count differs from publish output.' }
    foreach ($file in $expected) {
        $relative = $file.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
        $entry = $archive.GetEntry($relative)
        if (-not $entry -or $entry.Length -ne $file.Length) { throw "ZIP entry is missing or incomplete: $relative" }
    }
} finally {
    $archive.Dispose()
}

if (-not $ZipOnly) {
    Copy-Item -LiteralPath (Join-Path $root "dist\installer\Noct-Setup-$version-x64.exe") -Destination $releaseDir
}
$hashLines = Get-ChildItem -LiteralPath $releaseDir -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$hashLines | Set-Content -LiteralPath (Join-Path $releaseDir 'SHA256SUMS.txt') -Encoding ASCII
Write-Output 'Release assets:'
Get-ChildItem -LiteralPath $releaseDir -File | Select-Object Name, Length
