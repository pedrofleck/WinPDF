$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$AssetsDir = Join-Path $PSScriptRoot "..\src\WinPDF\Assets"
$IconPng = Join-Path $AssetsDir "appicon.png"

if (-not (Test-Path $IconPng)) {
    throw "appicon.png not found at: $IconPng"
}

function New-WizardLargeBanner {
    param(
        [System.Drawing.Image]$AppIcon,
        [string]$OutputPath,
        [System.Drawing.Color]$TopColor,
        [System.Drawing.Color]$BottomColor,
        [System.Drawing.Color]$TextColor,
        [System.Drawing.Color]$BorderColor
    )

    $w = 328; $h = 628
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

        $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
        $gradBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $TopColor, $BottomColor, 90.0)
        $g.FillRectangle($gradBrush, $rect)
        $gradBrush.Dispose()

        # Subtle divider line on the right edge of the side banner
        $pen = New-Object System.Drawing.Pen($BorderColor, 2)
        $g.DrawLine($pen, $w - 1, 0, $w - 1, $h)
        $pen.Dispose()

        # Centered WinPDF icon
        $iconSize = 148
        $iconX = [int](($w - $iconSize) / 2)
        $iconY = 175
        $g.DrawImage($AppIcon, $iconX, $iconY, $iconSize, $iconSize)

        # "WinPDF" title centered below the icon
        $fontTitle = New-Object System.Drawing.Font("Segoe UI", 24, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $textBrush = New-Object System.Drawing.SolidBrush($TextColor)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString("WinPDF", $fontTitle, $textBrush, [float]($w / 2), [float]344, $sf)

        $sf.Dispose()
        $textBrush.Dispose()
        $fontTitle.Dispose()

        $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

function New-WizardSmallImage {
    param(
        [System.Drawing.Image]$AppIcon,
        [string]$OutputPath,
        [System.Drawing.Color]$BgColor
    )

    $sw = 110; $sh = 110
    $bmp = New-Object System.Drawing.Bitmap($sw, $sh)
    $gs = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $gs.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gs.Clear($BgColor)
        $gs.DrawImage($AppIcon, 11, 11, 88, 88)
        $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally {
        $gs.Dispose()
        $bmp.Dispose()
    }
}

$appIcon = [System.Drawing.Image]::FromFile($IconPng)
try {
    # 1. LIGHT MODE artwork (Fluent Mica Light-style light background + Dark Graphite text)
    New-WizardLargeBanner `
        -AppIcon $appIcon `
        -OutputPath (Join-Path $AssetsDir "wizard-large-light.bmp") `
        -TopColor ([System.Drawing.Color]::FromArgb(255, 245, 247, 250)) `
        -BottomColor ([System.Drawing.Color]::FromArgb(255, 230, 234, 242)) `
        -TextColor ([System.Drawing.Color]::FromArgb(255, 24, 28, 36)) `
        -BorderColor ([System.Drawing.Color]::FromArgb(255, 212, 216, 226))

    New-WizardSmallImage `
        -AppIcon $appIcon `
        -OutputPath (Join-Path $AssetsDir "wizard-small-light.bmp") `
        -BgColor ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))

    # 2. DARK MODE artwork (Fluent Mica Dark-style dark background + White text)
    New-WizardLargeBanner `
        -AppIcon $appIcon `
        -OutputPath (Join-Path $AssetsDir "wizard-large-dark.bmp") `
        -TopColor ([System.Drawing.Color]::FromArgb(255, 28, 32, 42)) `
        -BottomColor ([System.Drawing.Color]::FromArgb(255, 16, 18, 24)) `
        -TextColor ([System.Drawing.Color]::FromArgb(255, 245, 247, 252)) `
        -BorderColor ([System.Drawing.Color]::FromArgb(255, 46, 50, 62))

    New-WizardSmallImage `
        -AppIcon $appIcon `
        -OutputPath (Join-Path $AssetsDir "wizard-small-dark.bmp") `
        -BgColor ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
}
finally {
    $appIcon.Dispose()
}

Write-Host "Light and Dark assets generated successfully in: $AssetsDir" -ForegroundColor Green