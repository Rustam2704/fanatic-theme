# Applies the per-user Windows settings of the theme (registry, language bar, layout hotkeys, desktop menu, shared language).
# install.ps1 runs it through a one-shot scheduled task (elevated, the desktop user): a process started from a packaged
# (MSIX) app such as a terminal inside an app container writes HKCU / %LOCALAPPDATA% into the package's private copy, and the
# real Explorer started by Windows never sees those values (native clock / icons / Pen came back after an Explorer restart).
param([string]$Pack = (Split-Path $PSScriptRoot -Parent), [string]$Root = 'C:\fanatic', [string]$Language = 'en', [string]$Log, [switch]$LockScreen)
$ErrorActionPreference = 'Stop'
$out = New-Object System.Collections.Generic.List[string]
function Note([string]$t) { $out.Add($t) }
try {
  foreach ($r in 'theme-dark-transparency', 'dwm-accent', 'accent-palette', 'explorer-advanced', 'explorerpatcher',
                 'taskbar-hide-system-icons', 'langbar-hidden', 'desktop-black') {
    $p = Start-Process reg.exe -ArgumentList 'import', "`"$Pack\registry\$r.reg`"" -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "reg import failed ($($p.ExitCode)): $r" }
    Note "$r.reg"
  }
  Set-WinLanguageBarOption -UseLegacyLanguageBar; Note 'legacy language bar (Windows input indicator hidden)'
  $tg = 'HKCU:\Keyboard Layout\Toggle'; New-Item $tg -Force | Out-Null
  foreach ($v in 'Hotkey', 'Language Hotkey', 'Layout Hotkey') { Set-ItemProperty $tg -Name $v -Value '3' }
  Note 'Windows layout hotkeys off'
  $label = (Get-Content "$Pack\config\strings\$Language.json" -Raw -Encoding UTF8 | ConvertFrom-Json).'sky.ctxmenu'
  $sky = Join-Path $Root 'sky-stars\sky-stars.exe'; $k = 'HKCU:\Software\Classes\DesktopBackground\Shell\SkyStars'
  New-Item -Path "$k\command" -Force | Out-Null
  Set-ItemProperty $k -Name MUIVerb -Value $label; Set-ItemProperty $k -Name Icon -Value $sky; Set-ItemProperty $k -Name Position -Value Bottom
  Set-Item "$k\command" -Value "`"$sky`" --settings"; Note "desktop menu: $label"
  $shared = Join-Path $env:LOCALAPPDATA 'fanatic'; New-Item -ItemType Directory -Force $shared | Out-Null
  Set-Content -Path (Join-Path $shared 'lang.txt') -Value $Language -Encoding ASCII; Note "lang.txt = $Language"
  if ($LockScreen) {
    New-Item -ItemType Directory -Force 'C:\ProgramData\fanatic' | Out-Null
    Copy-Item "$Pack\assets\lockscreen\lockscreen-sky.png" 'C:\ProgramData\fanatic\' -Force
    $p = Start-Process reg.exe -ArgumentList 'import', "`"$Pack\registry\lockscreen-sky.reg`"" -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "reg import failed ($($p.ExitCode)): lockscreen-sky" }
    Note 'lock screen: the sky'
  }
  Note 'OK'
} catch { Note ('FAILED: ' + $_) }
if ($Log) { $out | Set-Content -Path $Log -Encoding UTF8 } else { $out }
