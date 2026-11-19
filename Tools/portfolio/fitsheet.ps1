# Contact sheet that keeps each image's aspect (letterboxed into fixed cells), with optional per-image crops.
#   powershell -NoProfile -File fitsheet.ps1 -Out x.png -ImageList "a.png|b.png" [-LabelList "A|B"]
#       [-CropList "x,y,w,h|"] [-Columns 2] [-CellWidth 800] [-CellHeight 600] [-Title "..."]
# A crop entry is in source pixels (top-left origin); an empty entry uses the whole image.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$ImageList,
    [string]$LabelList = "",
    [string]$CropList = "",
    [int]$Columns = 2,
    [int]$CellWidth = 800,
    [int]$CellHeight = 600,
    [string]$Title = "",
    [int]$Gap = 6
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
$Images = @($ImageList -split "\|")
$Labels = @(if ($LabelList) { $LabelList -split "\|" })
$Crops = @(if ($CropList) { $CropList -split "\|" })

$rows = [math]::Ceiling($Images.Count / $Columns)
$titleHeight = if ($Title) { 48 } else { 0 }
$width = $Columns * $CellWidth + ($Columns + 1) * $Gap
$height = $titleHeight + $rows * $CellHeight + ($rows + 1) * $Gap

$sheet = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::FromArgb(255, 18, 18, 22))

$titleFont = New-Object System.Drawing.Font "Microsoft YaHei UI", 18, ([System.Drawing.FontStyle]::Bold)
$labelFont = New-Object System.Drawing.Font "Microsoft YaHei UI", ([math]::Max(11, [math]::Min($CellWidth, $CellHeight * 1.6) / 52)), ([System.Drawing.FontStyle]::Bold)
$white = [System.Drawing.Brushes]::White
$shade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 0, 0, 0))
if ($Title) { $g.DrawString($Title, $titleFont, $white, $Gap + 6, 10) }

for ($i = 0; $i -lt $Images.Count; $i++) {
    $img = [System.Drawing.Image]::FromFile((Resolve-Path $Images[$i]))
    $src = New-Object System.Drawing.Rectangle 0, 0, $img.Width, $img.Height
    if ($i -lt $Crops.Count -and $Crops[$i]) {
        $r = $Crops[$i].Split(",") | ForEach-Object { [int]$_ }
        $src = New-Object System.Drawing.Rectangle $r[0], $r[1], $r[2], $r[3]
    }
    $scale = [math]::Min($CellWidth / $src.Width, $CellHeight / $src.Height)
    $dw = [int]($src.Width * $scale); $dh = [int]($src.Height * $scale)
    $cx = $Gap + ($i % $Columns) * ($CellWidth + $Gap)
    $cy = $titleHeight + $Gap + [math]::Floor($i / $Columns) * ($CellHeight + $Gap)
    $x = $cx + [int](($CellWidth - $dw) / 2); $y = $cy + [int](($CellHeight - $dh) / 2)
    $dst = New-Object System.Drawing.Rectangle $x, $y, $dw, $dh
    $g.DrawImage($img, $dst, $src, [System.Drawing.GraphicsUnit]::Pixel)
    $img.Dispose()
    if ($i -lt $Labels.Count -and $Labels[$i]) {
        $size = $g.MeasureString($Labels[$i], $labelFont)
        $g.FillRectangle($shade, $cx, $cy, $size.Width + 12, $size.Height + 6)
        $g.DrawString($Labels[$i], $labelFont, $white, $cx + 6, $cy + 3)
    }
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $sheet.Dispose()
Write-Output "$Out ${width}x${height}"
