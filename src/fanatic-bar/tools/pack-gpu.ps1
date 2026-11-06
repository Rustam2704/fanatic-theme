# .NET Framework's Icon loader treats PNG-backed ICO frames as DIB data on this Windows build.
# Keep all supplied resolutions and exact RGBA pixels, but package them as native 32-bit DIB frames.
param([Parameter(Mandatory=$true)][string]$Directory)
Add-Type -AssemblyName System.Drawing
foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter 'gpu-L*.ico') {
  $inputBytes = [IO.File]::ReadAllBytes($file.FullName)
  $inputStream = New-Object IO.MemoryStream(,$inputBytes)
  $reader = New-Object IO.BinaryReader($inputStream)
  $reader.ReadUInt16() | Out-Null; $reader.ReadUInt16() | Out-Null; $count = $reader.ReadUInt16()
  $frames = @(); $sizes = @()
  for ($i = 0; $i -lt $count; $i++) {
    $inputStream.Position = 6 + 16 * $i
    $w = [int]$reader.ReadByte(); if ($w -eq 0) { $w = 256 }
    $h = [int]$reader.ReadByte(); if ($h -eq 0) { $h = 256 }
    $reader.ReadUInt16() | Out-Null; $reader.ReadUInt16() | Out-Null; $reader.ReadUInt16() | Out-Null
    $length = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
    $inputStream.Position = $offset; $raw = $reader.ReadBytes($length)
    if ($raw[0] -ne 137 -or $raw[1] -ne 80) { $frames += ,$raw; $sizes += ,@($w,$h); continue }
    $png = New-Object IO.MemoryStream(,$raw); $bmp = [Drawing.Bitmap]::FromStream($png)
    $frame = New-Object IO.MemoryStream; $writer = New-Object IO.BinaryWriter($frame)
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$w); $writer.Write([int]($h * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([int]0)
    $writer.Write([int]($w*$h*4)); for ($j=0; $j -lt 4; $j++) { $writer.Write([int]0) }
    for ($y=$h-1; $y -ge 0; $y--) { for ($x=0; $x -lt $w; $x++) {
      $c=$bmp.GetPixel($x,$y); $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
    } }
    $writer.Write((New-Object byte[] ($maskStride*$h)))
    $frames += ,$frame.ToArray(); $sizes += ,@($w,$h)
    $writer.Dispose(); $frame.Dispose(); $bmp.Dispose(); $png.Dispose()
  }
  $reader.Dispose(); $inputStream.Dispose()
  $outputStream=New-Object IO.MemoryStream; $outWriter=New-Object IO.BinaryWriter($outputStream)
  $outWriter.Write([uint16]0); $outWriter.Write([uint16]1); $outWriter.Write([uint16]$count)
  $offset=6+16*$count
  for ($i=0; $i -lt $count; $i++) {
    $outWriter.Write([byte]($sizes[$i][0] % 256)); $outWriter.Write([byte]($sizes[$i][1] % 256))
    $outWriter.Write([uint16]0); $outWriter.Write([uint16]1); $outWriter.Write([uint16]32)
    $outWriter.Write([int]$frames[$i].Length); $outWriter.Write([int]$offset); $offset += $frames[$i].Length
  }
  foreach ($frame in $frames) { $outWriter.Write([byte[]]$frame) }
  [IO.File]::WriteAllBytes($file.FullName,$outputStream.ToArray()); $outWriter.Dispose(); $outputStream.Dispose()
}
