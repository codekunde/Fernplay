param([Parameter(Mandatory)][string]$OutDir)
# Draws Fernplay's two icons at every Windows icon size and packs each into one .ico (PNG-compressed entries):
#   Fernplay.ico      app icon: charcoal tile with a bevelled rim (the skin's panel), a fern-green play triangle
#                     and, from 32 px up, three EQ bars in the skin's green -> yellow -> orange.
#   FernplayFile.ico  file icon (audio files and playlists opened with Fernplay): a white page with a folded
#                     corner, a fern-green note, and the app tile as a badge bottom-right from 32 px up.
# Also writes 256 px PNGs and a preview sheet on light and dark backgrounds.
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function C([int]$r, [int]$g, [int]$b, [int]$a = 255) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function P([float]$x, [float]$y) { New-Object System.Drawing.PointF $x, $y }

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

# The app mark in the square (x, y, s). $detail = draw the EQ bars.
function Draw-Mark($g, [float]$x, [float]$y, [float]$s, [bool]$detail) {
    $m = [Math]::Max(0.5, $s * 0.03)
    $tile = New-RoundRect ($x + $m) ($y + $m) ($s - 2 * $m) ($s - 2 * $m) ($s * 0.2)
    $rect = New-Object System.Drawing.RectangleF $x, $y, $s, $s
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C 74 79 98), (C 30 32 41), 90), $tile)
    # Rim: light top edge so the tile stays visible on a dark taskbar.
    $g.DrawPath((New-Object System.Drawing.Pen (C 138 144 163 200), ([Math]::Max(1.0, $s * 0.025))), $tile)

    # Play triangle, slightly rounded. With bars, triangle + gap + bars are centred as one group, measured on
    # what is visible (the outline pen adds $edge on each side of the triangle).
    $edge = $s * 0.025
    $h = if ($detail) { $s * 0.38 } else { $s * 0.56 }; $w = $h * 0.9
    $bw = $s * 0.065; $bgap = $s * 0.03; $tgap = $s * 0.06
    if ($detail) {
        $group = $w + 2 * $edge + $tgap + 3 * $bw + 2 * $bgap
        $left = $x + ($s - $group) / 2
        $cx = $left + $edge + $w / 2
    } else { $cx = $x + $s * 0.53 }
    $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tri.AddPolygon([System.Drawing.PointF[]]@((P ($cx - $w / 2) ($y + $s / 2 - $h / 2)), (P ($cx + $w / 2) ($y + $s / 2)), (P ($cx - $w / 2) ($y + $s / 2 + $h / 2))))
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C 140 214 120), (C 79 150 66), 90), $tri)
    $pen = New-Object System.Drawing.Pen (C 120 200 104), ($s * 0.05); $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $tri)

    if ($detail) {
        # Three EQ bars, bottom-aligned, coloured like the skin's band sliders.
        # Bottom aligned with the triangle's visible bottom.
        $bx = $cx + $w / 2 + $edge + $tgap; $base = $y + $s / 2 + $h / 2 + $edge
        $bars = @(@(0.20, (C 95 190 82)), @(0.34, (C 227 207 74)), @(0.26, (C 236 146 52)))
        foreach ($b in $bars) {
            $bh = $s * $b[0]
            $g.FillRectangle((New-Object System.Drawing.SolidBrush $b[1]), $bx, $base - $bh, $bw, $bh)
            $bx += $bw + $bgap
        }
    }
}

function Draw-AppIcon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Mark $g 0 0 $s ($s -ge 32)
    $g.Dispose(); return $bmp
}

