# Restart Explorer with its own Exit Explorer message. Never hard-kill a running shell.
param([string]$Root = 'C:\fanatic')
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ExpX {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
function Wait-Condition([scriptblock]$Condition, [int]$TimeoutMs) {
  $watch = [Diagnostics.Stopwatch]::StartNew()
  do {
    if (& $Condition) { return $true }
    Start-Sleep -Milliseconds 75
  } while ($watch.ElapsedMilliseconds -lt $TimeoutMs)
  return [bool](& $Condition)
}
function Get-Tray { [ExpX]::FindWindow('Shell_TrayWnd', [NullString]::Value) }
# Detach our child windows cleanly before closing their Explorer parent. Wait for the actual owners, not a fixed delay.
$children = @()
foreach ($exe in (Join-Path $Root 'fanatic-bar\fanatic-bar.exe'), (Join-Path $Root 'sky-stars\sky-stars.exe')) {
  $owners = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
  $children += $owners
  if ($owners.Count -gt 0 -and (Test-Path -LiteralPath $exe)) {
    $request = Start-Process $exe -ArgumentList '--quit' -PassThru -WindowStyle Hidden
    $request.WaitForExit(1000) | Out-Null
  }
}
$detached = Wait-Condition {
  @($children | Where-Object { -not $_.HasExited }).Count -eq 0
} 2500
if (-not $detached) { Write-Warning 'Bar/sky did not exit cleanly; Explorer left running.'; return }
$tray = Get-Tray
# Capture old processes only: never terminate an Explorer that AutoRestartShell starts during these waits.
$oldExplorers = @(Get-Process explorer -ErrorAction SilentlyContinue)
if ($tray -ne [IntPtr]::Zero) {
  [ExpX]::PostMessage($tray, 0x5B4, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
  $closed = Wait-Condition { (Get-Tray) -ne $tray } 10000
  if (-not $closed) { Write-Warning 'The taskbar did not close within 10 s; Explorer left running (no hard kill).'; return }
}
# AutoRestartShell=1 normally supplies the new shell. Avoid launching a competing explorer.exe.
$autoStarted = Wait-Condition { (Get-Tray) -ne [IntPtr]::Zero } 3000
if (-not $autoStarted) {
  # Only old, window-less leftovers, and only while no taskbar exists. Keep open folder windows.
  foreach ($old in $oldExplorers) {
    if ((Get-Tray) -ne [IntPtr]::Zero) { break }
    if ($old.HasExited) { continue }
    $old.Refresh()
    if ($old.MainWindowHandle -eq [IntPtr]::Zero) {
      Stop-Process -Id $old.Id -Force -ErrorAction SilentlyContinue
      "ended a window-less leftover explorer.exe ($($old.Id))"
    }
  }
  if ((Get-Tray) -eq [IntPtr]::Zero) { Start-Process "$env:WINDIR\explorer.exe" -WindowStyle Hidden }
}
$ready = Wait-Condition {
  $newTray = Get-Tray
  $newTray -ne [IntPtr]::Zero -and [ExpX]::FindWindowEx($newTray, [IntPtr]::Zero, 'TrayNotifyWnd', [NullString]::Value) -ne [IntPtr]::Zero
} 15000
if (-not $ready) { Write-Warning 'New taskbar/notification area did not appear within 15 s; tasks were not started.'; return }
foreach ($task in 'sky-stars', 'fanatic-bar') { Start-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue }
'explorer restarted'
