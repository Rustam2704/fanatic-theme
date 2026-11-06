# Run after build.ps1 -Root <test root> -NoStart. Optional apple-response.bin overrides the recorded live WLOC fixture.
param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference='Stop'
$fw='C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$source=Join-Path $PSScriptRoot '..\src'
$refs='System.Windows.Forms.dll','System.Drawing.dll','System.Management.dll','System.Web.Extensions.dll',
  "$fw\System.Runtime.dll","$fw\System.Runtime.WindowsRuntime.dll","$fw\System.Runtime.InteropServices.WindowsRuntime.dll","$fw\System.Threading.Tasks.dll",
  'C:\Windows\System32\WinMetadata\Windows.Networking.winmd','C:\Windows\System32\WinMetadata\Windows.Foundation.winmd' | ForEach-Object { "/r:$_" }
$out=Join-Path $Root 'fanatic-bar\bar-checks.exe'
& "$fw\csc.exe" /nologo /codepage:65001 /optimize+ /warnaserror+ /main:BarChecks "/out:$out" @refs (Get-ChildItem "$source\*.cs" | ForEach-Object FullName) (Join-Path $PSScriptRoot 'Checks.cs')
if($LASTEXITCODE -ne 0) { throw 'check compilation failed' }
& $out $Root
if($LASTEXITCODE -ne 0) { throw 'checks failed' }
