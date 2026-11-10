<#
Builds a synthetic library for measurements from the reference FLAC files (no real music is used):
  120 albums x 25 tracks = 3000 small FLAC files, 30 of them carrying a 1.3 MB embedded cover,
  plus one 90 second quiet tone for playback tests.

Albums are separate folders. Every file carries the same ALBUM tag on purpose, so the folder, not
the tag, is what has to define an album.
#>
param(
    [string]$Root = "$PSScriptRoot\..\artifacts\perflib",
    [string]$Source = "$PSScriptRoot\..\artifacts\testaudio",
    [string]$Dotnet = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$ToolDirectory = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Xiph.FLAC_Microsoft.Winget.Source_8wekyb3d8bbwe\flac-1.5.0-win\Win64"
)
$ErrorActionPreference = 'Stop'
$Root = [System.IO.Path]::GetFullPath($Root)
$Source = (Resolve-Path $Source).Path
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Root | Out-Null

$small = @('silence_16_44100_stereo', 'dc_16_44100_stereo', 'extremes_16_44100_stereo', 'extremes_24_44100_stereo')
$albums = 120
$perAlbum = 25
for ($a = 1; $a -le $albums; $a++) {
    $dir = Join-Path $Root ("Album {0:D3}" -f $a)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    for ($t = 1; $t -le $perAlbum; $t++) {
        $name = $small[($a + $t) % $small.Count]
        Copy-Item (Join-Path $Source "$name.flac") (Join-Path $dir ("{0:D2} Track {0:D2}.flac" -f $t))
    }
    if ($a -le 30) {
        Copy-Item (Join-Path $Source 'withart_16_44100_stereo.flac') (Join-Path $dir '26 Cover track.flac')
    }
}

$wav = Join-Path $Root '_long.wav'
& $Dotnet run --project "$PSScriptRoot\..\tools\Noct.Diagnostics" -c Release -- tone $wav 90 44100 0.04 | Out-Null
$longDir = Join-Path $Root 'Long tone'
New-Item -ItemType Directory -Force -Path $longDir | Out-Null
& (Join-Path $ToolDirectory 'flac.exe') -5 --force --silent -o (Join-Path $longDir 'Quiet tone 90s.flac') $wav
Remove-Item $wav -Force

$files = Get-ChildItem $Root -Recurse -Filter *.flac
"perflib: $($files.Count) files in $((Get-ChildItem $Root -Directory).Count) folders, $([math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)) MB at $Root"
