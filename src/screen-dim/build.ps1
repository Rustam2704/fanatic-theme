# Build screen-off.exe from source into <Root>\screen-dim (default C:\fanatic). Its task is created DISABLED by the installer.
param([switch]$NoStart, [string]$Root = 'C:\fanatic')
. (Join-Path $PSScriptRoot '..\build-common.ps1')
$out = Join-Path $Root 'screen-dim\screen-off.exe'; $tmp = Join-Path $env:TEMP 'screen-off-build.exe'
Compile $tmp @('/target:winexe', '/r:System.Management.dll', (Join-Path $PSScriptRoot 'src\ScreenOff.cs'))
StopCopy $out 'screen-off'
Place $tmp $out
DefaultConfig 'screen-off.json' (Split-Path $out)
if (-not $NoStart) { StartCopy $out 'screen-off' }
"built $out"
