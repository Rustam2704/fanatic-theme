# screen-off — Fn brightness-down at 0 % turns the display off, brightness-up turns it on

`D:\soft\screen-dim\screen-off.exe` (source `src\ScreenOff.cs`), started by task `screen-off` at logon +10 s (elevated, Priority 5, restarts on failure); no on-screen OSD.

- The Fn brightness keys produce no keyboard events on this MSI. The BIOS WMI event `MSI_Event` carries the same code
  (2228235) for brightness up, down and other Fn keys, so it is no longer used for decisions. The tool listens to
  `WmiMonitorBrightnessEvent` and brightness requests.
- Display OFF: a brightness request of 0 arrives while the panel is already at 0 for >= `holdMs` (600 ms).
- Display ON: the display is off and a request > 0 arrives (zero mouse move + `SC_MONITORPOWER -1`).
- Every request is logged. Actions are rate-limited by `cooldownMs`.
- Config `D:\soft\screen-dim\screen-off.json`: `{ "holdMs": 600, "cooldownMs": 1500 }`.
- Log `D:\soft\screen-dim\screen-off.log`.
- Rollback: `Unregister-ScheduledTask screen-off` and stop the process.

History: the earlier gamma-dim tool (`screen-dim.exe`, source kept as `src\ScreenDim.cs`) was replaced on 2026-10-22 at the owner's request ("below the minimum = display off"); logic rewritten 2026-10-24.

After sleep/resume (main loop sees a >30 s gap) the WMI watchers are recreated, logged as `resume detected, restarting watchers`.
