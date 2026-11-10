<#
Generates deterministic WAV material and encodes it with the Xiph reference encoder
(flac 1.5.0) so the managed decoder can be checked against known integer samples.

The WAVs are written by this script, the FLACs are produced by the reference encoder,
and a sibling .pcm file holds the exact little-endian integer samples that a correct
decoder must reproduce. Nothing here ships with the application.

This file is deliberately ASCII only: Windows PowerShell 5.1 reads .ps1 files using the
ANSI code page, so non-ASCII literals would be corrupted. Unicode tags are written to a
UTF-8 file from .NET and imported with "metaflac --no-utf8-convert".
#>
param(
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\testaudio",
    [string]$ToolDirectory = ""
)

$ErrorActionPreference = 'Stop'

if ($ToolDirectory) {
    $FlacExe = Join-Path $ToolDirectory 'flac.exe'
    $MetaflacExe = Join-Path $ToolDirectory 'metaflac.exe'
    if (-not (Test-Path -LiteralPath $FlacExe -PathType Leaf)) { throw "Reference encoder not found at $FlacExe" }
    if (-not (Test-Path -LiteralPath $MetaflacExe -PathType Leaf)) { throw "metaflac not found at $MetaflacExe" }
}
else {
    $flacCommand = Get-Command flac.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $metaflacCommand = Get-Command metaflac.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $flacCommand -or -not $metaflacCommand) {
        throw 'Install the Xiph FLAC tools and add flac.exe and metaflac.exe to PATH, or pass -ToolDirectory with their folder.'
    }
    $FlacExe = $flacCommand.Source
    $MetaflacExe = $metaflacCommand.Source
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

function Write-Wav {
    param([string]$Path, [int]$SampleRate, [int]$BitsPerSample, [int]$Channels, [int[]]$Samples)

    # Always WAVE_FORMAT_EXTENSIBLE: the reference encoder rejects >2 channels without it and
    # warns about 24-bit in a legacy fmt chunk.
    $bytesPerSample = $BitsPerSample / 8
    $dataBytes = $Samples.Length * $bytesPerSample
    $channelMask = switch ($Channels) { 1 { 0x4 } 2 { 0x3 } 4 { 0x33 } 6 { 0x3F } default { 0x3 } }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
        $bw.Write([int](60 + $dataBytes))
        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('fmt '))
        $bw.Write([int]40)
        $bw.Write([int16]-2)                      # 0xFFFE WAVE_FORMAT_EXTENSIBLE
        $bw.Write([int16]$Channels)
        $bw.Write([int]$SampleRate)
        $bw.Write([int]($SampleRate * $Channels * $bytesPerSample))
        $bw.Write([int16]($Channels * $bytesPerSample))
        $bw.Write([int16]$BitsPerSample)
        $bw.Write([int16]22)                      # cbSize
        $bw.Write([int16]$BitsPerSample)          # wValidBitsPerSample
        $bw.Write([int]$channelMask)
        $bw.Write(([guid]'00000001-0000-0010-8000-00aa00389b71').ToByteArray())
        $bw.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
        $bw.Write([int]$dataBytes)
        [byte[]]$raw = Get-RawPcm -BitsPerSample $BitsPerSample -Samples $Samples
        $bw.Write($raw, 0, $raw.Length)
    }
    finally { $bw.Dispose(); $fs.Dispose() }
}

function Get-RawPcm {
    param([int]$BitsPerSample, [int[]]$Samples)
    $bytesPerSample = [int][Math]::Floor(($BitsPerSample + 7) / 8)
    $raw = New-Object byte[] ($Samples.Length * $bytesPerSample)
    for ($i = 0; $i -lt $Samples.Length; $i++) {
        $v = $Samples[$i]
        $o = $i * $bytesPerSample
        for ($b = 0; $b -lt $bytesPerSample; $b++) {
            $raw[$o + $b] = [byte](($v -shr (8 * $b)) -band 0xFF)
        }
    }
    # The leading comma stops PowerShell from unrolling the array into the pipeline.
    return , $raw
}

function New-Samples {
    param([int]$SampleRate, [int]$BitsPerSample, [int]$Channels, [int]$Frames, [int]$Seed, [string]$Shape)

    $max = [int]([Math]::Pow(2, $BitsPerSample - 1) - 1)
    $min = -$max - 1
    $rng = New-Object System.Random($Seed)
    $out = New-Object int[] ($Frames * $Channels)
    $pattern = @(0, $max, $min, 1, -1, [int]($max / 2), [int]($min / 2))

    for ($n = 0; $n -lt $Frames; $n++) {
        for ($c = 0; $c -lt $Channels; $c++) {
            switch ($Shape) {
                'silence'  { $v = 0 }
                'dc'       { $v = [int]($max * 0.25) }
                'noise'    { $v = [int][Math]::Round(($rng.NextDouble() * 2 - 1) * $max) }
                'extremes' { $v = $pattern[($n + $c) % $pattern.Length] }
                default {
                    # Sum of tones plus a little noise: forces real LPC and rice paths.
                    $t = $n / $SampleRate
                    $a = [Math]::Sin(2 * [Math]::PI * (440 + 110 * $c) * $t)
                    $b = 0.35 * [Math]::Sin(2 * [Math]::PI * (1870 + 37 * $c) * $t)
                    $d = 0.02 * ($rng.NextDouble() * 2 - 1)
                    $v = [int][Math]::Round(($a * 0.55 + $b + $d) * $max * 0.8)
                }
            }
            if ($v -gt $max) { $v = $max }
            if ($v -lt $min) { $v = $min }
            $out[$n * $Channels + $c] = $v
        }
    }
    return , $out
}

