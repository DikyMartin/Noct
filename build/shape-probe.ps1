<#
Shows a borderless solid-colour window at the top centre of the primary screen and, optionally,
saves a crop of just that area. The island is topmost, so it sits on top of this colour; the crop
therefore contains only the island and the chosen backdrop colour, never other desktop content.

  -Color     hex RRGGBB of the backdrop (use FFFFFF and 202020 to test bright and dark wallpapers)
  -Seconds   how long the backdrop stays up
  -Crop      path of a PNG to save while it is up
  -CropAfter seconds to wait before the crop is taken
#>
param(
    [string]$Color = 'FFFFFF',
    [int]$Seconds = 12,
    [string]$Crop = '',
    [double]$CropAfter = 5,
    [int]$Width = 760,
    [int]$Height = 220
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class DpiFix { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[void][DpiFix]::SetProcessDPIAware()

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$x = [int](($screen.Width - $Width) / 2)

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.ShowInTaskbar = $false
$form.Location = New-Object System.Drawing.Point($x, 0)
$form.Size = New-Object System.Drawing.Size($Width, $Height)
$form.BackColor = [System.Drawing.ColorTranslator]::FromHtml("#$Color")

$deadline = (Get-Date).AddSeconds($Seconds)
$cropAt = (Get-Date).AddSeconds($CropAfter)
$cropped = $false

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 100
$timer.Add_Tick({
    if ($Crop -and -not $script:cropped -and (Get-Date) -ge $cropAt) {
        $script:cropped = $true
        $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($x, 0, 0, 0, (New-Object System.Drawing.Size($Width, $Height)))
        $bmp.Save($Crop, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    }
    if ((Get-Date) -ge $deadline) { $form.Close() }
})
$timer.Start()
[System.Windows.Forms.Application]::Run($form)
