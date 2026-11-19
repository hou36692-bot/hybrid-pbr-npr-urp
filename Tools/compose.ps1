# Labelled comparison sheets from captures (System.Drawing, no extra dependencies).
#   powershell -File Tools/compose.ps1 -Out Captures/x.png -Columns 2 -CellWidth 1280 `
#       -ImageList "a.png|b.png" -LabelList "Before|After" [-Title "..."]
# Lists are "|"-separated because powershell -File passes every argument as one string.
# Images are scaled to CellWidth keeping aspect; each cell gets its label in the top-left corner.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$ImageList,
    [string]$LabelList = "",
    [int]$Columns = 2,
    [int]$CellWidth = 1280,
    [string]$Title = "",
    [int]$Gap = 6
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
$Images = $ImageList -split "\|"
$Labels = if ($LabelList) { $LabelList -split "\|" } else { @() }

$first = [System.Drawing.Image]::FromFile((Resolve-Path $Images[0]))
$cellHeight = [int]($CellWidth * $first.Height / $first.Width)
$first.Dispose()

$rows = [math]::Ceiling($Images.Count / $Columns)
$titleHeight = if ($Title) { 56 } else { 0 }
$width = $Columns * $CellWidth + ($Columns + 1) * $Gap
$height = $titleHeight + $rows * $cellHeight + ($rows + 1) * $Gap

$sheet = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::FromArgb(255, 18, 18, 22))

$titleFont = New-Object System.Drawing.Font "Microsoft YaHei UI", 20, ([System.Drawing.FontStyle]::Bold)
$labelFont = New-Object System.Drawing.Font "Microsoft YaHei UI", ([math]::Max(12, $CellWidth / 64)), ([System.Drawing.FontStyle]::Bold)
$white = [System.Drawing.Brushes]::White
$shade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 0, 0, 0))

if ($Title) { $g.DrawString($Title, $titleFont, $white, $Gap + 6, 12) }

for ($i = 0; $i -lt $Images.Count; $i++) {
    $img = [System.Drawing.Image]::FromFile((Resolve-Path $Images[$i]))
    $x = $Gap + ($i % $Columns) * ($CellWidth + $Gap)
    $y = $titleHeight + $Gap + [math]::Floor($i / $Columns) * ($cellHeight + $Gap)
    $g.DrawImage($img, $x, $y, $CellWidth, $cellHeight)
    $img.Dispose()
    if ($i -lt $Labels.Count -and $Labels[$i]) {
        $size = $g.MeasureString($Labels[$i], $labelFont)
        $g.FillRectangle($shade, $x, $y, $size.Width + 16, $size.Height + 8)
        $g.DrawString($Labels[$i], $labelFont, $white, $x + 8, $y + 4)
    }
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $sheet.Dispose()
Write-Output "$Out ${width}x${height}"