# Title values carry Japanese and Chinese text to prove UTF-8 Vorbis comments round-trip.
$unicodeSuffix = [string]::Join('', [char]0x30C6, [char]0x30B9, [char]0x30C8, [char]0x0020, [char]0x6D4B, [char]0x8BD5)

$cases = @(
    @{ Name = 'tone_16_44100_stereo';     Rate = 44100; Bits = 16; Ch = 2; Frames = 44100; Shape = 'tone';     Level = '5' },
    @{ Name = 'tone_16_44100_mono';       Rate = 44100; Bits = 16; Ch = 1; Frames = 22050; Shape = 'tone';     Level = '8' },
    @{ Name = 'tone_24_48000_stereo';     Rate = 48000; Bits = 24; Ch = 2; Frames = 48000; Shape = 'tone';     Level = '5' },
    @{ Name = 'tone_24_96000_stereo';     Rate = 96000; Bits = 24; Ch = 2; Frames = 48000; Shape = 'tone';     Level = '8' },
    @{ Name = 'noise_16_44100_stereo';    Rate = 44100; Bits = 16; Ch = 2; Frames = 30000; Shape = 'noise';    Level = '0' },
    @{ Name = 'noise_24_48000_stereo';    Rate = 48000; Bits = 24; Ch = 2; Frames = 30000; Shape = 'noise';    Level = '8' },
    @{ Name = 'silence_16_44100_stereo';  Rate = 44100; Bits = 16; Ch = 2; Frames = 20000; Shape = 'silence';  Level = '5' },
    @{ Name = 'dc_16_44100_stereo';       Rate = 44100; Bits = 16; Ch = 2; Frames = 10000; Shape = 'dc';       Level = '5' },
    @{ Name = 'extremes_16_44100_stereo'; Rate = 44100; Bits = 16; Ch = 2; Frames = 10000; Shape = 'extremes'; Level = '8' },
    @{ Name = 'extremes_24_44100_stereo'; Rate = 44100; Bits = 24; Ch = 2; Frames = 10000; Shape = 'extremes'; Level = '8' },
    @{ Name = 'tone_16_88200_quad';       Rate = 88200; Bits = 16; Ch = 4; Frames = 20000; Shape = 'tone';     Level = '5' },
    @{ Name = 'tone_16_22050_stereo';     Rate = 22050; Bits = 16; Ch = 2; Frames = 20000; Shape = 'tone';     Level = '2' }
)

$summary = @()
$seed = 1
$tagFile = Join-Path $OutputDirectory '_tags.txt'

foreach ($case in $cases) {
    $wav = Join-Path $OutputDirectory "$($case.Name).wav"
    $flac = Join-Path $OutputDirectory "$($case.Name).flac"
    $pcm = Join-Path $OutputDirectory "$($case.Name).pcm"

    $samples = New-Samples -SampleRate $case.Rate -BitsPerSample $case.Bits -Channels $case.Ch -Frames $case.Frames -Seed ($seed++) -Shape $case.Shape
    Write-Wav -Path $wav -SampleRate $case.Rate -BitsPerSample $case.Bits -Channels $case.Ch -Samples $samples
    [System.IO.File]::WriteAllBytes($pcm, (Get-RawPcm -BitsPerSample $case.Bits -Samples $samples))

    if (Test-Path $flac) { Remove-Item $flac -Force }
    & $FlacExe "-$($case.Level)" --force --silent --no-padding -o $flac $wav
    if ($LASTEXITCODE -ne 0) { throw "flac encode failed for $($case.Name)" }

    $tagLines = @(
        "TITLE=$($case.Name) $unicodeSuffix",
        'ARTIST=Noct Reference Encoder',
        'ALBUM=Decoder verification set',
        'TRACKNUMBER=1'
    )
    [System.IO.File]::WriteAllText($tagFile, [string]::Join("`n", $tagLines) + "`n", (New-Object System.Text.UTF8Encoding($false)))
    & $MetaflacExe --no-utf8-convert "--import-tags-from=$tagFile" $flac
    if ($LASTEXITCODE -ne 0) { throw "metaflac tag import failed for $($case.Name)" }

    # The reference decoder confirms the file is valid before the managed decoder sees it.
    & $FlacExe --test --silent $flac
    if ($LASTEXITCODE -ne 0) { throw "reference --test failed for $($case.Name)" }

    $summary += [pscustomobject]@{
        Name = $case.Name; Rate = $case.Rate; Bits = $case.Bits; Ch = $case.Ch
        Frames = $case.Frames; Level = $case.Level; FlacBytes = (Get-Item $flac).Length
    }
}

