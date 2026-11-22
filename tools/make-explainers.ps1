<#
.SYNOPSIS
    Draws the two explanation images for the mod page (1600x900 PNG, original vector art, no game assets):
      Properties\Screenshots\0a-before-after.png   - without the mod vs with it
      Properties\Screenshots\0b-how-it-works.png   - measure, penalise, re-route
.DESCRIPTION
    Needs Windows PowerShell in STA mode:
        powershell.exe -STA -File tools\make-explainers.ps1
#>
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'LiveTrafficRouting\Properties\Screenshots'
New-Item -ItemType Directory -Force $outDir | Out-Null
$W = 1600.0; $H = 900.0

function C([byte]$a, [byte]$r, [byte]$g, [byte]$b) { [Windows.Media.Color]::FromArgb($a, $r, $g, $b) }
function B($c) { $br = [Windows.Media.SolidColorBrush]::new($c); $br.Freeze(); $br }
function P($brush, [double]$width) {
    $p = [Windows.Media.Pen]::new($brush, $width)
    $p.StartLineCap = 'Round'; $p.EndLineCap = 'Round'; $p.LineJoin = 'Round'
    $p
}
function Pt([double]$x, [double]$y) { [Windows.Point]::new($x, $y) }
function Rc([double]$x, [double]$y, [double]$w, [double]$h) { [Windows.Rect]::new($x, $y, $w, $h) }
function G([string]$data) { [Windows.Media.Geometry]::Parse($data) }
function Face([int]$weight) {
    [Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Segoe UI'), [Windows.FontStyles]::Normal,
        [Windows.FontWeight]::FromOpenTypeWeight($weight), [Windows.FontStretches]::Normal)
}
# Centred (or left-aligned) text, wrapped to $maxWidth.
function Text($dc, [string]$s, [double]$size, $brush, [double]$x, [double]$y, [int]$weight = 700, [double]$maxWidth = 0, [string]$align = 'Center') {
    $ft = [Windows.Media.FormattedText]::new($s, [Globalization.CultureInfo]::InvariantCulture, [Windows.FlowDirection]::LeftToRight,
        (Face $weight), $size, $brush, 1.0)
    if ($maxWidth -gt 0) { $ft.MaxTextWidth = $maxWidth }
    if ($align -eq 'Center' -and $maxWidth -gt 0) {
        # Wrapped text: centred inside a box of $maxWidth around $x.
        $ft.TextAlignment = [Windows.TextAlignment]::Center
        $left = $x - $maxWidth / 2
    } elseif ($align -eq 'Center') {
        $left = $x - $ft.Width / 2
    } else { $left = $x }
    $dc.DrawText($ft, (Pt $left $y))
}
function Save($dv, [string]$name) {
    $rtb = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$W, [int]$H, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $path = Join-Path $outDir $name
    $fs = [IO.File]::Create($path); $enc.Save($fs); $fs.Close()
    Write-Host "Written $path"
}

$white = B (C 255 255 255 255)
$soft = B (C 255 190 200 205)
$green = C 255 34 197 94
$red = C 255 220 50 50
$carColors = @((C 255 245 245 245), (C 255 255 196 70), (C 255 120 170 255), (C 255 255 120 90))

function Background($dc) {
    $bg = [Windows.Media.LinearGradientBrush]::new((C 255 24 34 38), (C 255 12 18 22), 90)
    $dc.DrawRectangle($bg, $null, (Rc 0 0 $W $H))
}

