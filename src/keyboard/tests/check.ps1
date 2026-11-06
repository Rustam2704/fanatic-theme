param([string]$Root = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')) '.test-root-kb'))
$ErrorActionPreference = 'Stop'
$tmp = Join-Path $Root 'tmp'
New-Item -ItemType Directory -Force $tmp | Out-Null
$testExe = Join-Path $tmp 'keyboard-logic-tests.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /optimize+ /codepage:65001 "/out:$testExe" (Join-Path $PSScriptRoot 'KeyboardLogicTests.cs') (Join-Path $PSScriptRoot '..\src\LayoutLogic.cs')
if ($LASTEXITCODE -ne 0) { throw "test build failed ($LASTEXITCODE)" }
& $testExe (Join-Path $Root 'keyboard-switch\keyboard-switch.exe')
if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