# Non-subset file (oversized block size) to confirm the decoder is not subset-only.
$laxName = 'lax_16_44100_stereo'
$laxWav = Join-Path $OutputDirectory "$laxName.wav"
$laxFlac = Join-Path $OutputDirectory "$laxName.flac"
$laxPcm = Join-Path $OutputDirectory "$laxName.pcm"
$laxSamples = New-Samples -SampleRate 44100 -BitsPerSample 16 -Channels 2 -Frames 40000 -Seed 99 -Shape 'tone'
Write-Wav -Path $laxWav -SampleRate 44100 -BitsPerSample 16 -Channels 2 -Samples $laxSamples
[System.IO.File]::WriteAllBytes($laxPcm, (Get-RawPcm -BitsPerSample 16 -Samples $laxSamples))
& $FlacExe -8 --lax --blocksize=16384 --force --silent --no-padding -o $laxFlac $laxWav
if ($LASTEXITCODE -ne 0) { throw "flac encode failed for $laxName" }
& $FlacExe --test --silent $laxFlac
if ($LASTEXITCODE -ne 0) { throw "reference --test failed for $laxName" }
$summary += [pscustomobject]@{ Name = $laxName; Rate = 44100; Bits = 16; Ch = 2; Frames = 40000; Level = '8/lax/16384'; FlacBytes = (Get-Item $laxFlac).Length }

# A file with no seek table, to exercise the binary-search seek fallback.
$noSeekName = 'noseektable_16_44100_stereo'
$noSeekFlac = Join-Path $OutputDirectory "$noSeekName.flac"
$noSeekPcm = Join-Path $OutputDirectory "$noSeekName.pcm"
Copy-Item (Join-Path $OutputDirectory 'tone_16_44100_stereo.wav') (Join-Path $OutputDirectory "$noSeekName.wav") -Force
Copy-Item (Join-Path $OutputDirectory 'tone_16_44100_stereo.pcm') $noSeekPcm -Force
& $FlacExe -5 --force --silent --no-padding --no-seektable -o $noSeekFlac (Join-Path $OutputDirectory "$noSeekName.wav")
if ($LASTEXITCODE -ne 0) { throw "flac encode failed for $noSeekName" }
& $FlacExe --test --silent $noSeekFlac
if ($LASTEXITCODE -ne 0) { throw "reference --test failed for $noSeekName" }
$summary += [pscustomobject]@{ Name = $noSeekName; Rate = 44100; Bits = 16; Ch = 2; Frames = 44100; Level = '5/no-seektable'; FlacBytes = (Get-Item $noSeekFlac).Length }

# Embedded cover art, so the artwork path has real input.
$artPath = Join-Path $OutputDirectory '_cover.png'
Copy-Item (Join-Path $PSScriptRoot '..\Noct.png') $artPath -Force
$artName = 'withart_16_44100_stereo'
$artFlac = Join-Path $OutputDirectory "$artName.flac"
Copy-Item (Join-Path $OutputDirectory 'tone_16_44100_stereo.flac') $artFlac -Force
# Spec is TYPE|MIME|DESCRIPTION|RESOLUTION|FILE; resolution left empty so metaflac derives it.
& $MetaflacExe "--import-picture-from=3|image/png|Front cover||$artPath" $artFlac
if ($LASTEXITCODE -ne 0) { throw "metaflac picture import failed" }
Copy-Item (Join-Path $OutputDirectory 'tone_16_44100_stereo.pcm') (Join-Path $OutputDirectory "$artName.pcm") -Force
$summary += [pscustomobject]@{ Name = $artName; Rate = 44100; Bits = 16; Ch = 2; Frames = 44100; Level = '5+picture'; FlacBytes = (Get-Item $artFlac).Length }

# Corrupt inputs for error-path checks.
$sourceBytes = [System.IO.File]::ReadAllBytes((Join-Path $OutputDirectory 'tone_16_44100_stereo.flac'))
$cut = [int]($sourceBytes.Length * 0.4)
$truncated = New-Object byte[] $cut
[System.Array]::Copy($sourceBytes, $truncated, $cut)
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'corrupt_truncated.flac'), $truncated)

$bitflip = New-Object byte[] $sourceBytes.Length
[System.Array]::Copy($sourceBytes, $bitflip, $sourceBytes.Length)
$flipAt = [int]($sourceBytes.Length * 0.6)
$bitflip[$flipAt] = [byte]($bitflip[$flipAt] -bxor 0x5A)
$bitflip[$flipAt + 1] = [byte]($bitflip[$flipAt + 1] -bxor 0xA5)
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'corrupt_bitflip.flac'), $bitflip)

[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'not_audio.flac'), 'this is not a flac stream')

Remove-Item $tagFile -Force -ErrorAction SilentlyContinue
$summary | Format-Table -AutoSize | Out-String -Width 160 | Write-Output
Write-Output "Test material written to $OutputDirectory"
