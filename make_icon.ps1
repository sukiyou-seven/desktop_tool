$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$png = "D:\code1\Rider\desktop_tool\title_icon.png"
$ico = "D:\code1\Rider\desktop_tool\Assets\AppIcon.ico"

$src = [System.Drawing.Image]::FromFile($png)
$sizes = 256, 128, 64, 48, 32, 16
$images = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@($s, $ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}
$src.Dispose()

$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]
    $data = $img[1]
    $wh = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$wh)   # width
    $bw.Write([byte]$wh)   # height
    $bw.Write([byte]0)     # color count
    $bw.Write([byte]0)     # reserved
    $bw.Write([uint16]1)   # planes
    $bw.Write([uint16]32)  # bit count
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) {
    $bw.Write($img[1])
}
$bw.Close()
$fs.Close()

Write-Output "OK generated: $ico ($($images.Count) sizes: $($sizes -join ','))"
