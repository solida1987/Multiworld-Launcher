# =============================================================================
#  generate_assets.ps1
#  Procedural art generator for the Launcher V2 game catalog (GDI+ only).
#
#  Generates, for each catalog entry:
#    Assets\<id>.png             256x256  rounded-card icon
#    Assets\Heroes\<id>_hero.png 1380x280 wide hero banner
#    Assets\Thumbs\<id>_thumb.png 488x256 catalog thumbnail
#
#  All artwork is abstract geometric, generated locally with System.Drawing.
#  Windows PowerShell 5.1 compatible. Re-runnable: existing PNGs are replaced.
# =============================================================================

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ------------------------------------------------------------------ paths ---
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetsDir   = Join-Path $projectRoot 'Assets'
$heroesDir   = Join-Path $assetsDir 'Heroes'
$thumbsDir   = Join-Path $assetsDir 'Thumbs'
foreach ($d in @($assetsDir, $heroesDir, $thumbsDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# ---------------------------------------------------------------- helpers ---

# Hex color + alpha -> System.Drawing.Color
function C([string]$hex, [int]$a = 255) {
    $h = $hex.TrimStart('#')
    return [System.Drawing.Color]::FromArgb($a,
        [Convert]::ToInt32($h.Substring(0, 2), 16),
        [Convert]::ToInt32($h.Substring(2, 2), 16),
        [Convert]::ToInt32($h.Substring(4, 2), 16))
}

# Multiplicative brightness shade of a hex color (f > 1 lightens, < 1 darkens)
function Shade([string]$hex, [double]$f, [int]$a = 255) {
    $h  = $hex.TrimStart('#')
    $rr = [Math]::Min(255, [Math]::Max(0, [int]([Convert]::ToInt32($h.Substring(0, 2), 16) * $f)))
    $gg = [Math]::Min(255, [Math]::Max(0, [int]([Convert]::ToInt32($h.Substring(2, 2), 16) * $f)))
    $bb = [Math]::Min(255, [Math]::Max(0, [int]([Convert]::ToInt32($h.Substring(4, 2), 16) * $f)))
    return [System.Drawing.Color]::FromArgb($a, $rr, $gg, $bb)
}

# New Graphics with high-quality settings
function New-Gfx([System.Drawing.Bitmap]$bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.TextRenderingHint  = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    return $g
}

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Axis-aligned linear gradient fills (brush rect inflated to avoid wrap seams)
function Fill-VGradient($g, [single]$x, [single]$y, [single]$w, [single]$h, $c1, $c2) {
    $rect = New-Object System.Drawing.RectangleF(($x - 1), ($y - 1), ($w + 2), ($h + 2))
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillRectangle($br, $x, $y, $w, $h)
    $br.Dispose()
}

function Fill-HGradient($g, [single]$x, [single]$y, [single]$w, [single]$h, $c1, $c2) {
    $rect = New-Object System.Drawing.RectangleF(($x - 1), ($y - 1), ($w + 2), ($h + 2))
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $g.FillRectangle($br, $x, $y, $w, $h)
    $br.Dispose()
}

# Horizontal gradient with an eased falloff (long soft tail toward c2);
# spans the full fill width so no terminal edge is visible mid-canvas.
function Fill-HGradientEase($g, [single]$x, [single]$y, [single]$w, [single]$h, $c1, $c2) {
    $rect = New-Object System.Drawing.RectangleF(($x - 1), ($y - 1), ($w + 2), ($h + 2))
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $bl = New-Object System.Drawing.Drawing2D.Blend(4)
    $bl.Positions = [single[]]@(0.0, 0.3, 0.65, 1.0)
    $bl.Factors   = [single[]]@(0.0, 0.55, 0.9, 1.0)
    $br.Blend = $bl
    $g.FillRectangle($br, $x, $y, $w, $h)
    $br.Dispose()
}

function Fill-PathVGradient($g, $path, $c1, $c2) {
    $b = $path.GetBounds()
    $rect = New-Object System.Drawing.RectangleF(($b.X - 1), ($b.Y - 1), ($b.Width + 2), ($b.Height + 2))
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($br, $path)
    $br.Dispose()
}

# Soft radial glow (color fades to fully transparent at radius)
function Draw-Glow($g, [single]$cx, [single]$cy, [single]$r, $color) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddEllipse(($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
    $pgb.CenterColor = $color
    $pgb.SurroundColors = [System.Drawing.Color[]]@([System.Drawing.Color]::FromArgb(0, $color.R, $color.G, $color.B))
    $g.FillPath($pgb, $gp)
    $pgb.Dispose()
    $gp.Dispose()
}

# Flat coordinate list (x1,y1,x2,y2,...) -> PointF[]
function PtsF($coords) {
    $n = [int]($coords.Count / 2)
    $pts = New-Object 'System.Drawing.PointF[]' $n
    for ($i = 0; $i -lt $n; $i++) {
        $pts[$i] = New-Object System.Drawing.PointF([single]$coords[2 * $i], [single]$coords[2 * $i + 1])
    }
    return ,$pts
}

# Draw an image centered at (cx,cy), square dest of given size, scaled alpha
function Draw-Faint($g, $img, [int]$cx, [int]$cy, [int]$size, [double]$alpha) {
    $cm = New-Object System.Drawing.Imaging.ColorMatrix
    $cm.Matrix33 = [single]$alpha
    $ia = New-Object System.Drawing.Imaging.ImageAttributes
    $ia.SetColorMatrix($cm)
    $dest = New-Object System.Drawing.Rectangle(($cx - [int]($size / 2)), ($cy - [int]($size / 2)), $size, $size)
    $g.DrawImage($img, $dest, 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
    $ia.Dispose()
}

# Thin curved connector (bezier bowed sideways by 'bow' design units)
function Draw-Bow($g, $pen, [double]$x1, [double]$y1, [double]$x2, [double]$y2, [double]$bow) {
    $dx = $x2 - $x1; $dy = $y2 - $y1
    $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
    $nx = -$dy / $len; $ny = $dx / $len
    $p1 = New-Object System.Drawing.PointF([single]$x1, [single]$y1)
    $p2 = New-Object System.Drawing.PointF([single]$x2, [single]$y2)
    $c1 = New-Object System.Drawing.PointF([single]($x1 + $dx / 3 + $nx * $bow), [single]($y1 + $dy / 3 + $ny * $bow))
    $c2 = New-Object System.Drawing.PointF([single]($x1 + 2 * $dx / 3 + $nx * $bow), [single]($y1 + 2 * $dy / 3 + $ny * $bow))
    $g.DrawBezier($pen, $p1, $c1, $c2, $p2)
}

function Save-Bmp($bmp, [string]$path) {
    if (Test-Path $path) { Remove-Item -Path $path -Force }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Fixed-seed film grain tile; tiled at very low alpha it dithers away the
# quantization banding that long dark gradients otherwise show.
function New-NoiseTile([int]$size, [int]$alpha, [int]$seed) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rnd = New-Object System.Random($seed)
    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            # dark-biased values: dithers banding without bright sparkle on
            # the near-black areas of the artwork
            $v = $rnd.Next(0, 128)
            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $v, $v, $v))
        }
    }
    return $bmp
}

