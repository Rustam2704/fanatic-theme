# fanatic UI baseline - uninstaller. Run from an ELEVATED PowerShell as the desktop user.
#   powershell -ExecutionPolicy Bypass -File install\uninstall.ps1 [-Root C:\fanatic] [-RemoveFiles]
# Undoes what install.ps1 added. Ordinary Windows look settings (dark mode, accent colour, Explorer options, ExplorerPatcher's
# own settings) are left as they are - change them in Settings if wanted.
param([string]$Root = 'C:\fanatic', [switch]$RemoveFiles)
$ErrorActionPreference = 'Continue'
function Note([string]$t) { Write-Host "    $t" -ForegroundColor DarkGray }

# clean exits first (a killed bar leaves ghost tray icons)
foreach ($exe in 'fanatic-bar\fanatic-bar.exe', 'sky-stars\sky-stars.exe') {
  $p = Join-Path $Root $exe; if (Test-Path $p) { Start-Process $p -ArgumentList '--quit' -Wait -WindowStyle Hidden; Note "quit $exe" }
}
Start-Sleep 2
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force

foreach ($t in 'fanatic-bar', 'sky-stars', 'keyboard-switch', 'screen-off', 'fan-tray', 'fanatic-time-sync',
               'msi-fan', 'msi-fan-eco', 'msi-fan-balanced', 'msi-fan-performance', 'everything-search') {
  $task = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
  # only our tasks: the action runs something under $Root, w32tm for the sync task, or msi-fan.ps1 under $Root
  if ($task -and ($task.Actions | Where-Object { "$($_.Execute) $($_.Arguments)" -like "*$Root*" -or $t -eq 'fanatic-time-sync' -or ($t -eq 'everything-search' -and $task.Description -like 'fanatic:*') })) {
    Unregister-ScheduledTask -TaskName $t -Confirm:$false; Note "task $t removed"
  }
}

# taskbar: give Windows' own clock / volume / power back, unfold the tray
$pol = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'
foreach ($v in 'HideClock', 'HideSCAVolume', 'HideSCAPower', 'HideSCAMeetNow') { Remove-ItemProperty $pol -Name $v -ErrorAction SilentlyContinue }
$tp = Join-Path $Root 'tray-prefs\tray-prefs.exe'; if (Test-Path $tp) { & $tp autotray off; Note 'tray: all icons shown again' }
Remove-Item 'HKCU:\Software\Classes\DesktopBackground\Shell\SkyStars' -Recurse -ErrorAction SilentlyContinue; Note 'desktop menu entry removed'
Remove-ItemProperty 'HKCU:\Software\Microsoft\CTF\LangBar' -Name ShowStatus -ErrorAction SilentlyContinue
Set-WinLanguageBarOption   # Windows' own input indicator back
# Windows' own layout hotkeys back to their defaults (Alt+Shift language, Ctrl+Shift layout)
$tg = 'HKCU:\Keyboard Layout\Toggle'; Set-ItemProperty $tg -Name 'Hotkey' -Value '1'; Set-ItemProperty $tg -Name 'Language Hotkey' -Value '1'; Set-ItemProperty $tg -Name 'Layout Hotkey' -Value '2'
Note 'Windows layout hotkeys restored (keyboard layouts themselves are left installed)'
$lk = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP'
if ((Get-ItemProperty $lk -ErrorAction SilentlyContinue).LockScreenImagePath -like '*\fanatic\*') {
  foreach ($v in 'LockScreenImagePath', 'LockScreenImageUrl', 'LockScreenImageStatus') { Remove-ItemProperty $lk -Name $v -ErrorAction SilentlyContinue }
  Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -Name DisableAcrylicBackgroundOnLogon -ErrorAction SilentlyContinue
  Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization' -Name LockScreenImage -ErrorAction SilentlyContinue
  Note 'lock screen picture policy removed'
}
Remove-Item (Join-Path $env:LOCALAPPDATA 'fanatic') -Recurse -ErrorAction SilentlyContinue; Note 'shared language/location removed'
if ($RemoveFiles) { Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item 'C:\ProgramData\fanatic' -Recurse -Force -ErrorAction SilentlyContinue; Note "$Root removed" }

Note 'restart Explorer (or log off and on) to get the stock taskbar icons back; pick a wallpaper in Settings > Personalization'
