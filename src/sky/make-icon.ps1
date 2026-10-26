# Builds src\sky-stars.ico: the Big Dipper as a micro constellation (7 stars, faint lines) on a black rounded square.
# Sizes 16..256, PNG-compressed entries. Run once; build.ps1 embeds the .ico with /win32icon.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'src\sky-stars.ico'
# Dubhe, Merak, Phecda, Megrez, Alioth, Mizar, Alkaid (unit square, y down)
$stars = @(@(0.84,0.30),@(0.86,0.55),@(0.62,0.64),@(0.56,0.44),@(0.40,0.40),@(0.25,0.36),@(0.08,0.50))
$mag   = @(1.8, 2.4, 2.4, 3.3, 1.8, 2.2, 1.9)
$lines = @(@(0,1),@(1,2),@(2,3),@(3,0),@(3,4),@(4,5),@(5,6))
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($n in $sizes) {
  $b = New-Object Drawing.Bitmap $n, $n; $g = [Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
  $r = [Math]::Max(2, $n * 0.18); $p = New-Object Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
  $p.AddArc(0, 0, $d, $d, 180, 90); $p.AddArc($n - 1 - $d, 0, $d, $d, 270, 90); $p.AddArc($n - 1 - $d, $n - 1 - $d, $d, $d, 0, 90); $p.AddArc(0, $n - 1 - $d, $d, $d, 90, 90); $p.CloseFigure()
  $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 4, 8, 14))), $p)
  $g.DrawPath((New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#1e262c')), ([Math]::Max(1, $n / 64))), $p)
  $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(150, 72, 218, 249)), ([Math]::Max(1, $n / 40))
  if ($n -ge 24) { foreach ($l in $lines) { $a = $stars[$l[0]]; $c = $stars[$l[1]]; $g.DrawLine($pen, [float]($a[0] * $n), [float]($a[1] * $n), [float]($c[0] * $n), [float]($c[1] * $n)) } }
  for ($i = 0; $i -lt 7; $i++) {
    $s = $stars[$i]; $rad = [Math]::Max(0.9, $n * (0.075 - 0.012 * ($mag[$i] - 1.8)))
    if ($n -ge 48) { $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(60, 223, 232, 238))), [float]($s[0] * $n - 2 * $rad), [float]($s[1] * $n - 2 * $rad), [float](4 * $rad), [float](4 * $rad)) }
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#dfe8ee'))), [float]($s[0] * $n - $rad), [float]($s[1] * $n - $rad), [float](2 * $rad), [float](2 * $rad))
  }
  $g.Dispose(); $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); $pngs += ,($ms.ToArray())
}
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $n = $sizes[$i]; $w.Write([byte]($(if ($n -ge 256) { 0 } else { $n }))); $w.Write([byte]($(if ($n -ge 256) { 0 } else { $n }))); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset); $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close(); "wrote $out ($((Get-Item $out).Length) bytes)"