$script:noiseTile  = New-NoiseTile 160 9 12345
$script:noiseBrush = New-Object System.Drawing.TextureBrush($script:noiseTile)

function Draw-Grain($g, [single]$w, [single]$h) {
    $g.FillRectangle($script:noiseBrush, ([single]0), ([single]0), $w, $h)
}

# ----------------------------------------------------------- motif painters -
# All painters draw centered on the origin in a design space of roughly
# +/- 62 units. Callers position them with Translate/Scale transforms.

# Gothic abstract: dark crimson flame shards rising from below + gold "II"
function Draw-MotifD2($g) {
    # back row of darker shards for depth
    $back = @( @(-42, 10, -18), @(-8, 11, -24), @(24, 10, -20), @(46, 9, -10) )
    $bbr = New-Object System.Drawing.SolidBrush (C '#4A0D10' 185)
    foreach ($s in $back) {
        $bx = [double]$s[0]; $hw = [double]$s[1]; $ty = [double]$s[2]
        $pts = PtsF @(($bx - $hw), 56, ($bx + $hw), 56, ($bx * 0.9), $ty)
        $g.FillPolygon($bbr, $pts)
    }
    $bbr.Dispose()

    # front shards: crimson gradient, brighter toward the tip
    $front = @( @(-50, 9, -6), @(-33, 11, -28), @(-16, 10, -14), @(0, 12, -44),
                @(16, 10, -18), @(33, 11, -32), @(50, 9, -4) )
    foreach ($s in $front) {
        $bx = [double]$s[0]; $hw = [double]$s[1]; $ty = [double]$s[2]
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $pts = PtsF @(($bx - $hw), 56, ($bx + $hw), 56, ($bx * 0.92), $ty)
        $path.AddPolygon($pts)
        $bounds = $path.GetBounds()
        $rect = New-Object System.Drawing.RectangleF(($bounds.X - 1), ($bounds.Y - 1), ($bounds.Width + 2), ($bounds.Height + 2))
        $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, (C '#B0301F' 240), (C '#580E0E' 235), [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($br, $path)
        $br.Dispose()
        $path.Dispose()
    }

    # gold "II" monogram with a dark backing and warm glow
    Draw-Glow $g 0 -16 36 (C '#000000' 120)
    Draw-Glow $g 0 -16 30 (C '#CCA800' 95)
    $font = New-Object System.Drawing.Font('Segoe UI', ([single]58), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sz = $g.MeasureString('II', $font)
    $tx = -$sz.Width / 2
    $ty2 = -16 - $sz.Height / 2 + 4
    $trect = New-Object System.Drawing.RectangleF([single]$tx, [single]$ty2, $sz.Width, $sz.Height)
    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush($trect, (C '#F2DC6E'), (C '#B8950A'), [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.DrawString('II', $font, $gold, [single]$tx, [single]$ty2)
    $gold.Dispose()
    $font.Dispose()

    # thin gold base accent under the monogram
    $ub = New-Object System.Drawing.SolidBrush (C '#CCA800' 215)
    $g.FillRectangle($ub, ([single]-19), ([single]12), ([single]38), ([single]2.6))
    $ub.Dispose()
}

# Transport abstract: pseudo-isometric twin rails with sleepers + station nodes
function Draw-MotifRails($g) {
    $A = @(-52.0, 38.0); $B = @(0.0, 12.0); $Cc = @(52.0, -14.0)
    $D = @(-44.0, -10.0); $E = @(36.0, 30.0)

    # dashed feeder routes under the main line
    $route = New-Object System.Drawing.Pen((C '#6FA8DC' 150), ([single]1.4))
    $route.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $g.DrawLine($route, [single]$B[0], [single]$B[1], [single]$D[0], [single]$D[1])
    $g.DrawLine($route, [single]$B[0], [single]$B[1], [single]$E[0], [single]$E[1])
    $route.Dispose()

    # main line A -> B -> C as twin rails with sleeper ticks
    $rail = New-Object System.Drawing.Pen((C '#4F8FD0' 235), ([single]2.0))
    $tie  = New-Object System.Drawing.Pen((C '#2F6FB4' 165), ([single]1.7))
    $segs = @( @($A, $B), @($B, $Cc) )
    foreach ($seg in $segs) {
        $p1x = [double]$seg[0][0]; $p1y = [double]$seg[0][1]
        $p2x = [double]$seg[1][0]; $p2y = [double]$seg[1][1]
        $dx = $p2x - $p1x; $dy = $p2y - $p1y
        $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
        $ux = $dx / $len; $uy = $dy / $len
        $nx = -$uy; $ny = $ux
        for ($t = 7.0; $t -le ($len - 7.0); $t += 11.0) {
            $px = $p1x + $ux * $t; $py = $p1y + $uy * $t
            $g.DrawLine($tie, [single]($px + $nx * 4.6), [single]($py + $ny * 4.6), [single]($px - $nx * 4.6), [single]($py - $ny * 4.6))
        }
        foreach ($o in @(2.6, -2.6)) {
            $g.DrawLine($rail, [single]($p1x + $nx * $o), [single]($p1y + $ny * $o), [single]($p2x + $nx * $o), [single]($p2y + $ny * $o))
        }
    }
    $rail.Dispose()
    $tie.Dispose()

    # station nodes with halo, fill, ring and highlight
    $nodes = @( @($A, 5.0), @($B, 6.5), @($Cc, 5.0), @($D, 4.2), @($E, 4.2) )
    $ring = New-Object System.Drawing.Pen((C '#BBD7F2' 170), ([single]1.1))
    $fill = New-Object System.Drawing.SolidBrush (C '#7FB2E8')
    $hi   = New-Object System.Drawing.SolidBrush (C '#FFFFFF' 150)
    foreach ($n in $nodes) {
        $x = [double]$n[0][0]; $y = [double]$n[0][1]; $r = [double]$n[1]
        Draw-Glow $g $x $y ($r * 2.6) (C '#4F8FD0' 90)
        $g.FillEllipse($fill, [single]($x - $r), [single]($y - $r), [single]($r * 2), [single]($r * 2))
        $g.DrawEllipse($ring, [single]($x - $r - 1.6), [single]($y - $r - 1.6), [single](($r + 1.6) * 2), [single](($r + 1.6) * 2))
        $g.FillEllipse($hi, [single]($x - $r * 0.45 - 1), [single]($y - $r * 0.45 - 1), [single]($r * 0.7), [single]($r * 0.7))
    }
    $ring.Dispose()
    $fill.Dispose()
    $hi.Dispose()
}

# Faceted gem: abstract emerald cut, light from the upper left
function Draw-MotifGem($g) {
    $ox = @(-28.0, 28.0, 48.0, 48.0, 28.0, -28.0, -48.0, -48.0)
    $oy = @(-38.0, -38.0, -18.0, 18.0, 38.0, 38.0, 18.0, -18.0)
    $k = 0.50
    $f = @(1.55, 1.15, 0.85, 0.55, 0.45, 0.65, 1.0, 1.35)

    for ($i = 0; $i -lt 8; $i++) {
        $j = ($i + 1) % 8
        $pts = PtsF @($ox[$i], $oy[$i], $ox[$j], $oy[$j], ($ox[$j] * $k), ($oy[$j] * $k), ($ox[$i] * $k), ($oy[$i] * $k))
        $br = New-Object System.Drawing.SolidBrush (Shade '#1A8E5A' ([double]$f[$i]) 245)
        $g.FillPolygon($br, $pts)
        $br.Dispose()
    }

    # center table facet with soft vertical gradient
    $tablePts = New-Object 'System.Drawing.PointF[]' 8
    for ($i = 0; $i -lt 8; $i++) {
        $tablePts[$i] = New-Object System.Drawing.PointF([single]($ox[$i] * $k), [single]($oy[$i] * $k))
    }
    $tp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tp.AddPolygon($tablePts)
    $trc = New-Object System.Drawing.RectangleF(-26, -21, 52, 42)
    $tbr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($trc, (Shade '#1A8E5A' 1.3), (Shade '#1A8E5A' 0.75), [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($tbr, $tp)
    $tbr.Dispose()
    $tp.Dispose()

    # facet edges, inner and outer outlines
    $edge = New-Object System.Drawing.Pen((C '#7FE0B0' 120), ([single]1.0))
    for ($i = 0; $i -lt 8; $i++) {
        $g.DrawLine($edge, [single]$ox[$i], [single]$oy[$i], [single]($ox[$i] * $k), [single]($oy[$i] * $k))
    }
    $g.DrawPolygon($edge, $tablePts)
    $edge.Dispose()
    $outerPts = New-Object 'System.Drawing.PointF[]' 8
    for ($i = 0; $i -lt 8; $i++) {
        $outerPts[$i] = New-Object System.Drawing.PointF([single]$ox[$i], [single]$oy[$i])
    }
    $open = New-Object System.Drawing.Pen((C '#48CF8E' 210), ([single]1.8))
    $g.DrawPolygon($open, $outerPts)
    $open.Dispose()

    # small sparkle glint near the upper-left table corner
    $sp = New-Object System.Drawing.Pen((C '#FFFFFF' 210), ([single]1.2))
    $g.DrawLine($sp, ([single]-19), ([single]-24), ([single]-5), ([single]-24))
    $g.DrawLine($sp, ([single]-12), ([single]-31), ([single]-12), ([single]-17))
    $sp.Dispose()
    $sd = New-Object System.Drawing.SolidBrush (C '#FFFFFF' 235)
    $g.FillEllipse($sd, ([single]-13.8), ([single]-25.8), ([single]3.6), ([single]3.6))
    $sd.Dispose()
}

# Abstract: single upward triangle outline pierced by a sword-like vertical
function Draw-MotifTriSword($g) {
    $tri = PtsF @(0, -46, 46, 34, -46, 34)
    $tp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tp.AddPolygon($tri)

    # faint interior fill, then soft halo stroke, then crisp stroke
    $fb = New-Object System.Drawing.SolidBrush (C '#C9A227' 18)
    $g.FillPath($fb, $tp)
    $fb.Dispose()
    $soft = New-Object System.Drawing.Pen((C '#C9A227' 60), ([single]7))
    $soft.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($soft, $tp)
    $soft.Dispose()
    $crisp = New-Object System.Drawing.Pen((C '#D9B23A'), ([single]3))
    $crisp.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($crisp, $tp)
    $crisp.Dispose()
    $tp.Dispose()

    # blade: slim tapered vertical shard through the apex
    $bp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bladePts = PtsF @(0, -62, 3, -50, 3, 14, 0, 20, -3, 14, -3, -50)
    $bp.AddPolygon($bladePts)
    $brc = New-Object System.Drawing.RectangleF(-4, -63, 8, 85)
    $bbr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($brc, (C '#F6EFD2' 245), (C '#CBB668' 225), [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($bbr, $bp)
    $bbr.Dispose()
    $bp.Dispose()
    $fl = New-Object System.Drawing.Pen((C '#9C8420' 150), ([single]0.8))
    $g.DrawLine($fl, ([single]0), ([single]-56), ([single]0), ([single]12))
    $fl.Dispose()

    # grip, crossguard (shallow V), pommel
    $gb = New-Object System.Drawing.SolidBrush (C '#A9871E')
    $g.FillRectangle($gb, ([single]-1.8), ([single]20), ([single]3.6), ([single]20))
    $gb.Dispose()
    $cg = New-Object System.Drawing.Pen((C '#C9A227'), ([single]3.4))
    $cg.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cg.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($cg, ([single]-13), ([single]21), ([single]0), ([single]17))
    $g.DrawLine($cg, ([single]0), ([single]17), ([single]13), ([single]21))
    $cg.Dispose()
    $pb = New-Object System.Drawing.SolidBrush (C '#C9A227')
    $g.FillEllipse($pb, ([single]-3.6), ([single]40.5), ([single]7.2), ([single]7.2))
    $pb.Dispose()

    # glint where the blade crosses the apex
    $gl = New-Object System.Drawing.SolidBrush (C '#FFFFFF' 180)
    $g.FillEllipse($gl, ([single]-2.2), ([single]-48.2), ([single]4.4), ([single]4.4))
    $gl.Dispose()
}

# Energy sphere: warm orb core with broken concentric ring segments
function Draw-MotifOrb($g) {
    # core with off-center radial light
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddEllipse(([single]-22), ([single]-22), ([single]44), ([single]44))
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
    $pgb.CenterColor = (C '#FFC078')
    $pgb.CenterPoint = New-Object System.Drawing.PointF(-5, -6)
    $pgb.SurroundColors = [System.Drawing.Color[]]@((C '#9A4408'))
    $g.FillPath($pgb, $gp)
    $pgb.Dispose()
    $gp.Dispose()
    $rim = New-Object System.Drawing.Pen((C '#7A3406' 235), ([single]1.6))
    $g.DrawEllipse($rim, ([single]-22), ([single]-22), ([single]44), ([single]44))
    $rim.Dispose()
    $spec = New-Object System.Drawing.SolidBrush (C '#FFFFFF' 85)
    $g.FillEllipse($spec, ([single]-14), ([single]-15), ([single]11), ([single]8))
    $spec.Dispose()

    # ring segments at three radii, alternating teal / orange
    $teal1 = New-Object System.Drawing.Pen((C '#1FA8A0' 235), ([single]3.2))
    $teal1.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $teal1.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($teal1, ([single]-34), ([single]-34), ([single]68), ([single]68), ([single]140), ([single]120))
    $g.DrawArc($teal1, ([single]-34), ([single]-34), ([single]68), ([single]68), ([single]-55), ([single]70))
    $teal1.Dispose()
    $orange = New-Object System.Drawing.Pen((C '#E07428' 215), ([single]2.4))
    $orange.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $orange.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($orange, ([single]-44), ([single]-44), ([single]88), ([single]88), ([single]40), ([single]80))
    $g.DrawArc($orange, ([single]-44), ([single]-44), ([single]88), ([single]88), ([single]185), ([single]60))
    $g.DrawArc($orange, ([single]-44), ([single]-44), ([single]88), ([single]88), ([single]290), ([single]38))
    $orange.Dispose()
    $teal2 = New-Object System.Drawing.Pen((C '#1FA8A0' 150), ([single]1.8))
    $teal2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $teal2.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($teal2, ([single]-56), ([single]-56), ([single]112), ([single]112), ([single]-20), ([single]62))
    $g.DrawArc($teal2, ([single]-56), ([single]-56), ([single]112), ([single]112), ([single]95), ([single]48))
    $g.DrawArc($teal2, ([single]-56), ([single]-56), ([single]112), ([single]112), ([single]205), ([single]68))
    $teal2.Dispose()

    # small satellite dots at selected segment ends
    $dots = @( @(34.0, 15.0, '#1FA8A0', 2.6), @(44.0, 120.0, '#E07428', 2.2), @(56.0, 273.0, '#1FA8A0', 2.0) )
    foreach ($d in $dots) {
        $rad = [double]$d[1] * [Math]::PI / 180.0
        $x = [double]$d[0] * [Math]::Cos($rad)
        $y = [double]$d[0] * [Math]::Sin($rad)
        $db = New-Object System.Drawing.SolidBrush (C $d[2])
        $rr = [double]$d[3]
        $g.FillEllipse($db, [single]($x - $rr), [single]($y - $rr), [single]($rr * 2), [single]($rr * 2))
        $db.Dispose()
    }
}

# Neutral archipelago: island dots joined by thin arcs inside a dashed ring
function Draw-MotifIslands($g) {
    $ring = New-Object System.Drawing.Pen((C '#727A99' 45), ([single]1))
    $ring.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $g.DrawEllipse($ring, ([single]-58), ([single]-58), ([single]116), ([single]116))
    $ring.Dispose()

    $isl = @( @(-40.0, 18.0, 9.0), @(-4.0, -26.0, 7.0), @(34.0, 8.0, 11.0), @(10.0, 34.0, 5.5) )

    # thin curved connections between islands
    $cp = New-Object System.Drawing.Pen((C '#727A99' 165), ([single]1.4))
    Draw-Bow $g $cp $isl[0][0] $isl[0][1] $isl[1][0] $isl[1][1] 9
    Draw-Bow $g $cp $isl[1][0] $isl[1][1] $isl[2][0] $isl[2][1] -9
    Draw-Bow $g $cp $isl[2][0] $isl[2][1] $isl[3][0] $isl[3][1] 7
    $cp.Dispose()
    $cd = New-Object System.Drawing.Pen((C '#727A99' 90), ([single]1.0))
    $cd.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dot
    Draw-Bow $g $cd $isl[0][0] $isl[0][1] $isl[3][0] $isl[3][1] -8
    $cd.Dispose()

    # island dots: soft radial fill + rim ring
    $rim = New-Object System.Drawing.Pen((C '#6A74A0' 190), ([single]1.2))
    for ($i = 0; $i -lt $isl.Count; $i++) {
        $x = [double]$isl[$i][0]; $y = [double]$isl[$i][1]; $r = [double]$isl[$i][2]
        $ip = New-Object System.Drawing.Drawing2D.GraphicsPath
        $ip.AddEllipse([single]($x - $r), [single]($y - $r), [single]($r * 2), [single]($r * 2))
        $ib = New-Object System.Drawing.Drawing2D.PathGradientBrush($ip)
        $ib.CenterColor = (C '#5A6798')
        $ib.CenterPoint = New-Object System.Drawing.PointF([single]($x - $r * 0.3), [single]($y - $r * 0.3))
        $ib.SurroundColors = [System.Drawing.Color[]]@((C '#262C48'))
        $g.FillPath($ib, $ip)
        $ib.Dispose()
        $ip.Dispose()
        $g.DrawEllipse($rim, [single]($x - $r), [single]($y - $r), [single]($r * 2), [single]($r * 2))
    }
    $rim.Dispose()

    # single gold accent ring around the largest island (launcher tie-in)
    $gr = New-Object System.Drawing.Pen((C '#CCA800' 120), ([single]1.0))
    $g.DrawEllipse($gr, ([single](34 - 15)), ([single](8 - 15)), ([single]30), ([single]30))
    $gr.Dispose()

    # faint scattered specks
    $sp = New-Object System.Drawing.SolidBrush (C '#FFFFFF' 35)
    foreach ($p in @( @(-22, -44), @(46, -30), @(-52, -8), @(20, -6), @(52, 28) )) {
        $g.FillEllipse($sp, [single]([double]$p[0] - 0.9), [single]([double]$p[1] - 0.9), ([single]1.8), ([single]1.8))
    }
    $sp.Dispose()
}

function Draw-Motif($g, [string]$id) {
    switch ($id) {
        'diablo2_archipelago' { Draw-MotifD2 $g }
        'openttd_archipelago' { Draw-MotifRails $g }
        'pokemon_emerald'     { Draw-MotifGem $g }
        'alttp'               { Draw-MotifTriSword $g }
        'super_metroid'       { Draw-MotifOrb $g }
        '_generic'            { Draw-MotifIslands $g }
    }
}

# Render a motif alone onto a transparent square bitmap (for echo layers)
function New-MotifBitmap([string]$id, [int]$px) {
    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Gfx $bmp
    $g.TranslateTransform([single]($px / 2), [single]($px / 2))
    $s = $px / 140.0
    $g.ScaleTransform([single]$s, [single]$s)
    Draw-Motif $g $id
    $g.Dispose()
    return $bmp
}

# ------------------------------------------------------------- compositors --

# 256x256 rounded-card icon
function New-Icon($game, [string]$outPath) {
    $bmp = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Gfx $bmp
    $g.Clear([System.Drawing.Color]::Transparent)

    $card = New-RoundedRectPath 0.5 0.5 255 255 28
    Fill-PathVGradient $g $card (C '#141720') (C '#0D1018')
    $g.SetClip($card)

    Fill-VGradient $g 0 0 256 90 (C '#FFFFFF' 10) (C '#FFFFFF' 0)
    Draw-Glow $g 128 134 118 (C $game.accent $game.glowA)

    $g.TranslateTransform(([single]128), ([single]130))
    $g.ScaleTransform(([single]1.27), ([single]1.27))
    Draw-Motif $g $game.id
    $g.ResetTransform()

    Fill-VGradient $g 0 192 256 64 (C '#000000' 0) (C '#000000' 55)
    Draw-Grain $g 256 256
    $g.ResetClip()

    # subtle 1px border + faint inner highlight
    $pen = New-Object System.Drawing.Pen((C '#1E2233'), ([single]1))
    $g.DrawPath($pen, $card)
    $pen.Dispose()
    $inner = New-RoundedRectPath 1.5 1.5 253 253 27
    $ip = New-Object System.Drawing.Pen((C '#FFFFFF' 9), ([single]1))
    $g.DrawPath($ip, $inner)
    $ip.Dispose()
    $inner.Dispose()
    $card.Dispose()

    $g.Dispose()
    Save-Bmp $bmp $outPath
}

# 1380x280 hero banner: accent wash left, oversized faint motif echo right
function New-Hero($game, $motifBmp, [string]$outPath) {
    $w = 1380; $h = 280
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Gfx $bmp

    Fill-VGradient $g 0 0 $w $h (C '#10131B') (C '#0D1017')
    # full-width eased accent wash (about 35 percent alpha at the left edge)
    Fill-HGradientEase $g 0 0 $w $h (C $game.accent $game.washA) (C $game.accent 0)
    # organic hot spot near the left edge (radial, so no straight seams)
    Draw-Glow $g 60 150 620 (C $game.accent ([int]($game.washA * 0.45)))

    Draw-Faint $g $motifBmp 1140 140 600 0.13

    $l1 = New-Object System.Drawing.Pen((C $game.accent2 110), ([single]1.3))
    $g.DrawLine($l1, ([single]1010), ([single]310), ([single]1190), ([single]-30))
    $l1.Dispose()
    $l2 = New-Object System.Drawing.Pen((C $game.accent 95), ([single]1.0))
    $g.DrawLine($l2, ([single]1052), ([single]310), ([single]1232), ([single]-30))
    $l2.Dispose()

    Fill-HGradient $g 0 274 380 2.4 (C $game.accent2 160) (C $game.accent2 0)
    Draw-Grain $g $w $h

    $hp = New-Object System.Drawing.Pen((C '#1E2233' 220), ([single]1))
    $g.DrawLine($hp, ([single]0), ([single]278.5), ([single]$w), ([single]278.5))
    $hp.Dispose()

    $g.Dispose()
    Save-Bmp $bmp $outPath
}

# 488x256 catalog thumbnail: mini-hero with a prominent motif
function New-Thumb($game, $motifBmp, [string]$outPath) {
    $w = 488; $h = 256
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Gfx $bmp

    Fill-VGradient $g 0 0 $w $h (C '#161B28') (C '#0D1018')
    $washA = [int][Math]::Min(150, $game.washA + 25)
    Fill-HGradientEase $g 0 0 $w $h (C $game.accent $washA) (C $game.accent 0)

    # faint oversized echo bottom-left for depth
    Draw-Faint $g $motifBmp 70 218 380 0.08

    Draw-Glow $g 312 126 150 (C $game.accent ([int][Math]::Min(255, $game.glowA + 5)))

    # diagonal accent lines sit behind the main motif
    $l1 = New-Object System.Drawing.Pen((C $game.accent2 95), ([single]1.2))
    $g.DrawLine($l1, ([single]205), ([single]300), ([single]355), ([single]-44))
    $l1.Dispose()
    $l2 = New-Object System.Drawing.Pen((C $game.accent 70), ([single]1.0))
    $g.DrawLine($l2, ([single]241), ([single]300), ([single]391), ([single]-44))
    $l2.Dispose()

    $g.TranslateTransform(([single]312), ([single]126))
    $g.ScaleTransform(([single]1.42), ([single]1.42))
    Draw-Motif $g $game.id
    $g.ResetTransform()

    Fill-VGradient $g 0 206 $w 50 (C '#000000' 0) (C '#000000' 58)
    Draw-Grain $g $w $h

    $bp = New-Object System.Drawing.Pen((C '#1E2233'), ([single]1))
    $g.DrawRectangle($bp, ([single]0.5), ([single]0.5), ([single]487), ([single]255))
    $bp.Dispose()

    $g.Dispose()
    Save-Bmp $bmp $outPath
}

# ------------------------------------------------------------------ config --

$games = @(
    @{ id = 'diablo2_archipelago'; accent = '#8E1A1A'; accent2 = '#CCA800'; glowA = 85;  washA = 92 },
    @{ id = 'openttd_archipelago'; accent = '#0A4A8E'; accent2 = '#4F8FD0'; glowA = 95;  washA = 100 },
    @{ id = 'pokemon_emerald';     accent = '#1A8E5A'; accent2 = '#56D896'; glowA = 78;  washA = 88 },
    @{ id = 'alttp';               accent = '#C9A227'; accent2 = '#EFE6C0'; glowA = 55;  washA = 72 },
    @{ id = 'super_metroid';       accent = '#C75B12'; accent2 = '#1FA8A0'; glowA = 78;  washA = 88 },
    @{ id = '_generic';            accent = '#2A3050'; accent2 = '#727A99'; glowA = 115; washA = 135 }
)

# ---------------------------------------------------------------- generate --

foreach ($game in $games) {
    Write-Host ('Generating art set: ' + $game.id)
    $motif = New-MotifBitmap $game.id 600
    New-Icon  $game (Join-Path $assetsDir ($game.id + '.png'))
    New-Hero  $game $motif (Join-Path $heroesDir ($game.id + '_hero.png'))
    New-Thumb $game $motif (Join-Path $thumbsDir ($game.id + '_thumb.png'))
    $motif.Dispose()
}

$script:noiseBrush.Dispose()
$script:noiseTile.Dispose()

# ---------------------------------------------------------------- validate --

$expected = @()
foreach ($game in $games) {
    $expected += (Join-Path $assetsDir ($game.id + '.png'))
    $expected += (Join-Path $heroesDir ($game.id + '_hero.png'))
    $expected += (Join-Path $thumbsDir ($game.id + '_thumb.png'))
}
$fail = $false
foreach ($f in $expected) {
    if (-not (Test-Path $f)) {
        Write-Host ('MISSING: ' + $f)
        $fail = $true
        continue
    }
    $len = (Get-Item $f).Length
    Write-Host ('{0,9:N0} bytes  {1}' -f $len, $f.Substring($projectRoot.Length + 1))
    if ($len -lt 1024) {
        Write-Host ('TOO SMALL: ' + $f)
        $fail = $true
    }
}
if ($fail) { throw 'Asset generation failed validation.' }
Write-Host ('All ' + $expected.Count + ' assets generated and validated.')
