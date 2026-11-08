# Genera src/Navegador/navegador.ico (16, 32, 48, 256 px; PNG dentro de ICO).
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$sizes = 16, 32, 48, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(138, 180, 248)), ([System.Drawing.Color]::FromArgb(52, 168, 83)), 45
    $g.FillEllipse($brush, 0.5, 0.5, $s - 1, $s - 1)
    # Pluma: el navegador liviano
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, $s / 14))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawBezier($pen, $s * 0.28, $s * 0.76, $s * 0.40, $s * 0.45, $s * 0.55, $s * 0.30, $s * 0.76, $s * 0.22)
    $g.DrawLine($pen, $s * 0.42, $s * 0.56, $s * 0.60, $s * 0.60)
    $g.DrawLine($pen, $s * 0.51, $s * 0.41, $s * 0.68, $s * 0.43)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
[IO.File]::WriteAllBytes((Join-Path $root 'src\Navegador\navegador.ico'), $out.ToArray())
