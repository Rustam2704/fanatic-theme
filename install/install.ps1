# fanatic UI baseline - installer.
# Run from an ELEVATED PowerShell, logged in as the user who will use the desktop, in the unpacked pack folder:
#   powershell -ExecutionPolicy Bypass -File install\install.ps1                       # English UI, C:\fanatic
#   powershell -ExecutionPolicy Bypass -File install\install.ps1 -Language ua          # Ukrainian (ua) / ru / en
#   powershell -ExecutionPolicy Bypass -File install\install.ps1 -MsiFan               # ONLY MSI laptops with the EC fan control
# Every program is BUILT FROM SOURCE here (in-box .NET Framework 4.8 compiler). There are no prebuilt exes in the pack:
# a stale prebuilt binary next to fixed sources is exactly how old bugs came back on other machines.
param(
  [string]$Root = 'C:\fanatic',
  [string]$Language = 'en',            # any config\strings\<code>.json: en, ua, ru (+ drop-in translations)
  [switch]$MsiFan,                      # fan-tray + msi-fan SYSTEM tasks (MSI EC only; never on other hardware)
  [switch]$NoLockScreen,                # keep the current lock-screen picture
  [switch]$SkipExplorerPatcherCheck,    # build anyway without ExplorerPatcher (the bar needs it to attach to the taskbar)
  [switch]$SkipLayouts                  # do not add the UA / RU / EN keyboard layouts to the user's language list
)
$ErrorActionPreference = 'Stop'
$Pack = Split-Path $PSScriptRoot -Parent
function Step([string]$t) { Write-Host "`n=== $t" -ForegroundColor Cyan }
function Note([string]$t) { Write-Host "    $t" -ForegroundColor DarkGray }
# reg.exe reports success on stderr; Windows PowerShell 5.1 turns that into an error under EAP=Stop -> judge by the exit code
function RegImport([string]$file) {
  $p = Start-Process reg.exe -ArgumentList 'import', "`"$file`"" -Wait -PassThru -WindowStyle Hidden
  if ($p.ExitCode -ne 0) { throw "reg import failed ($($p.ExitCode)): $file" }
}

# ---------------------------------------------------------------- 0. checks
Step '0. Checks'
if (-not (Test-Path "$Pack\config\strings\$Language.json")) { throw "No translation config\strings\$Language.json (have: $((Get-ChildItem "$Pack\config\strings\*.json").BaseName -join ', '))" }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this from an ELEVATED PowerShell (Run as administrator), logged in as the desktop user.' }
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.8 compiler missing: $csc (it ships with Windows 10 1903+ / 11)." }
$ep = (Test-Path 'HKCU:\Software\ExplorerPatcher') -or (Test-Path "$env:ProgramFiles\ExplorerPatcher")
if (-not $ep -and -not $SkipExplorerPatcherCheck) {
  throw ("ExplorerPatcher is not installed. The bar attaches to EP's classic taskbar and does not work with the stock Windows 11 one.`n" +
         "Install ep_setup.exe from https://github.com/valinet/ExplorerPatcher/releases (tested: 26100.8457.70.3), then run this again.")
}
$os = Get-CimInstance Win32_OperatingSystem
Note "Windows $($os.Caption) build $($os.BuildNumber); root $Root; language $Language; MSI fan: $([bool]$MsiFan)"
foreach ($f in 'consola.ttf', 'bahnschrift.ttf') { if (-not (Test-Path "$env:WINDIR\Fonts\$f")) { Write-Warning "Font $f is missing (stripped Windows build?): flyout text / indicator digits will use a fallback font." } }

# ---------------------------------------------------------------- 1. build from source
Step '1. Build every program from source'
& "$Pack\src\fanatic-bar\build.ps1"        -Root $Root -NoStart
& "$Pack\src\sky\build.ps1"                -Root $Root -NoStart
& "$Pack\src\keyboard\build.ps1"           -Root $Root -NoStart
& "$Pack\src\screen-dim\build.ps1"         -Root $Root -NoStart
& "$Pack\src\scripts\tray-prefs\build.ps1" -Root $Root
if ($MsiFan) { & "$Pack\src\fan\build.ps1" -Root $Root -NoStart }
Copy-Item "$Pack\src\scripts\restart-explorer.ps1" $Root -Force
Copy-Item "$Pack\README.md" $Root -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 2. language (shared by the bar and the sky)
Step "2. UI language: $Language"
$shared = Join-Path $env:LOCALAPPDATA 'fanatic'
New-Item -ItemType Directory -Force $shared | Out-Null
Set-Content -Path (Join-Path $shared 'lang.txt') -Value $Language -Encoding ASCII
Note "switch later in the sky settings (right-click the desktop) - it changes the bar too"

