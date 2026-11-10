param([string]$Name, [int]$X, [int]$Y, [int]$W, [int]$H, [int]$Scale = 4)
# Crops a region of artifacts\screens\<Name>.png and enlarges it with nearest-neighbour sampling.
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $PSScriptRoot '..\artifacts\screens'
$src = [System.Drawing.Bitmap]::FromFile((Join-Path $dir "$Name.png"))
$out = New-Object System.Drawing.Bitmap(($W * $Scale), ($H * $Scale))
$g = [System.Drawing.Graphics]::FromImage($out)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, ($W * $Scale), ($H * $Scale))), $X, $Y, $W, $H, [System.Drawing.GraphicsUnit]::Pixel)
$path = Join-Path $dir "$Name-zoom.png"
$out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $out.Dispose(); $src.Dispose()
Write-Output "wrote $path"
