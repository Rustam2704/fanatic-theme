# Build tray-prefs.exe (console tool) from source into <Root>\tray-prefs (default C:\fanatic).
param([string]$Root = 'C:\fanatic')
. (Join-Path $PSScriptRoot '..\..\build-common.ps1')
$out = Join-Path $Root 'tray-prefs\tray-prefs.exe'; $tmp = Join-Path $env:TEMP 'tray-prefs-build.exe'
Compile $tmp @('/target:exe', (Join-Path $PSScriptRoot 'TrayPrefs.cs'))
Place $tmp $out
"built $out"