function Draw-FileIcon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # Page with a folded top-right corner.
    $l = $s * 0.16; $t = $s * 0.04; $r = $s * 0.84; $b = $s * 0.96; $f = $s * 0.22
    $page = New-Object System.Drawing.Drawing2D.GraphicsPath
    $page.AddPolygon([System.Drawing.PointF[]]@((P $l $t), (P ($r - $f) $t), (P $r ($t + $f)), (P $r $b), (P $l $b)))
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 250 250 248)), $page)
    $g.DrawPath((New-Object System.Drawing.Pen (C 120 124 138), ([Math]::Max(1.0, $s * 0.025))), $page)
    $fold = [System.Drawing.PointF[]]@((P ($r - $f) $t), (P ($r - $f) ($t + $f)), (P $r ($t + $f)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (C 214 216 222)), $fold)
    $g.DrawPolygon((New-Object System.Drawing.Pen (C 120 124 138), ([Math]::Max(1.0, $s * 0.025))), $fold)

    # Beamed note (two heads), fern green, upper-left area of the page.
    $ink = New-Object System.Drawing.SolidBrush (C 63 128 52)
    $big = $s -lt 32
    $u = if ($big) { $s * 1.25 } else { $s }      # larger note when there is no badge
    $ox = if ($big) { $l + $s * 0.02 } else { $l + $s * 0.06 }
    $oy = if ($big) { $t + $s * 0.18 } else { $t + $s * 0.14 }
    $hw = $u * 0.17; $hh = $u * 0.12; $stem = [Math]::Max(1.0, $u * 0.045)
    $h1x = $ox; $h1y = $oy + $u * 0.38; $h2x = $ox + $u * 0.26; $h2y = $oy + $u * 0.32
    $g.FillEllipse($ink, $h1x, $h1y, $hw, $hh); $g.FillEllipse($ink, $h2x, $h2y, $hw, $hh)
    $s1 = $h1x + $hw - $stem; $s2 = $h2x + $hw - $stem
    $g.FillRectangle($ink, $s1, $oy + $u * 0.06, $stem, $h1y - $oy - $u * 0.06 + $hh / 2)
    $g.FillRectangle($ink, $s2, $oy, $stem, $h2y - $oy + $hh / 2)
    $beam = [System.Drawing.PointF[]]@((P $s1 ($oy + $u * 0.06)), (P ($s2 + $stem) $oy), (P ($s2 + $stem) ($oy + $u * 0.08)), (P $s1 ($oy + $u * 0.14)))
    $g.FillPolygon($ink, $beam)

    # App badge bottom-right.
    if (-not $big) { Draw-Mark $g ($s * 0.46) ($s * 0.52) ($s * 0.48) $false }
    $g.Dispose(); return $bmp
}

function Write-Ico([scriptblock]$draw, [string]$name) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = foreach ($s in $sizes) {
        $b = & $draw $s; $ms = New-Object IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($s -eq 256) { $b.Save((Join-Path $OutDir "$name-256.png")) }
        $b.Dispose(); , $ms.ToArray()
    }
    $out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($p in $pngs) { $w.Write($p) }
    [IO.File]::WriteAllBytes((Join-Path $OutDir "$name.ico"), $out.ToArray())
}

Write-Ico ${function:Draw-AppIcon} 'Fernplay'
Write-Ico ${function:Draw-FileIcon} 'FernplayFile'

# Preview sheet: both icons at each size on light and dark backgrounds.
$sheet = New-Object System.Drawing.Bitmap 640, 400
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.FillRectangle((New-Object System.Drawing.SolidBrush (C 243 243 243)), 0, 0, 640, 200)
$g.FillRectangle((New-Object System.Drawing.SolidBrush (C 32 32 32)), 0, 200, 640, 200)
foreach ($row in @(@(${function:Draw-AppIcon}, 0), @(${function:Draw-FileIcon}, 100))) {
    $x = 10
    foreach ($s in 16, 24, 32, 48, 64, 128) {
        $b = & $row[0] $s
        $yy = $row[1] + 50 - [Math]::Min($s, 90) / 2
        $d = [Math]::Min($s, 90)
        $g.DrawImage($b, $x, $yy, $d, $d); $g.DrawImage($b, $x, $yy + 200, $d, $d)
        $b.Dispose(); $x += $d + 24
    }
}
$g.Dispose(); $sheet.Save((Join-Path $OutDir 'Fernplay-preview.png')); $sheet.Dispose()
Get-ChildItem $OutDir | Select-Object Name, Length
