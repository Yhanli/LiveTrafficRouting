param([string]$Src, [string]$Dst)
Add-Type -AssemblyName PresentationCore, WindowsBase
$map = [ordered]@{
    '26.webp' = '1-interchange.jpg'
    '31.png'  = '2-hotspots-native-vs-live.jpg'
    '29.png'  = '3-jam-warnings-by-hour.jpg'
    '30.png'  = '4-queues-timeline.jpg'
    '27.png'  = '5-right-now.jpg'
    '33.png'  = '6-rerouting.jpg'
    '35.png'  = '7-runs.jpg'
    '36.webp' = '8-options.jpg'
}
New-Item -ItemType Directory -Force $Dst | Out-Null
foreach ($k in $map.Keys) {
    $in = Join-Path $Src $k
    $out = Join-Path $Dst $map[$k]
    try {
        $fs = [IO.File]::OpenRead($in)
        $dec = [Windows.Media.Imaging.BitmapDecoder]::Create($fs, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $dec.Frames[0]
        $fs.Close()
        # JPG has no transparency: flatten onto black first.
        $conv = [Windows.Media.Imaging.FormatConvertedBitmap]::new($frame, [Windows.Media.PixelFormats]::Bgr24, $null, 0)
        $enc = [Windows.Media.Imaging.JpegBitmapEncoder]::new()
        $enc.QualityLevel = 92
        $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($conv))
        $o = [IO.File]::Create($out); $enc.Save($o); $o.Close()
        "{0} -> {1} ({2}x{3}, {4:N0} KB)" -f $k, $map[$k], $frame.PixelWidth, $frame.PixelHeight, ((Get-Item $out).Length / 1KB)
    }
    catch {
        "FAILED $k : $($_.Exception.Message)"
    }
}
