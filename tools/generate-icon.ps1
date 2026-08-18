# Generates src/MaxHub.Agent.Tray/Assets/maxhub.ico (hub-relay mark, sizes 16/24/32/48/256).
# ASCII-only script for Windows PowerShell 5.1 compatibility.
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "..\src\MaxHub.Agent.Tray\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outPath = Join-Path $outDir "maxhub.ico"

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = @(16, 24, 32, 48, 256)
$frames = @()
$tempPng = Join-Path $env:TEMP "maxhub-icon-frame.png"
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $f = $size / 32.0

    $bg     = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x1E, 0x20, 0x23))
    $link   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x4C, 0x9F, 0xE0))
    $center = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xE8, 0xEA, 0xED))

    $plate = New-RoundedPath (1 * $f) (1 * $f) (30 * $f) (30 * $f) (7 * $f)
    $g.FillPath($bg, $plate)

    $penWidth = [Math]::Max(1.5, 2.5 * $f)
    $pen = New-Object System.Drawing.Pen($link, $penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, 16 * $f, 16 * $f, 8 * $f, 8 * $f)
    $g.DrawLine($pen, 16 * $f, 16 * $f, 8 * $f, 24 * $f)
    $g.DrawLine($pen, 16 * $f, 16 * $f, 24 * $f, 16 * $f)

    if ($size -gt 16) {
        foreach ($pt in @(@(5, 5), @(5, 21), @(21, 13))) {
            $sq = New-RoundedPath ($pt[0] * $f) ($pt[1] * $f) (6 * $f) (6 * $f) (1.5 * $f)
            $g.FillPath($link, $sq)
            $sq.Dispose()
        }
    }
    $c = New-RoundedPath (12 * $f) (12 * $f) (8 * $f) (8 * $f) (1.5 * $f)
    $g.FillPath($center, $c)

    $c.Dispose(); $pen.Dispose(); $plate.Dispose()
    $bg.Dispose(); $link.Dispose(); $center.Dispose(); $g.Dispose()

    $bmp.Save($tempPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += ,([System.IO.File]::ReadAllBytes($tempPng))
}
Remove-Item $tempPng -ErrorAction SilentlyContinue

$stream = [System.IO.File]::Create($outPath)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)                # reserved
$writer.Write([UInt16]1)                # type: icon
$writer.Write([UInt16]$sizes.Count)     # image count
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([Byte]$dim)           # width
    $writer.Write([Byte]$dim)           # height
    $writer.Write([Byte]0)              # palette
    $writer.Write([Byte]0)              # reserved
    $writer.Write([UInt16]1)            # planes
    $writer.Write([UInt16]32)           # bpp
    $writer.Write([UInt32]$frames[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Close()

Write-Host "Icon written: $outPath ($((Get-Item $outPath).Length) bytes)"
