# Shared by every src\<app>\build.ps1 (dot-sourced). Builds from source with the in-box C# compiler and installs into
# <Root>\<folder>. Only the copy AT THE TARGET PATH is ever stopped or started: a same-named process or task elsewhere on the
# machine (another install, an older setup) is never touched.
$ErrorActionPreference = 'Stop'
$Fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$Pack = Split-Path $PSScriptRoot -Parent          # pack root (this file lives in <pack>\src)

function Compile([string]$tmp, [string[]]$cscArgs) {
  if (-not (Test-Path "$Fw\csc.exe")) { throw ".NET Framework 4.8 compiler not found: $Fw\csc.exe" }
  & "$Fw\csc.exe" /nologo /codepage:65001 /optimize+ "/out:$tmp" @cscArgs
  if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE) for $tmp" }
}

# The scheduled task of this copy, or $null when the task is missing or runs a different exe.
function OwnTask([string]$name, [string]$out) {
  $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
  if ($t -and ($t.Actions | Where-Object { $_.Execute -eq $out })) { return $t } else { return $null }
}

# Stop the copy at $out: its task, then a clean --quit (the bar and the sky remove their icons / windows themselves; a
# killed process leaves ghost tray icons), then kill whatever is still running from that exact path.
function StopCopy([string]$out, [string]$task, [switch]$Quit) {
  $t = OwnTask $task $out; if ($t) { Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue }
  $mine = { Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $out } }
  if ($Quit -and (Test-Path $out) -and (& $mine)) {
    Start-Process $out -ArgumentList '--quit' -WindowStyle Hidden -Wait
    & $mine | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
  }
  & $mine | Stop-Process -Force
  Start-Sleep -Milliseconds 500
}

function Place([string]$tmp, [string]$out) {
  New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
  Move-Item -Force $tmp $out
}

# Copy a default config only when the user has none (a rebuild never overwrites settings).
function DefaultConfig([string]$name, [string]$dir) {
  if (-not (Test-Path (Join-Path $dir $name))) { Copy-Item (Join-Path $Pack "config\defaults\$name") $dir }
}

function StartCopy([string]$out, [string]$task) {
  if (OwnTask $task $out) { Start-ScheduledTask -TaskName $task } else { Start-Process $out }
}
