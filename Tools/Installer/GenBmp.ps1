Add-Type -AssemblyName System.Drawing

$basePath = 'C:\Users\Admin\Documents\GitHub\BIManageRevit\Tools\Installer'
$w = 164; $h = 314
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'HighQuality'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.InterpolationMode = 'HighQualityBicubic'
$g.CompositingQuality = 'HighQuality'

# Colors - light brand theme (Construction Orange accent)
$bgTop    = [System.Drawing.Color]::FromArgb(255, 255, 255)
$bgBottom = [System.Drawing.Color]::FromArgb(240, 242, 245)
$gridCol  = [System.Drawing.Color]::FromArgb(8, 60, 120, 200)
$white    = [System.Drawing.Color]::White
$black    = [System.Drawing.Color]::Black
$orange   = [System.Drawing.Color]::FromArgb(255, 85, 0)

# ================================================
# BACKGROUND - clean white/light gradient
# ================================================
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point(0, $h)),
    $bgTop, $bgBottom)
$g.FillRectangle($bgBrush, 0, 0, $w, $h)

# No grid - clean background

# ================================================
# ZEMANAGE LOGO - rounded card with blue border
# ================================================
$zmLogo = [System.Drawing.Image]::FromFile("$basePath\BIManage.png")

# Soft orange glow behind card
for ($r = 45; $r -gt 0; $r -= 4) {
    $alpha = [int]([Math]::Max(1, 4 - ($r / 18)))
    $glowBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($alpha, 255, 85, 0))
    $g.FillEllipse($glowBr, (82 - $r), (75 - $r), ($r * 2), ($r * 2))
}

# Card
$cardW = 64; $cardH = 64
$cardX = [int](($w - $cardW) / 2)
$cardY = 42

$cardPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$cr = 16
$cardPath.AddArc($cardX, $cardY, $cr*2, $cr*2, 180, 90)
$cardPath.AddArc($cardX+$cardW-$cr*2, $cardY, $cr*2, $cr*2, 270, 90)
$cardPath.AddArc($cardX+$cardW-$cr*2, $cardY+$cardH-$cr*2, $cr*2, $cr*2, 0, 90)
$cardPath.AddArc($cardX, $cardY+$cardH-$cr*2, $cr*2, $cr*2, 90, 90)
$cardPath.CloseFigure()

# White fill for logo background
$cardFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
$g.FillPath($cardFill, $cardPath)

# Subtle grey border
$cardBorder = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 220, 220, 220), 1.5)
$g.DrawPath($cardBorder, $cardPath)

# Logo
$icoSize = 42
$g.DrawImage($zmLogo, [int]($cardX + ($cardW - $icoSize)/2), [int]($cardY + ($cardH - $icoSize)/2), $icoSize, $icoSize)
$zmLogo.Dispose()

# ================================================
# "Ze'" orange + "Manage" black (matching new logo)
# ================================================
$zeFont  = New-Object System.Drawing.Font('Segoe UI', 20, [System.Drawing.FontStyle]::Bold)
$manFont = New-Object System.Drawing.Font('Segoe UI', 20, [System.Drawing.FontStyle]::Bold)

$zeS  = $g.MeasureString("Ze'", $zeFont)
$manS = $g.MeasureString('Manage', $manFont)
$tw   = $zeS.Width + $manS.Width - 12
$sx   = ($w - $tw) / 2
$ty   = 118

# "Ze'" in Construction Orange (#FF5500)
$zeBrush = New-Object System.Drawing.SolidBrush($orange)
$g.DrawString("Ze'", $zeFont, $zeBrush, $sx, $ty)

# "Manage" in black
$manBrush = New-Object System.Drawing.SolidBrush($black)
$g.DrawString('Manage', $manFont, $manBrush, ($sx + $zeS.Width - 11), $ty)

# ================================================
# BOTTOM - ZestineTech logo (centered, wider)
# ================================================
$zLogo = [System.Drawing.Image]::FromFile("$basePath\Zestine.png")

# Z logo PNG as-is (includes built-in text)
$zLogoW = 130
$zLogoH = [int]($zLogoW * $zLogo.Height / $zLogo.Width)
$zLogoX = [int](($w - $zLogoW) / 2)
$zLogoY = [int](270 - ($zLogoH / 2))
$g.DrawImage($zLogo, $zLogoX, $zLogoY, $zLogoW, $zLogoH)
$zLogo.Dispose()

# ================================================
$g.Dispose()
$bmp.Save("$basePath\Zestine.bmp", [System.Drawing.Imaging.ImageFormat]::Bmp)
$bmp.Dispose()
Write-Output 'Done'