# A small city map: start dot, destination pin, a main road and a bypass above it.
# $x0: left edge of the panel; $mainCars / $bypassCars: how many cars on each road.
function MapPanel($dc, [double]$x0, [int]$mainCars, [int]$bypassCars, [bool]$withMod) {
    $pw = 720.0; $top = 210.0
    $dc.DrawRoundedRectangle((B (C 255 30 42 46)), $null, (Rc $x0 $top $pw 520), 28, 28)
    # blocks
    $block = B (C 255 38 52 56)
    foreach ($r in @(@(40, 30, 200, 90), @(270, 30, 180, 90), @(480, 30, 200, 90), @(40, 400, 300, 90), @(380, 400, 300, 90))) {
        $dc.DrawRoundedRectangle($block, $null, (Rc ($x0 + $r[0]) ($top + $r[1]) $r[2] $r[3]), 14, 14)
    }
    $roadY = $top + 330.0; $bypassY = $top + 180.0
    $road = P (B (C 255 70 84 90)) 64
    $bypass = P (B (C 255 62 76 82)) 40
    $dc.DrawLine($road, (Pt ($x0 + 60) $roadY), (Pt ($x0 + $pw - 60) $roadY))
    $bypassGeo = G ("M {0},{1} C {2},{1} {3},{4} {5},{4} L {6},{4} C {7},{4} {8},{1} {9},{1}" -f `
        ($x0 + 150), $roadY, ($x0 + 210), ($x0 + 220), $bypassY, ($x0 + 290), ($x0 + 430), ($x0 + 500), ($x0 + 510), ($x0 + 570))
    $dc.DrawGeometry($null, $bypass, $bypassGeo)

    # main road: jam colour depends on how full it is
    $mainColor = if ($withMod) { C 255 245 158 11 } else { $red }
    $dc.DrawLine((P (B (C 80 $mainColor.R $mainColor.G $mainColor.B)) 84), (Pt ($x0 + 200) $roadY), (Pt ($x0 + 560) $roadY))
    $i = 0
    for ($k = 0; $k -lt $mainCars; $k++) {
        $cx = $x0 + 205 + ($k % 9) * 39
        $cy = $roadY + $(if ($k -lt 9) { -16 } else { 6 })
        $dc.DrawRoundedRectangle((B $carColors[$i++ % 4]), (P (B (C 255 30 30 34)) 2), (Rc $cx $cy 30 14), 4, 4)
    }
    if ($withMod) {
        $dc.DrawGeometry($null, (P (B (C 90 $green.R $green.G $green.B)) 30), $bypassGeo)
        $dc.DrawGeometry($null, (P (B $green) 10), $bypassGeo)
    }
    # bypass cars, on top of the route
    for ($k = 0; $k -lt $bypassCars; $k++) {
        # the flat part of the side road runs from x0+290 to x0+430: keep the cars on it
        $cx = $x0 + 298 + $k * 27
        $dc.DrawRoundedRectangle((B $carColors[($k + 1) % 4]), (P (B (C 255 30 30 34)) 2), (Rc $cx ($bypassY - 7) 22 14), 4, 4)
    }
    # start and destination
    $dc.DrawEllipse($white, (P (B $green) 8), (Pt ($x0 + 60) $roadY), 18, 18)
    $px = $x0 + $pw - 60
    $pin = G ("M {0},{1} C {2},{3} {4},{5} {4},{6} A 36,36 0 1 1 {7},{6} C {7},{5} {8},{3} {0},{1} Z" -f `
        $px, $roadY, ($px - 28), ($roadY - 40), ($px - 36), ($roadY - 56), ($roadY - 76), ($px + 36), ($px + 28))
    $dc.DrawGeometry((B $green), (P $white 5), $pin)
    $dc.DrawEllipse($white, $null, (Pt $px ($roadY - 76)), 13, 13)
}

# ======================= Image 1: before / after =======================
$dv = New-Object Windows.Media.DrawingVisual
$dc = $dv.RenderOpen()
Background $dc
Text $dc 'WHAT CHANGES' 64 $white 800 40 900
Text $dc 'Same city, same hour: without the mod and with it' 30 $soft 800 125 400

Text $dc 'WITHOUT THE MOD' 38 (B (C 255 248 113 113)) 420 160 900
MapPanel $dc 60 18 0 $false
Text $dc 'Everyone keeps the road they always take. The side road stays empty while the main road jams.' 26 $soft 420 750 400 640

