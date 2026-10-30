<#
.SYNOPSIS
    Draws every sprite Fernplay takes from a classic skin (.wsz) onto one labelled PNG, to check the sprite map by eye.
.EXAMPLE
    ./tools/Dump-SkinSprites.ps1 -Skin _skins/base-2.91.wsz -Out dump.png
    Build first (dotnet build); the script loads Fernplay.Core.dll from the Debug output.
#>
param(
    [Parameter(Mandatory)][string]$Skin,
    [Parameter(Mandatory)][string]$Out,
    [string]$CoreDll = (Join-Path $PSScriptRoot '..\src\Fernplay.Core\bin\Debug\net10.0-windows\Fernplay.Core.dll'),
    [int]$Zoom = 3
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Resolve-Path $CoreDll)
$s = [Fernplay.Core.Skins.ClassicSkin]::Load((Resolve-Path $Skin).Path)

function To-Bitmap([Fernplay.Core.Skins.SkinImage]$img) {
    $bmp = [System.Drawing.Bitmap]::new($img.Width, $img.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $data = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $img.Width, $img.Height), 'WriteOnly', $bmp.PixelFormat)
    $ints = [int[]]::new($img.Pixels.Length); [Buffer]::BlockCopy($img.Pixels, 0, $ints, 0, $ints.Length * 4)
    for ($y = 0; $y -lt $img.Height; $y++) { [Runtime.InteropServices.Marshal]::Copy($ints, $y * $img.Width, [IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $img.Width) }
    $bmp.UnlockBits($data); $bmp
}

# Sprites to show: the big backgrounds once, every other named sprite (volume/balance/EQ steps: first, middle, last only).
$skip = '^(Volume|Balance)\.Background\.([1-9]|1[0-35-9]|2[0-6])$|^Eq\.Slider\.([1-9]|1[0-35-9]|2[0-6])$'
$items = [Fernplay.Core.Skins.SpriteMap]::All.GetEnumerator() | Where-Object { $_.Key -notmatch $skip } | Sort-Object { $_.Value.Sheet }, { $_.Key }
$text = 'FERNPLAY 0123456789 ()-:.! AOU ÄÖÜ ÅÉ ?*'
$glyphs = $text.ToCharArray() | ForEach-Object { $s.GetSprite([Fernplay.Core.Skins.SkinFont]::Glyph($_)) }

$canvasWidth = 1400; $font = [System.Drawing.Font]::new('Segoe UI', 9); $pad = 10
# Lay out first to know the height.
$x = $pad; $y = 70; $rowH = 0; $places = @()
foreach ($it in $items) {
    $sp = $it.Value; $cellW = [Math]::Max($sp.W * $Zoom, 120); $cellH = $sp.H * $Zoom + 18
    if ($x + $cellW -gt $canvasWidth - $pad) { $x = $pad; $y += $rowH + $pad; $rowH = 0 }
    $places += [pscustomobject]@{ Name = $it.Key; Sprite = $sp; X = $x; Y = $y }
    $x += $cellW + $pad; $rowH = [Math]::Max($rowH, $cellH)
}
$canvasHeight = $y + $rowH + 100
$canvas = [System.Drawing.Bitmap]::new($canvasWidth, $canvasHeight)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'; $g.Clear([System.Drawing.Color]::FromArgb(40, 40, 48))
$white = [System.Drawing.Brushes]::White; $red = [System.Drawing.Pen]::new([System.Drawing.Color]::Red, 2)
$g.DrawString("$($s.Name)   sheets: $($s.Sheets.Keys -join ', ')   digits: $($s.DigitSheet)", $font, $white, $pad, 8)
$g.DrawString("warnings: $(if ($s.Warnings.Count) { $s.Warnings -join ' | ' } else { 'none' })", $font, $white, $pad, 26)
$regionText = ($s.Regions.Windows.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value.Count)" }) -join ', '
$info = "playlist: normal #{0:X6} current #{1:X6} bg #{2:X6} selected #{3:X6} font {4}   regions: {5}" -f ($s.Playlist.Normal -band 0xFFFFFF), ($s.Playlist.Current -band 0xFFFFFF), ($s.Playlist.NormalBackground -band 0xFFFFFF), ($s.Playlist.SelectedBackground -band 0xFFFFFF), $s.Playlist.Font, $regionText
$g.DrawString($info, $font, $white, $pad, 44)
$missing = 0
foreach ($p in $places) {
    $img = $s.GetSprite($p.Sprite)
    if ($img) { $b = To-Bitmap $img; $g.DrawImage($b, $p.X, $p.Y, $p.Sprite.W * $Zoom, $p.Sprite.H * $Zoom); $b.Dispose() }
    else { $g.DrawRectangle($red, $p.X, $p.Y, $p.Sprite.W * $Zoom, $p.Sprite.H * $Zoom); $missing++ }
    $g.DrawString($p.Name, $font, $white, $p.X, $p.Y + $p.Sprite.H * $Zoom + 1)
}
# Skin font sample and the 24 spectrum colours.
$fy = $y + $rowH + 10; $fx = $pad
foreach ($gl in $glyphs) { if ($gl) { $b = To-Bitmap $gl; $g.DrawImage($b, $fx, $fy, 5 * $Zoom, 6 * $Zoom); $b.Dispose() }; $fx += 5 * $Zoom }
for ($i = 0; $i -lt $s.VisColors.Count; $i++) {
    $v = [uint32]$s.VisColors[$i]
    $c = [System.Drawing.Color]::FromArgb(255, [int](($v -shr 16) -band 255), [int](($v -shr 8) -band 255), [int]($v -band 255))
    $br = [System.Drawing.SolidBrush]::new($c); $g.FillRectangle($br, $pad + $i * 22, $fy + 30, 20, 20); $br.Dispose()
}
$g.Dispose(); $canvas.Save((Join-Path (Resolve-Path (Split-Path $Out -Parent)) (Split-Path $Out -Leaf)), [System.Drawing.Imaging.ImageFormat]::Png); $canvas.Dispose()
"$($places.Count) sprites drawn, $missing not in this skin; warnings: $($s.Warnings.Count)"