# ---------------------------------------------------------------- 3. registry: look of Windows, taskbar, desktop
Step '3. Registry (visual settings only)'
foreach ($r in 'theme-dark-transparency', 'dwm-accent', 'accent-palette', 'explorer-advanced', 'explorerpatcher',
               'taskbar-hide-system-icons', 'langbar-hidden', 'desktop-black') {
  RegImport "$Pack\registry\$r.reg"; Note "$r.reg"
}
# black desktop NOW (the .reg alone needs a re-login; without black the old wallpaper shows through the sky)
Add-Type 'using System; using System.Runtime.InteropServices; public class FanDesk {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SystemParametersInfo(uint a, uint p, string v, uint f);
  [DllImport("user32.dll")] public static extern bool SetSysColors(int n, int[] e, int[] c); }'
[FanDesk]::SystemParametersInfo(20, 0, '', 3) | Out-Null                    # SPI_SETDESKWALLPAPER: no picture
[FanDesk]::SetSysColors(1, @(1), @(0)) | Out-Null                          # COLOR_DESKTOP = black
Note 'desktop background: black, applied now'
if (-not $NoLockScreen) {
  New-Item -ItemType Directory -Force 'C:\ProgramData\fanatic' | Out-Null
  Copy-Item "$Pack\assets\lockscreen\lockscreen-sky.png" 'C:\ProgramData\fanatic\' -Force
  RegImport "$Pack\registry\lockscreen-sky.reg"
  Note 'lock screen: the sky (C:\ProgramData\fanatic\lockscreen-sky.png)'
}
# desktop right-click "Sky..." -> sky settings (label in the chosen language, path of this install)
$label = (Get-Content "$Pack\config\strings\$Language.json" -Raw -Encoding UTF8 | ConvertFrom-Json).'sky.ctxmenu'
$sky = Join-Path $Root 'sky-stars\sky-stars.exe'
$k = 'HKCU:\Software\Classes\DesktopBackground\Shell\SkyStars'
New-Item -Path "$k\command" -Force | Out-Null
Set-ItemProperty $k -Name MUIVerb -Value $label; Set-ItemProperty $k -Name Icon -Value $sky; Set-ItemProperty $k -Name Position -Value Bottom
Set-Item "$k\command" -Value "`"$sky`" --settings"
Note "desktop context menu entry: $label"

# keyboard-switch owns Alt+Shift / Ctrl+Shift: Windows' own layout hotkeys must be OFF or every switch happens twice
$tg = 'HKCU:\Keyboard Layout\Toggle'; New-Item $tg -Force | Out-Null
foreach ($v in 'Hotkey', 'Language Hotkey', 'Layout Hotkey') { Set-ItemProperty $tg -Name $v -Value '3' }
Note "Windows' own Alt+Shift / Ctrl+Shift layout hotkeys: off (keyboard-switch handles them)"
if (-not $SkipLayouts) {   # keyboard-switch cycles Ukrainian / Russian / English: add the missing ones, never remove any
  $ll = Get-WinUserLanguageList; $have = @($ll | ForEach-Object LanguageTag)
  $add = @('uk', 'ru', 'en-US') | Where-Object { $have -notcontains $_ }
  if ($add) { foreach ($t in $add) { $ll.Add($t) }; Set-WinUserLanguageList $ll -Force; Note "keyboard layouts added: $($add -join ', ')" }
  else { Note 'keyboard layouts uk, ru, en-US already present' }
}

# ---------------------------------------------------------------- 4. scheduled tasks (logon autostart)
Step '4. Autostart tasks'
$me  = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
       -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -Priority 5
# one-shot / on-demand tasks: the defaults refuse to start on battery and the task sits "Queued" forever (Viksa, 26.10)
$once = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
function UserTask($name, $exe, $delay, $level, $desc) {
  $t = New-ScheduledTaskTrigger -AtLogOn -User $me; $t.Delay = "PT${delay}S"
  $p = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel $level
  Register-ScheduledTask -TaskName $name -Action (New-ScheduledTaskAction -Execute $exe) -Trigger $t -Principal $p -Settings $set -Description $desc -Force | Out-Null
  Note "$name  (+${delay}s, $level)"
}
UserTask 'fanatic-bar'     (Join-Path $Root 'fanatic-bar\fanatic-bar.exe')         3  Limited 'fanatic: big indicators, tray icons, flyouts'
UserTask 'sky-stars'       (Join-Path $Root 'sky-stars\sky-stars.exe')             2  Limited 'fanatic: starry sky desktop'
UserTask 'keyboard-switch' (Join-Path $Root 'keyboard-switch\keyboard-switch.exe') 2  Highest 'fanatic: keyboard layout switch + indicator'
UserTask 'screen-off'      (Join-Path $Root 'screen-dim\screen-off.exe')           10 Highest 'fanatic: screen off below the lowest brightness (optional)'
Disable-ScheduledTask -TaskName 'screen-off' | Out-Null; Note 'screen-off created DISABLED (Enable-ScheduledTask screen-off if wanted)'
# the clock flyout's "sync now" button runs this on demand: w32tm as SYSTEM, startable by any user
$sd = 'D:AI(A;;FR;;;SY)(A;;0x1200a9;;;AU)(A;;0x1f019f;;;BA)(A;;0x1f019f;;;SY)(A;;FA;;;BA)'
Register-ScheduledTask -TaskName 'fanatic-time-sync' -Action (New-ScheduledTaskAction -Execute "$env:WINDIR\System32\w32tm.exe" -Argument '/resync /force') `
  -Principal (New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest) -Settings $once -Description 'w32tm /resync on demand (fanatic-bar clock menu)' -Force | Out-Null
