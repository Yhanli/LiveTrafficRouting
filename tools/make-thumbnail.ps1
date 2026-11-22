<#
.SYNOPSIS
    Draws the Paradox Mods thumbnail (1024x1024): a jammed highway in red and a green route around it.
.DESCRIPTION
    Pure vector drawing with WPF (no game assets). Needs Windows PowerShell in STA mode:
        powershell.exe -STA -File tools\make-thumbnail.ps1
    Writes LiveTrafficRouting\Properties\Thumbnail.png unless -Out is given.
#>
param([string]$Out)
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

if (-not $Out) { $Out = Join-Path (Split-Path -Parent $PSScriptRoot) 'LiveTrafficRouting\Properties\Thumbnail.png' }
$W = 1024.0

function C([byte]$a, [byte]$r, [byte]$g, [byte]$b) { [Windows.Media.Color]::FromArgb($a, $r, $g, $b) }
function B($c) { $br = [Windows.Media.SolidColorBrush]::new($c); $br.Freeze(); $br }
function P($brush, [double]$width, [string]$cap = 'Round') {
    $p = [Windows.Media.Pen]::new($brush, $width)
    $p.StartLineCap = $cap; $p.EndLineCap = $cap; $p.LineJoin = 'Round'
    $p
}
function G([string]$data) { [Windows.Media.Geometry]::Parse($data) }
function Text($dc, [string]$s, [double]$size, $brush, [double]$x, [double]$y, [string]$weight = 'Bold', [double]$spacing = 0) {
    $tf = [Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Segoe UI'), [Windows.FontStyles]::Normal, [Windows.FontWeight]::FromOpenTypeWeight($(if ($weight -eq 'Black') { 900 } elseif ($weight -eq 'Bold') { 700 } else { 400 })), [Windows.FontStretches]::Normal)
    $ft = [Windows.Media.FormattedText]::new($s, [Globalization.CultureInfo]::InvariantCulture, [Windows.FlowDirection]::LeftToRight, $tf, $size, $brush, 1.0)
    $dc.DrawText($ft, ([Windows.Point]::new($x - $ft.Width / 2, $y)))
}

$dv = New-Object Windows.Media.DrawingVisual
$dc = $dv.RenderOpen()
$full = [Windows.Rect]::new(0, 0, $W, $W)

# ---- 1. Map background: dark ground with blocks ----
$bg = [Windows.Media.LinearGradientBrush]::new((C 255 24 34 38), (C 255 14 20 24), 90)
$dc.DrawRectangle($bg, $null, $full)
$block = B (C 255 33 46 50)
$park = B (C 255 30 58 44)
foreach ($r in @(
        @(40, 40, 250, 150), @(320, 40, 300, 150), @(650, 40, 330, 150),
        @(40, 600, 230, 160), @(300, 600, 200, 160), @(530, 600, 210, 160), @(770, 600, 214, 160))) {
    $dc.DrawRoundedRectangle($block, $null, ([Windows.Rect]::new($r[0], $r[1], $r[2], $r[3])), 18, 18)
}
$dc.DrawRoundedRectangle($park, $null, ([Windows.Rect]::new(330, 250, 360, 150)), 40, 40)

# ---- 2. Side streets ----
$street = P (B (C 255 58 72 78)) 22
foreach ($l in @(@(0, 220, 1024, 220), @(300, 0, 300, 780), @(720, 0, 720, 780), @(0, 580, 1024, 580))) {
    $dc.DrawLine($street, ([Windows.Point]::new($l[0], $l[1])), ([Windows.Point]::new($l[2], $l[3])))
}

# ---- 3. The highway ----
$hwY = 470.0
$dc.DrawLine((P (B (C 255 70 84 90)) 96 'Flat'), ([Windows.Point]::new(0, $hwY)), ([Windows.Point]::new($W, $hwY)))
$dash = P (B (C 160 230 235 240)) 5 'Flat'
$dash.DashStyle = [Windows.Media.DashStyle]::new([double[]]@(4.0, 4.0), 0.0)
$dc.DrawLine($dash, ([Windows.Point]::new(0, $hwY)), ([Windows.Point]::new($W, $hwY)))

# ---- 4. The jam: red glow and queued cars ----
$jamFrom = 330.0; $jamTo = 800.0
$dc.DrawLine((P (B (C 90 239 68 68)) 120), ([Windows.Point]::new($jamFrom, $hwY)), ([Windows.Point]::new($jamTo, $hwY)))
$dc.DrawLine((P (B (C 255 220 50 50)) 18), ([Windows.Point]::new($jamFrom, $hwY - 30)), ([Windows.Point]::new($jamTo, $hwY - 30)))
$dc.DrawLine((P (B (C 255 220 50 50)) 18), ([Windows.Point]::new($jamFrom, $hwY + 30)), ([Windows.Point]::new($jamTo, $hwY + 30)))
$carColors = @((C 255 245 245 245), (C 255 255 196 70), (C 255 120 170 255), (C 255 245 245 245), (C 255 255 120 90))
$i = 0
for ($x = $jamFrom + 6; $x -lt $jamTo - 100; $x += 46) {
    foreach ($lane in @(-17, 17)) {
        $c = $carColors[$i % $carColors.Length]; $i++
        $dc.DrawRoundedRectangle((B $c), (P (B (C 255 30 30 34)) 2), ([Windows.Rect]::new($x, $hwY + $lane - 10, 36, 20)), 5, 5)
    }
}

# ---- 5. The live route around the jam ----
$route = G "M 70,470 L 230,470 C 290,470 300,330 380,330 L 650,330 C 730,330 740,470 800,470 L 905,470"
$dc.DrawGeometry($null, (P (B (C 70 74 222 128)) 64), $route)
$dc.DrawGeometry($null, (P (B (C 255 34 197 94)) 30), $route)
$dc.DrawGeometry($null, (P (B (C 255 187 247 208)) 8), $route)
# direction arrows along the top
$arrowBrush = B (C 255 255 255 255)
foreach ($ax in @(430, 520, 610)) {
    $dc.DrawGeometry($arrowBrush, $null, (G "M $($ax-12),318 L $($ax+10),330 L $($ax-12),342 Z"))
}

# start dot and destination pin
$dc.DrawEllipse((B (C 255 255 255 255)), (P (B (C 255 34 197 94)) 10), ([Windows.Point]::new(70, 470)), 22, 22)
$pin = G "M 905,470 C 870,420 860,400 860,375 A 45,45 0 1 1 950,375 C 950,400 940,420 905,470 Z"
$shadow = $pin.Clone(); $shadow.Transform = [Windows.Media.TranslateTransform]::new(6, 8)
$dc.DrawGeometry((B (C 110 0 0 0)), $null, $shadow)
$dc.DrawGeometry((B (C 255 34 197 94)), (P (B (C 255 255 255 255)) 6), $pin)
$dc.DrawEllipse((B (C 255 255 255 255)), $null, ([Windows.Point]::new(905, 375)), 17, 17)

# "LIVE" badge
$dc.DrawRoundedRectangle((B (C 235 220 38 38)), $null, ([Windows.Rect]::new(60, 70, 190, 74)), 37, 37)
$dc.DrawEllipse((B (C 255 255 255 255)), $null, ([Windows.Point]::new(103, 107)), 14, 14)
Text $dc 'LIVE' 50 (B (C 255 255 255 255)) 182 74

# ---- 6. Title band ----
$band = [Windows.Media.LinearGradientBrush]::new((C 0 8 12 16), (C 245 8 12 16), 90)
$dc.DrawRectangle($band, $null, ([Windows.Rect]::new(0, 700, $W, 324)))
Text $dc 'LIVE TRAFFIC' 128 (B (C 255 255 255 255)) 512 770 'Black'
Text $dc 'ROUTING' 96 (B (C 255 74 222 128)) 512 900 'Black'

$dc.Close()
$rtb = [Windows.Media.Imaging.RenderTargetBitmap]::new(1024, 1024, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($dv)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rtb))
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$fs = [IO.File]::Create($Out); $enc.Save($fs); $fs.Close()
Write-Host "Thumbnail written to $Out"
