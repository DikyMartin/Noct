# Prints the vertical extent and centre of bright pixels inside named x-ranges of a window shot.
# Usage: measure-row.ps1 <shotName> <y0> <y1> "name:x0-x1" ...
param([string]$Name, [int]$Y0, [int]$Y1, [string[]]$Ranges)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot "..\artifacts\screens\$Name.png"))
foreach ($r in $Ranges) {
    $label, $span = $r.Split(':'); $x0, $x1 = $span.Split('-') | ForEach-Object { [int]$_ }
    $min = 99999; $max = -1
    for ($y = $Y0; $y -lt $Y1; $y++) {
        for ($x = $x0; $x -le $x1; $x++) {
            $c = $bmp.GetPixel($x, $y)
            if (($c.R + $c.G + $c.B) / 3 -gt 95) { if ($y -lt $min) { $min = $y }; if ($y -gt $max) { $max = $y } }
        }
    }
    if ($max -lt 0) { "{0,-10} none" -f $label } else { "{0,-10} rows {1}-{2}  centre {3}" -f $label, $min, $max, (($min + $max) / 2) }
}
$bmp.Dispose()