$svc = New-Object -ComObject Schedule.Service; $svc.Connect()
$svc.GetFolder('\').GetTask('fanatic-time-sync').SetSecurityDescriptor($sd, 0); Note 'fanatic-time-sync (SYSTEM, on demand)'
# optional: Ctrl+Alt+F (keyboard-switch) opens Everything search - only when Everything is installed
$ev = @("$env:ProgramFiles\Everything\Everything.exe", "${env:ProgramFiles(x86)}\Everything\Everything.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($ev) {
  Register-ScheduledTask -TaskName 'everything-search' -Action (New-ScheduledTaskAction -Execute $ev) -Settings $set `
    -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited) -Description 'fanatic: Ctrl+Alt+F opens Everything' -Force | Out-Null
  Note "everything-search -> $ev (Ctrl+Alt+F)"
} else { Note 'Everything not installed: Ctrl+Alt+F stays free' }
if ($MsiFan) {
  UserTask 'fan-tray' (Join-Path $Root 'fan-tray\fan-tray.exe') 4 Highest 'fanatic: CPU temperature / fan mode (MSI EC)'
  $ps = Join-Path $Root 'fan-tray\msi-fan.ps1'
  foreach ($n in 'msi-fan', 'msi-fan-eco', 'msi-fan-balanced', 'msi-fan-performance') {
    $xml = (Get-Content "$Pack\install\tasks\$n.xml" -Raw).Replace('D:\ai\system-setup\fan\msi-fan.ps1', $ps)
    Register-ScheduledTask -TaskName $n -Xml $xml -Force | Out-Null; Note "$n (SYSTEM) -> $ps"
  }
  foreach ($n in 'msi-fan-eco', 'msi-fan-balanced', 'msi-fan-performance') { $svc.GetFolder('\').GetTask($n).SetSecurityDescriptor($sd, 0) }
} else { Note 'fan-tray / msi-fan: skipped (MSI EC only; add -MsiFan on such a laptop)' }

# ---------------------------------------------------------------- 5. restart Explorer cleanly, start everything
Step '5. Restart Explorer (clean) and start the programs'
& (Join-Path $Root 'restart-explorer.ps1') -Root $Root
foreach ($t in 'keyboard-switch', 'fan-tray') { Start-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------- 6. tray: our icons stay, everything else folds into ^
# Run an action once in the user's session, un-elevated, and wait for it (Explorer's tray object and WinRT
# per-user settings refuse an elevated caller)
function RunAsUserOnce($name, $action) {
  Register-ScheduledTask -TaskName $name -Action $action -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited) -Settings $once -Force | Out-Null
  Start-ScheduledTask -TaskName $name; Start-Sleep 1
  for ($i = 0; $i -lt 60 -and (Get-ScheduledTask -TaskName $name).State -in 'Running', 'Queued'; $i++) { Start-Sleep 1 }
  Unregister-ScheduledTask -TaskName $name -Confirm:$false
}
Step '6. Tray: keep our icons on the taskbar, fold the rest into ^'
Start-Sleep 10   # Windows accepts the preference only for an icon it has already seen
$tp = Join-Path $Root 'tray-prefs\tray-prefs.exe'
$ours = @('fanatic-bar.exe', 'keyboard-switch.exe') + $(if ($MsiFan) { 'fan-tray.exe' } else { @() })
# tray-prefs talks to Explorer's tray COM object, which an ELEVATED process cannot reach (REGDB_E_CLASSNOTREG): run it
# un-elevated in the user's session through a one-shot task
$cmd = "& '$tp' set 2 $($ours -join ' '); & '$tp' autotray on"
$ta = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -Command $cmd"
RunAsUserOnce 'fanatic-tray-once' $ta
Note "tray: $($ours -join ', ') kept on the taskbar, the rest folds into ^"
Note 'if a bar icon ever lands in ^ later: run   tray-prefs set 2 fanatic-bar.exe   while it is visible, or drag it out'
if (-not $NoLockScreen) {
  # the sign-in screen (after a click on the account picture) shows the USER's lock picture, not the machine one: set it too
  RunAsUserOnce 'fanatic-lock-once' (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$Pack\src\scripts\user-lockscreen.ps1`"")
  Note 'sign-in screen: the same sky as the lock screen'
}

Step 'Done'
Write-Host @"
    Installed to $Root. Log off and on once to be sure every setting is applied.
    - Right-click the desktop -> "$label": stars, constellation lines/names, label background, Milky Way, language.
    - Weather flyout -> "update location" if the city is wrong (first start detects it from the IP).
    - Restart Explorer ONLY with  $Root\restart-explorer.ps1  (never taskkill /f explorer).
    - Optional: AltSnap 1.68 (https://github.com/RamonUnch/AltSnap/releases), then copy config\defaults\AltSnap.ini next to it.
    - Undo: install\uninstall.ps1
"@
