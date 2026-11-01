# Regenerates src/PriceRunnerPanel/Assets/PriceRunnerPanel.ico (multi-size, PNG-compressed entries).
# The design: a pale "lab glass" rounded square, a cyan orbit ring and a downward price arrow.
# Windows only (System.Drawing). Run from the repository root:  pwsh scripts/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 64.0

    $radius = 14 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [System.Drawing.RectangleF]::new(2 * $s, 2 * $s, 60 * $s, 60 * $s)
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90); $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90); $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90); $path.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0xD6, 0xE6, 0xF5)), 90.0
    $g.FillPath($fill, $path)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 0x00, 0x8C, 0xB4)), ([Math]::Max(1, 1.5 * $s))), $path)

    $cyan = [System.Drawing.Color]::FromArgb(255, 0x00, 0x9E, 0xC7)
    $ring = New-Object System.Drawing.Pen $cyan, ([Math]::Max(1.2, 3.2 * $s))
    $g.DrawEllipse($ring, 13 * $s, 13 * $s, 38 * $s, 38 * $s)

    # Downward arrow: a price drop.
    $green = [System.Drawing.Color]::FromArgb(255, 0x0E, 0xA8, 0x6C)
    $shaft = New-Object System.Drawing.SolidBrush $green
    $g.FillRectangle($shaft, 28.5 * $s, 20 * $s, 7 * $s, 14 * $s)
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(21 * $s, 32 * $s), [System.Drawing.PointF]::new(43 * $s, 32 * $s),
        [System.Drawing.PointF]::new(32 * $s, 44 * $s))
    $g.FillPolygon($shaft, $points)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $cyan), 44 * $s, 12 * $s, 8 * $s, 8 * $s)

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size }))); $w.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
$target = Join-Path (Get-Location) 'src/PriceRunnerPanel/Assets/PriceRunnerPanel.ico'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($out.Length) bytes, sizes $($sizes -join ', '))"
