$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# ABSOLUTE path (.NET classes do not use PowerShell's current directory)
$assetsDir = Join-Path (Get-Location).Path "src\WinPDF\Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
$icoPath = Join-Path $assetsDir "appicon.ico"

function New-AppIconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size

    # 1. Background squircle
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = [int]($size * 0.22)
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $colorTop = [System.Drawing.Color]::FromArgb(255, 239, 68, 68)
    $colorBottom = [System.Drawing.Color]::FromArgb(255, 197, 34, 34)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect, $colorTop, $colorBottom,
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $path)

    # Translucent border
    $penWidth = [single][Math]::Max(1.0, $size / 64.0)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(40, 255, 255, 255), $penWidth)
    $g.DrawPath($pen, $path)

    # 2. "PDF" text (font size in PIXELS, otherwise it overflows the icon)
    $fontSize = [single]($size * 0.34)
    $font = [System.Drawing.Font]::new(
        "Segoe UI", $fontSize,
        [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)

    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center

    # Shadow
    $shadowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(80, 0, 0, 0))
    $shadowRect = New-Object System.Drawing.RectangleF ([single](2 * $size / 128)), ([single](3 * $size / 128)), ([single]$size), ([single]$size)
    $g.DrawString("PDF", $font, $shadowBrush, $shadowRect, $sf)

    # White text
    $whiteBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $rectF = New-Object System.Drawing.RectangleF 0, 0, ([single]$size), ([single]$size)
    $g.DrawString("PDF", $font, $whiteBrush, $rectF, $sf)

    $whiteBrush.Dispose(); $shadowBrush.Dispose(); $font.Dispose(); $sf.Dispose()
    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()

    return , $bmp
}

# Generates each size and converts it to PNG (bytes)
$sizes = @(16, 32, 48, 64, 128, 256)
$pngList = New-Object System.Collections.Generic.List[byte[]]

foreach ($s in $sizes) {
    $bmp = New-AppIconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngList.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

# Also saves a PNG version for the WinUI 3 UI (TitleBar, etc.)
$pngPath = Join-Path $assetsDir "appicon.png"
[System.IO.File]::WriteAllBytes($pngPath, $pngList[-1])

# Assembles the .ico file
$fileStream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter $fileStream

try {
    # ICONDIR header
    $writer.Write([UInt16]0)               # Reserved
    $writer.Write([UInt16]1)               # Type 1 = icon
    $writer.Write([UInt16]$sizes.Count)    # Image count

    $offset = 6 + (16 * $sizes.Count)

    # ICONDIRENTRY entries
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }   # 0 = 256px (must be Byte!)

        $writer.Write([byte]$dim)                       # Width
        $writer.Write([byte]$dim)                       # Height
        $writer.Write([byte]0)                          # Palette
        $writer.Write([byte]0)                          # Reserved
        $writer.Write([UInt16]1)                        # Color planes
        $writer.Write([UInt16]32)                       # Bits per pixel
        $writer.Write([UInt32]$pngList[$i].Length)      # Data size
        $writer.Write([UInt32]$offset)                  # Offset

        $offset += $pngList[$i].Length
    }

    # Image data
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $writer.Write($pngList[$i])
    }
}
finally {
    $writer.Close()
    $fileStream.Close()
}

$info = Get-Item $icoPath
Write-Host "Icon generated successfully: $($info.FullName) ($($info.Length) bytes)" -ForegroundColor Green

# Automatically generates installer visual assets
& "$PSScriptRoot\Generate-InstallerAssets.ps1"