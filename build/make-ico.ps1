# Kaynak PNG'den WPF uyumlu .ico üretir: 16–128 px BMP (DIB, 32 bit BGRA + AND maskesi), 256 px PNG.
# WPF'nin simge çözücüsü küçük boyutlarda PNG sıkıştırmalı girdileri okuyamıyor (0x88982F60).
param(
    [string]$Source = "$PSScriptRoot\..\ios\App\Assets.xcassets\AppIcon.appiconset\AppIcon.png",
    [string]$Target = "$PSScriptRoot\..\windows\src\WinDrop.App\Assets\app.ico"
)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

function Resize([System.Drawing.Image]$img, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    # iOS simgesi kare; Windows'ta yuvarlatılmış köşe için %18 yarıçaplı maske
    $r = [int]($size * 0.18)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($size-2*$r-1, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($size-2*$r-1, $size-2*$r-1, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $size-2*$r-1, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)
    $g.DrawImage($img, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

function DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER (yükseklik = 2 × boy: XOR + AND)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s*2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) {
        $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $rowBytes = [int]([math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($rowBytes * $s)))   # AND maskesi: tümü 0 (alfa kullanılır)
    $bw.Flush(); return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Resize $src $s
    if ($s -eq 256) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); ,$ms.ToArray() }
    else { ,(DibBytes $bmp) }
    $bmp.Dispose()
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = [byte[]]$images[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$d.Length); $w.Write([int]$offset); $offset += $d.Length
}
foreach ($d in $images) { $w.Write([byte[]]$d) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path (Resolve-Path (Split-Path $Target)) (Split-Path $Target -Leaf)), $out.ToArray())
"Yazıldı: $Target ($($out.Length) bayt)"
