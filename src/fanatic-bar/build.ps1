# Build fanatic-bar.exe from source into <Root>\fanatic-bar (default C:\fanatic). -Shot <png> renders the state preview.
param([string]$Shot = "", [switch]$NoStart, [string]$Root = 'C:\fanatic')
. (Join-Path $PSScriptRoot '..\build-common.ps1')
$out = Join-Path $Root 'fanatic-bar\fanatic-bar.exe'; $tmp = Join-Path $env:TEMP 'fanatic-bar-build.exe'
$refs = 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Management.dll',
        "$Fw\System.Runtime.dll", "$Fw\System.Runtime.WindowsRuntime.dll", "$Fw\System.Runtime.InteropServices.WindowsRuntime.dll", "$Fw\System.Threading.Tasks.dll",
        'C:\Windows\System32\WinMetadata\Windows.Networking.winmd', 'C:\Windows\System32\WinMetadata\Windows.Foundation.winmd' | ForEach-Object { "/r:$_" }
Compile $tmp (@('/target:winexe') + $refs + (Get-ChildItem (Join-Path $PSScriptRoot 'src\*.cs') | ForEach-Object FullName))
StopCopy $out 'fanatic-bar' -Quit
Place $tmp $out
# runtime files next to the exe: strings (EN/UA/RU), forecast icons (Meteocons, MIT), default config only if none exists
$dir = Split-Path $out
Copy-Item (Join-Path $Pack 'config\strings') $dir -Recurse -Force
Copy-Item (Join-Path $Pack 'assets\weather-icons') $dir -Recurse -Force
DefaultConfig 'fanatic-bar.json' $dir
if ($Shot) { $p = Start-Process $out -ArgumentList '--shot', $Shot -PassThru -WindowStyle Hidden; $p.WaitForExit(20000) | Out-Null }
if (-not $NoStart) { StartCopy $out 'fanatic-bar' }
"built $out"
