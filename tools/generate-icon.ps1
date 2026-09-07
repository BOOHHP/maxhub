# Generates src/MaxHub.Agent.Tray/Assets/maxhub.ico (isometric cube + hub node, sizes 16/24/32/48/256).
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

function Fill-Polygon([System.Drawing.Graphics]$gr, [float[]]$xs, [float[]]$ys, [System.Drawing.Color]$col) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = New-Object 'System.Drawing.PointF[]' $ys.Count
    for ($i = 0; $i -lt $ys.Count; $i++) { $pts[$i] = New-Object System.Drawing.PointF ($xs[$i]), ($ys[$i]) }
    $b = New-Object System.Drawing.SolidBrush $col
    $gr.FillPath($b, $path)
    $path.Dispose(); $b.Dispose()
}

$sizes = @(16, 24, 32, 48, 256)
$frames = @()
$tempPng = Join-Path $env:TEMP "maxhub-icon-frame.png"
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $f = $size / 32.0

    # Palette aligned with Agent 1.1.0 UI tokens (layered gray-blue + light-blue accent)
    $bgTop     = [System.Drawing.Color]::FromArgb(0x2A, 0x30, 0x3A)
    $bgBottom  = [System.Drawing.Color]::FromArgb(0x17, 0x1B, 0x22)
    $faceTop   = [System.Drawing.Color]::FromArgb(0x9C, 0xC4, 0xFA)
    $faceLeft  = [System.Drawing.Color]::FromArgb(0x58, 0x90, 0xE8)
    $faceRight = [System.Drawing.Color]::FromArgb(0x34, 0x64, 0xB4)
    $node      = [System.Drawing.Color]::FromArgb(0x8F, 0xBB, 0xF7)
    $edge      = [System.Drawing.Color]::FromArgb(0x4A, 0x52, 0x5C)

    # Rounded plate with vertical gradient + subtle inner edge
    $plate = New-RoundedPath (1 * $f) (1 * $f) (30 * $f) (30 * $f) (7 * $f)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, $size)),
        $bgTop, $bgBottom)
    $g.FillPath($brush, $plate)
    $edgePen = New-Object System.Drawing.Pen($edge, [Math]::Max(1.0, 1 * $f))
    $g.DrawPath($edgePen, $plate)

    # Isometric cube (3ds Max "3D" cue) — vertices on the 32-grid, scalars only
    # (kept identical to the verified preview code; PowerShell 5.1 array-literal math is fragile)
    $ax = 15 * $f;  $ay = 11.8 * $f
    $bx = 24 * $f;  $by = 17 * $f
    $qx = 15 * $f;  $qy = 22.2 * $f
    $gdx = 6 * $f;  $gdy = 17 * $f
    $ey = 30.2 * $f
    $fyy = 25 * $f
    $topX = [float[]]@($ax, $bx, $qx, $gdx)
    $topY = [float[]]@($ay, $by, $qy, $gdy)
    $leftX = [float[]]@($gdx, $qx, $qx, $gdx)
    $leftY = [float[]]@($gdy, $qy, $ey, $fyy)
    $rightX = [float[]]@($bx, $qx, $qx, $bx)
    $rightY = [float[]]@($by, $qy, $ey, $fyy)
    Fill-Polygon $g $topX $topY $faceTop
    Fill-Polygon $g $leftX $leftY $faceLeft
    Fill-Polygon $g $rightX $rightY $faceRight

    # Hub satellite node + link (skipped at 16px for legibility)
    if ($size -gt 16) {
        $pen = New-Object System.Drawing.Pen($node, [Math]::Max(1.5, 2 * $f))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($pen, (26 * $f), (6 * $f), (20.5 * $f), (11 * $f))
        $dot = New-RoundedPath (23.5 * $f) (3.5 * $f) (5 * $f) (5 * $f) (1.5 * $f)
        $nb = New-Object System.Drawing.SolidBrush $node
        $g.FillPath($nb, $dot)
        $dot.Dispose(); $nb.Dispose(); $pen.Dispose()
    }

    $brush.Dispose(); $edgePen.Dispose(); $plate.Dispose(); $g.Dispose()
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
