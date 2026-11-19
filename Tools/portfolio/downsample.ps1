# Downsamples a supersampled capture to the final size with a high-quality bicubic filter.
#   powershell -NoProfile -ExecutionPolicy Bypass -File downsample.ps1 -In big.png -Out final.png -Width 2560 -Height 1440
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Width = 2560,
    [int]$Height = 1440
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
$src = [System.Drawing.Image]::FromFile((Resolve-Path $In))
$dst = New-Object System.Drawing.Bitmap $Width, $Height
$g = [System.Drawing.Graphics]::FromImage($dst)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$attr = New-Object System.Drawing.Imaging.ImageAttributes
$attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
$g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $Width, $Height), 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$dst.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $dst.Dispose(); $src.Dispose()
Write-Output "$Out ${Width}x${Height}"
