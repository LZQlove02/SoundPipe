# Generate SoundPipe app icon: blue rounded rect + white speaker/waves, multi-size ICO (16/32/48/256)
# Usage: powershell -File tools\make-icon.ps1   (run from repo root)
# NOTE: ASCII only - Windows PowerShell 5 reads .ps1 as ANSI/GBK and CJK comments break parsing.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = (Get-Location).Path   # repo root = cwd (keep CJK out of this file)
$outDir = Join-Path $root 'SoundPipe.App\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$icoPath = Join-Path $outDir 'app.ico'
$sizes = 16, 32, 48, 256

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    return $path
}

# ---- draw 256 master ----
$master = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($master)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$bgPath = New-RoundedRectPath 8 8 240 240 56
$bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 108, 235))
$g.FillPath($bgBrush, $bgPath)

$white = [System.Drawing.Color]::White
$speakerBrush = New-Object System.Drawing.SolidBrush $white

# speaker: box + cone
$cx = 104; $cy = 128
$g.FillRectangle($speakerBrush, $cx, ($cy - 20), 30, 40)
$cone = @(
    (New-Object System.Drawing.Point ($cx + 30), ($cy - 20)),
    (New-Object System.Drawing.Point ($cx + 62), ($cy - 46)),
    (New-Object System.Drawing.Point ($cx + 62), ($cy + 46)),
    (New-Object System.Drawing.Point ($cx + 30), ($cy + 20))
)
$g.FillPolygon($speakerBrush, $cone)

# sound waves: two arcs
$wave1 = New-Object System.Drawing.Pen ($white), 14
$wave1.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$wave1.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawArc($wave1, 150, 88, 70, 80, -55, 110)
$wave2 = New-Object System.Drawing.Pen ($white), 12
$wave2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$wave2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawArc($wave2, 178, 62, 96, 132, -55, 110)

$g.Dispose()

# ---- frames: BMP(32bit BGRA + AND mask); 256 uses PNG ----
function Get-FrameData([System.Drawing.Bitmap]$src, [int]$size, [bool]$png) {
    if ($png) {
        $ms = New-Object System.IO.MemoryStream
        $src.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$ms.ToArray()
    }
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $gg = [System.Drawing.Graphics]::FromImage($bmp)
    $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gg.DrawImage($src, 0, 0, $size, $size)
    $gg.Dispose()

    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($size * $size * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    # BMP frames are bottom-up: flip rows (BGRA already in place)
    $xor = New-Object byte[] ($size * $size * 4)
    for ($y = 0; $y -lt $size; $y++) {
        $srcOff = $y * $stride
        $dstOff = ($size - 1 - $y) * $size * 4
        [Array]::Copy($bytes, $srcOff, $xor, $dstOff, $size * 4)
    }
    # AND mask: all zero (opaque bits), alpha carried by 32bit channel; rows padded to 4 bytes
    $maskRow = [Math]::Ceiling($size / 32.0) * 4
    $and = New-Object byte[] ($maskRow * $size)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER, height = 2*size (XOR+AND)
    $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32](2 * $size))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($xor.Length + $and.Length)); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    $bw.Write($xor); $bw.Write($and)
    return ,$ms.ToArray()
}

$frames = @()
foreach ($s in $sizes) {
    $frames += ,@( $s, (Get-FrameData $master $s ($s -eq 256)) )
}
$master.Dispose()

# ---- assemble ICO ----
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write($f[1]) }
$bw.Dispose(); $fs.Dispose()

Write-Host "OK: $icoPath ($((Get-Item $icoPath).Length) bytes)"
