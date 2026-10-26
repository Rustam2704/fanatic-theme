# Restart Explorer CLEANLY (taskbar "Exit Explorer": WM_USER+436 to Shell_TrayWnd), then start it again.
# Never Stop-Process / taskkill /f explorer: a hard kill takes the desktop's child windows (sky-stars) down with it and
# produced "explorer.exe - Unknown Hard Error" on 2026-10-24 22:42 (research\review-2026-10-24\00-log.md, phase 4).
param([string]$Root = 'C:\fanatic')
$ErrorActionPreference = 'Stop'
Add-Type 'using System; using System.Runtime.InteropServices; public class ExpX { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t); [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }'
# Our foreign child windows (fanatic-bar's big indicators on the taskbar, sky-stars on the desktop) held Explorer's exit once
# (25.10 03:12): detach them first with their clean --quit, and start their tasks again at the end.
foreach ($exe in (Join-Path $Root 'fanatic-bar\fanatic-bar.exe'), (Join-Path $Root 'sky-stars\sky-stars.exe')) { if (Test-Path $exe) { Start-Process $exe -ArgumentList '--quit' -Wait -WindowStyle Hidden } }
Start-Sleep -Milliseconds 1500
$tray = [ExpX]::FindWindow('Shell_TrayWnd', [NullString]::Value)
if ($tray -ne [IntPtr]::Zero) {
  [ExpX]::PostMessage($tray, 0x5B4, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
  # wait for the TASKBAR to go, not the process: once (25.10) the shell windows closed but explorer.exe lingered with hidden
  # service windows only - waiting for the process left the user without a taskbar
  for ($i = 0; $i -lt 50 -and [ExpX]::FindWindow('Shell_TrayWnd', [NullString]::Value) -ne [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 200 }
}
if ([ExpX]::FindWindow('Shell_TrayWnd', [NullString]::Value) -ne [IntPtr]::Zero) { Write-Warning 'The taskbar did not close within 10 s; Explorer left running (no hard kill).'; return }
Start-Sleep -Milliseconds 800
# A shell explorer.exe can linger after its taskbar closed, with no windows (seen 25-26.10). The new shell then comes up
# half-working: the tray's COM object is unreachable (tray-prefs: REGDB_E_CLASSNOTREG) and new tray icons stay in ^.
# The shell is already gone at this point, so only window-less leftovers are ended; open folder windows are kept.
Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -eq [IntPtr]::Zero } | ForEach-Object { Stop-Process -Id $_.Id -Force; "ended a window-less leftover explorer.exe ($($_.Id))" }
Start-Sleep -Milliseconds 500
Start-Process "$env:WINDIR\explorer.exe"
Start-Sleep 6
foreach ($task in 'sky-stars', 'fanatic-bar') { Start-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue }
'explorer restarted'
