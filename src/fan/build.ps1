# OPTIONAL, MSI EC only. Build fan-tray.exe from source into <Root>\fan-tray (default C:\fanatic) together with the fan scripts.
param([switch]$NoStart, [string]$Root = 'C:\fanatic')
. (Join-Path $PSScriptRoot '..\build-common.ps1')
$out = Join-Path $Root 'fan-tray\fan-tray.exe'; $tmp = Join-Path $env:TEMP 'fan-tray-build.exe'
Compile $tmp @('/target:winexe', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll', (Join-Path $PSScriptRoot 'src\FanTray.cs'), (Join-Path $PSScriptRoot 'src\Strings.cs'))
StopCopy $out 'fan-tray'
Place $tmp $out
Copy-Item (Join-Path $Pack 'config\strings') (Split-Path $out) -Recurse -Force   # UI text (shared language)
$dir = Split-Path $out
Copy-Item (Join-Path $PSScriptRoot 'msi-fan.ps1'), (Join-Path $PSScriptRoot 'msi-ec.ps1') $dir -Force    # used by the msi-fan tasks
if (-not (Test-Path (Join-Path $dir 'fan-profile.json'))) { Copy-Item (Join-Path $PSScriptRoot 'fan-profile.json') $dir }
if (-not $NoStart) { StartCopy $out 'fan-tray' }
"built $out"
