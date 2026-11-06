# Regenerate the compiled outlines with the in-box compiler; the converted TTF is the only input.
param([string]$Font = (Join-Path $PSScriptRoot '..\..\..\assets\fonts\Mainframe-Bold.ttf'))
$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$tool = Join-Path $env:TEMP 'ttf2cs-mainframe.exe'
& $csc /nologo /codepage:65001 "/out:$tool" (Join-Path $PSScriptRoot 'ttf2cs.cs')
if ($LASTEXITCODE -ne 0) { throw 'ttf2cs build failed' }
& $tool $Font (Join-Path $PSScriptRoot '..\src\MainframeGlyphs.cs') Mainframe --mapped --metrics | Out-Null
if ($LASTEXITCODE -ne 0) { throw "$LASTEXITCODE glyph(s) missing or empty" }
"wrote $PSScriptRoot\..\src\MainframeGlyphs.cs"