Text $dc 'WITH LIVE TRAFFIC ROUTING' 38 (B (C 255 74 222 128)) 1180 160 900
MapPanel $dc 820 8 5 $true
Text $dc 'Drivers see the jam like a navigation app would and spread out: shorter queues, everyone gets there sooner.' 26 $soft 1180 750 400 640
$dc.Close()
Save $dv '0a-before-after.png'

# ======================= Image 2: how it works =======================
$dv = New-Object Windows.Media.DrawingVisual
$dc = $dv.RenderOpen()
Background $dc
Text $dc 'HOW IT WORKS' 64 $white 800 40 900

$cards = @(
    @{ x = 60;   title = '1  MEASURE';  color = (C 255 96 165 250);
       body = 'Every few game minutes the mod measures how fast cars really drive on every lane, right now.' },
    @{ x = 570;  title = '2  PENALISE'; color = (C 255 248 113 113);
       body = 'Jammed lanes look slower to the game''s own route planner, up to many times their normal travel time.' },
    @{ x = 1080; title = '3  RE-ROUTE'; color = (C 255 74 222 128);
       body = 'New trips pick another way. Drivers already on the road switch when the way ahead jams up.' }
)
foreach ($c in $cards) {
    $x = [double]$c.x
    $dc.DrawRoundedRectangle((B (C 255 30 42 46)), $null, (Rc $x 170 460 560), 28, 28)
    $dc.DrawRoundedRectangle((B $c.color), $null, (Rc $x 170 460 12), 6, 6)
    Text $dc $c.title 40 (B $c.color) ($x + 230) 210 900
    Text $dc $c.body 27 $soft ($x + 230) 540 400 400
}

# icon 1: speedometer
$cx = 290; $cy = 420
$dc.DrawGeometry($null, (P (B (C 255 70 84 90)) 22), (G "M 190,440 A 100,100 0 0 1 390,440"))
$dc.DrawGeometry($null, (P (B (C 255 96 165 250)) 22), (G "M 190,440 A 100,100 0 0 1 262,345"))
$dc.DrawLine((P $white 10), (Pt $cx 440), (Pt 240 370))
$dc.DrawEllipse($white, $null, (Pt $cx 440), 16, 16)
Text $dc 'actual speed vs limit' 22 $soft $cx 470 400

# icon 2: a lane turning red, "x10 slower"
$dc.DrawLine((P (B (C 255 70 84 90)) 56), (Pt 630 420), (Pt 970 420))
$dc.DrawLine((P (B (C 120 220 50 50)) 70), (Pt 720 420), (Pt 900 420))
for ($k = 0; $k -lt 5; $k++) {
    $dc.DrawRoundedRectangle((B $carColors[$k % 4]), (P (B (C 255 30 30 34)) 2), (Rc (725 + $k * 35) 412 28 16), 4, 4)
}
$dc.DrawRoundedRectangle((B (C 255 220 50 50)), $null, (Rc 710 310 200 56), 28, 28)
Text $dc 'x10 slower' 28 $white 810 318 800

# icon 3: route splitting around a blocked road
$dc.DrawLine((P (B (C 255 70 84 90)) 40), (Pt 1130 440), (Pt 1490 440))
$dc.DrawLine((P (B (C 140 220 50 50)) 40), (Pt 1240 440), (Pt 1390 440))
$detour = G "M 1130,440 L 1200,440 C 1240,440 1240,340 1290,340 L 1340,340 C 1390,340 1390,440 1430,440 L 1490,440"
$dc.DrawGeometry($null, (P (B (C 90 34 197 94)) 34), $detour)
$dc.DrawGeometry($null, (P (B $green) 12), $detour)
$dc.DrawGeometry($white, $null, (G "M 1305,328 L 1327,340 L 1305,352 Z"))
$dc.DrawEllipse($white, (P (B $green) 7), (Pt 1130 440), 14, 14)
$dc.DrawEllipse((B $green), (P $white 5), (Pt 1490 440), 16, 16)

Text $dc 'Nothing is saved in your city  -  about 1% of one CPU core  -  turn it off any time' 28 (B (C 255 140 150 158)) 800 790 600
$dc.Close()
Save $dv '0b-how-it-works.png'
