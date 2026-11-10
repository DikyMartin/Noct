<#
Builds src/Noct.App/Assets/Noct.ico and a trimmed Noct.png from the source logo.

The source image has a transparent margin around the rounded-square mark, so it is cropped to the
mark's bounding box before scaling. Each icon entry is stored as a PNG, which keeps the alpha edge
clean at every size and is understood by Windows Vista and later.
#>
param(
    [string]$Source = "$PSScriptRoot\..\Noct.png",
    [string]$AssetDirectory = "$PSScriptRoot\..\src\Noct.App\Assets"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path $AssetDirectory | Out-Null
$AssetDirectory = (Resolve-Path $AssetDirectory).Path
$Source = (Resolve-Path $Source).Path

$original = New-Object System.Drawing.Bitmap($Source)
try {
    # Crop to the opaque mark, keeping it square and centred.
    $cropX = 87; $cropY = 88; $cropSize = 1080
    $crop = New-Object System.Drawing.Rectangle($cropX, $cropY, $cropSize, $cropSize)
    $square = New-Object System.Drawing.Bitmap($cropSize, $cropSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($square)
    try {
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($original, (New-Object System.Drawing.Rectangle(0, 0, $cropSize, $cropSize)), $crop, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally { $g.Dispose() }

    $trimmedPng = Join-Path $AssetDirectory 'Noct.png'
    $square.Save($trimmedPng, [System.Drawing.Imaging.ImageFormat]::Png)

    $sizes = @(256, 128, 64, 48, 40, 32, 24, 20, 16)
    $pngBytes = @{}
    foreach ($size in $sizes) {
        $scaled = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gs = [System.Drawing.Graphics]::FromImage($scaled)
        try {
            $gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gs.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $gs.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $gs.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $gs.Clear([System.Drawing.Color]::Transparent)
            $gs.DrawImage($square, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
        }
        finally { $gs.Dispose() }

        $ms = New-Object System.IO.MemoryStream
        $scaled.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBytes[$size] = $ms.ToArray()
        $ms.Dispose()
        $scaled.Dispose()
    }

    # A 256 px copy of the same mark for in-app use (nav logo, island, empty states). The full-size
    # trimmed Noct.png stays on disk as a source asset but is not embedded in the executable.
    $markPath = Join-Path $AssetDirectory 'NoctMark.png'
    [System.IO.File]::WriteAllBytes($markPath, $pngBytes[256])

    $icoPath = Join-Path $AssetDirectory 'Noct.ico'
    $fs = [System.IO.File]::Create($icoPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([int16]0)              # reserved
        $bw.Write([int16]1)              # type: icon
        $bw.Write([int16]$sizes.Count)

        $offset = 6 + (16 * $sizes.Count)
        foreach ($size in $sizes) {
            $bytes = $pngBytes[$size]
            $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))  # width, 0 means 256
            $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))  # height
            $bw.Write([byte]0)           # palette count
            $bw.Write([byte]0)           # reserved
            $bw.Write([int16]1)          # colour planes
            $bw.Write([int16]32)         # bits per pixel
            $bw.Write([int]$bytes.Length)
            $bw.Write([int]$offset)
            $offset += $bytes.Length
        }

        foreach ($size in $sizes) {
            $bytes = $pngBytes[$size]
            $bw.Write($bytes, 0, $bytes.Length)
        }
    }
    finally { $bw.Dispose(); $fs.Dispose() }

    $square.Dispose()
    Write-Output "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes, $($sizes.Count) sizes)"
    Write-Output "Wrote $trimmedPng ($((Get-Item $trimmedPng).Length) bytes, ${cropSize}x${cropSize})"
}
finally { $original.Dispose() }
