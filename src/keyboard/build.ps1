# Build keyboard-switch.exe from source into <Root>\keyboard-switch (default C:\fanatic). Run elevated (its task is elevated).
param([switch]$NoStart, [string]$Root = 'C:\fanatic')
. (Join-Path $PSScriptRoot '..\build-common.ps1')
$out = Join-Path $Root 'keyboard-switch\keyboard-switch.exe'; $tmp = Join-Path $env:TEMP 'keyboard-switch-build.exe'
Compile $tmp @('/target:winexe', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', (Join-Path $PSScriptRoot 'src\KeyboardSwitch.cs'), (Join-Path $PSScriptRoot 'src\Strings.cs'))
StopCopy $out 'keyboard-switch'
Place $tmp $out
Copy-Item (Join-Path $Pack 'config\strings') (Split-Path $out) -Recurse -Force   # UI text (shared language)
if (-not $NoStart) { StartCopy $out 'keyboard-switch' }
"built $out"
