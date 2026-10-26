# Regenerates ..\src\DiabloCyr.cs from DiabloCyr.ttf (Ukrainian capitals + punctuation of the dates). Run after a new TTF, then ..\build.ps1.
param([string]$Font = (Join-Path $PSScriptRoot '..\..\..\assets\fonts\DiabloCyr.ttf'))   # pack layout
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /codepage:65001 /out:"$PSScriptRoot\ttf2cs.exe" "$PSScriptRoot\ttf2cs.cs"; if (-not $?) { throw 'ttf2cs build failed' }
& "$PSScriptRoot\ttf2cs.exe" $Font "$PSScriptRoot\..\src\DiabloCyr.cs" DiabloCyr "АБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ ,.'-" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE glyph(s) missing or empty" }
"wrote $PSScriptRoot\..\src\DiabloCyr.cs"
