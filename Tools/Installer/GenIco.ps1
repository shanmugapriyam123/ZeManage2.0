Add-Type -AssemblyName System.Drawing

$basePath = 'C:\Users\Admin\Documents\GitHub\BIManageRevit\Tools\Installer'
$srcPng   = Join-Path $basePath 'BIManage.png'
$outIco   = Join-Path $basePath 'BIManage.ico'

$sizes = @(16, 24, 32, 48, 64, 128, 256)

$src = [System.Drawing.Image]::FromFile($srcPng)

$pngStreams = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode        = 'HighQuality'
    $g.InterpolationMode    = 'HighQualityBicubic'
    $g.CompositingQuality   = 'HighQuality'
    $g.PixelOffsetMode      = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngStreams += ,@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
}
$src.Dispose()

$icoStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($icoStream)

# ICONDIR
$bw.Write([UInt16]0)                       # reserved
$bw.Write([UInt16]1)                       # type = icon
$bw.Write([UInt16]$pngStreams.Count)       # image count

$headerSize = 6
$entrySize  = 16
$dataOffset = $headerSize + ($entrySize * $pngStreams.Count)

# ICONDIRENTRY[]
foreach ($p in $pngStreams) {
    $sz = if ($p.Size -ge 256) { 0 } else { [byte]$p.Size }
    $bw.Write([byte]$sz)                   # width  (0 = 256)
    $bw.Write([byte]$sz)                   # height (0 = 256)
    $bw.Write([byte]0)                     # color count
    $bw.Write([byte]0)                     # reserved
    $bw.Write([UInt16]1)                   # planes
    $bw.Write([UInt16]32)                  # bpp
    $bw.Write([UInt32]$p.Bytes.Length)     # bytes in resource
    $bw.Write([UInt32]$dataOffset)         # offset
    $dataOffset += $p.Bytes.Length
}

# image data
foreach ($p in $pngStreams) { $bw.Write($p.Bytes) }

$bw.Flush()
[System.IO.File]::WriteAllBytes($outIco, $icoStream.ToArray())
$bw.Dispose()
$icoStream.Dispose()

Write-Output ("Wrote {0} ({1} bytes, {2} sizes)" -f $outIco, (Get-Item $outIco).Length, $pngStreams.Count)
