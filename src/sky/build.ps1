# Build sky-stars.exe from source into <Root>\sky-stars (default C:\fanatic) with its data, strings and the Milky Way image.
param([switch]$NoStart, [string]$Root = 'C:\fanatic')
. (Join-Path $PSScriptRoot '..\build-common.ps1')
$out = Join-Path $Root 'sky-stars\sky-stars.exe'; $tmp = Join-Path $env:TEMP 'sky-stars-build.exe'
Compile $tmp (@('/target:winexe', "/win32manifest:$(Join-Path $PSScriptRoot 'src\sky-stars.manifest')", "/win32icon:$(Join-Path $PSScriptRoot 'src\sky-stars.ico')",
               '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll') + @(Get-ChildItem (Join-Path $PSScriptRoot 'src\*.cs') | ForEach-Object FullName))
StopCopy $out 'sky-stars' -Quit
Place $tmp $out
$dir = Split-Path $out
Copy-Item (Join-Path $PSScriptRoot 'data\*') $dir -Force                                       # star catalogue (HYG, CC BY-SA 4.0) + lines
Copy-Item (Join-Path $Pack 'config\strings') $dir -Recurse -Force
Copy-Item (Join-Path $Pack 'assets\milkyway\eso0932a.jpg') (Join-Path $dir 'milkyway.jpg') -Force   # ESO/S. Brunier, CC BY 4.0
DefaultConfig 'sky-stars.json' $dir
if (-not $NoStart) { StartCopy $out 'sky-stars' }
"built $out"
