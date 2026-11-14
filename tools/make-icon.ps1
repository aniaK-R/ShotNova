# Gera assets\ShotNova.ico (PNG embutido em vários tamanhos). Uso: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'assets\ShotNova.ico'
$sizes = 16,24,32,48,64,128,256

function New-Frame([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # fundo: quadrado arredondado com degradê azul
    $m = $S * 0.04; $w = $S - 2 * $m; $r = $S * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90); $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $S, $S
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255,96,165,250)), ([System.Drawing.Color]::FromArgb(255,37,99,235)), 45
    $g.FillPath($grad, $path)

    # área de seleção levemente clara no centro
    $in = $S * 0.27; $sel = $S - 2 * $in
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60,255,255,255))
    $g.FillRectangle($fill, $in, $in, $sel, $sel)

    # cantoneiras brancas (visor de captura)
    $pw = [Math]::Max(1.5, $S * 0.075); $L = $S * 0.2; $a = $S * 0.2; $b = $S - $a
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @(
        @(@($a,($a+$L)),@($a,$a),@(($a+$L),$a)),
        @(@(($b-$L),$a),@($b,$a),@($b,($a+$L))),
        @(@($b,($b-$L)),@($b,$b),@(($b-$L),$b)),
        @(@(($a+$L),$b),@($a,$b),@($a,($b-$L)))
    )
    foreach ($c in $pts) {
        $p = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ([single]$c[0][0]), ([single]$c[0][1])),
            (New-Object System.Drawing.PointF ([single]$c[1][0]), ([single]$c[1][1])),
            (New-Object System.Drawing.PointF ([single]$c[2][0]), ([single]$c[2][1])))
        $g.DrawLines($pen, $p)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

$frames = foreach ($s in $sizes) { ,(New-Frame $s) }
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close(); $fs.Close()
"gerado: $out ($((Get-Item $out).Length) bytes)"
