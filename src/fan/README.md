# msi-fan — fan / power modes for MSI Thin GF63 12VE without MSI Center

Talks to the embedded controller through the BIOS ACPI-WMI interface `root\wmi : MSI_ACPI`
(`Get_Data` / `Set_Data`), the same channel GhostDeck uses. No drivers. EC firmware `16R8IMS1.117`.

| File | What |
|---|---|
| `fan-profile.json` | 3 modes (performance / balanced / eco): shift byte, super battery, 6 temperature thresholds, 6 hysteresis offsets, 7 speeds; `activeMode` |
| `msi-fan.ps1` | `status`, `mode <name>`, `apply`, `check`, `backup`, `restore <dump>` (admin) |
| `msi-ec.ps1` | EC library: read/write byte with read-back, table read/write, validation (`Test-FanCurve`: whole numbers only, no 100) |
| `backups\` | EC dumps (before every apply that changes something; last 20 kept) |
| `msi-fan.log` | applies, mode switches, drift fixes |

Curve layout: `speeds[0]` below `upTemps[0]`, `speeds[i]` from `upTemps[i-1]`, `speeds[6]` above `upTemps[5]`
(MSI Center only shows 6 points and hides the last band). Speeds are EC curve units 0-150 (not percent); 120+ = max 6666 rpm.
**Never exactly 100 %** — this EC stops the fan (measured 2026-10-20); validation rejects it.

Mode recipes (GhostDeck `data/models.json`, entry "MSI Thin GF63 12VE", verified):
performance `0xD2=C4 0xEB=00`, balanced `0xD2=C1 0xEB=00`, eco `0xD2=C2 0xEB=0F`; fan mode always `0xD4=8D` (curve).

Tasks (SYSTEM, hidden): `msi-fan` (runs `msi-fan.ps1 check`, global mutex `Global\fanatic.MsiFan`, RestartOnFailure) — boot+30 s, logon+20 s, resume, every 10 min: re-applies the active
mode if anything drifted. The EC is written before the config file is updated; restore validates values (never 100 %); mode names are case-insensitive. `msi-fan-performance|balanced|eco` — on demand, runnable by any user
(Start menu → "Fan mode" shortcuts → `D:\soft\fan-tray\fan-tray.exe --mode <m>`: runs the task, shows the OSD, exits; no UAC, no window. They used to target conhost.exe, which made Explorer label every console window on the taskbar "Eco (Економія)").

Tray (`fan-tray`, `src\FanTray.cs`; task at logon +10 s, Priority 5): icon = CPU °C, bold (light blue <70 °C, yellow 70–84, red 85+);
tooltip `CPU 64 °C · 1677 об/хв · Баланс` (rpm only; "вентилятор стоїть" at 0 rpm), blank while the menu is open. Dark right-click menu:
modes Performance / Balanced / Eco; "Підсвітка клавіатури" submenu (Вимкнено / 1 / 2 / 3, EC `0xD3`, e.g. level 3 = `0x83`);
"Cooler Boost" toggle (EC `0x98` bit 7, on = `0x86`, off = `0x06`); "Вихід". Mode / Cooler Boost / backlight changes show a non-activating OSD.
