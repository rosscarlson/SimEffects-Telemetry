# Generates src/GtTelemetry.App/Assets/App.ico (multi-size, PNG-compressed entries).
# Run with Windows PowerShell 5.1: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\GtTelemetry.App\Assets\App.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square background
    $r = [single]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(255, 0, 95, 184)), ([System.Drawing.Color]::FromArgb(255, 76, 194, 255))
    $g.FillPath($brush, $path)

    # gauge: 270-degree dial arc with a needle pointing to the upper right
    $pw = [single][Math]::Max(1.5, $s * 0.09)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $m = [single]($s * 0.22)
    $d = [single]($s - 2 * $m)
    $g.DrawArc($pen, $m, $m + $s * 0.04, $d, $d, 135, 270)
    $cx = [single]($s / 2); $cy = [single]($s / 2 + $s * 0.04)
    $len = $d * 0.42
    $a = -50 * [Math]::PI / 180
    $g.DrawLine($pen, $cx, $cy, [single]($cx + $len * [Math]::Cos($a)), [single]($cy + $len * [Math]::Sin($a)))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $hub = [single]($pw * 1.3)
    $g.FillEllipse($white, $cx - $hub, $cy - $hub, $hub * 2, $hub * 2)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Wrote $out"
