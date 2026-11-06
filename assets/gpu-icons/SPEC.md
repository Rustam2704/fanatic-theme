# GPU-load catgirl icon - Windows implementer spec

Goal: tray icon = anime catgirl silhouette used as an alpha mask, filled with 4 horizontal load bands. Reproduce it from this folder.

## Rules
- Silhouette = alpha mask (`catgirl-mask-1024.png`, white on transparent, square 1024). Background outside the silhouette is fully transparent.
- Inside the mask: 4 equal-height horizontal bands, numbered 1..4 from the BOTTOM. Bands span the silhouette's vertical extent (bbox height).
- level = max(1, min(4, ceil(gpu_percent / 25))): 0% still lights band 1; 1-25 -> 1, 26-50 -> 2, 51-75 -> 3, 76-100 -> 4.
- L0 = all bands unlit, used for "GPU unknown/offline". Files L1..L4 = lit bands count.
- Gap: 1 px black (#000000, background) at the top of bands 1..3 (not band 4). Proportional: gap = size/26 px (1 px at 26 px; render.py draws at 8x supersampling then LANCZOS down).
- Original craftbox panel icon: 26 px tall, nearest-neighbour scaled on the Plasma panel. Poll NVIDIA utilisation once per 1 s.

| Band (bottom->top) | Lit colour |
|---|---|
| 1 | #48daf9 cyan |
| 2 | #28c840 green |
| 3 | #febc2e amber |
| 4 | #ff5f57 red |
| unlit (any) | #5b6b76 |
| gap | #000000 |

## Files
- `render.py` - the single generator (Pillow only, pathlib; `python render.py`, idempotent). Source: `catgirl-source.jpg` (1024x1024 black silhouette on white; ink = gray < 110, crop to bbox).
- `catgirl-mask-1024.png` - alpha mask, bbox-cropped, centred, padded to square.
- `catgirl-silhouette.svg` - single-path vector trace (viewBox 0 0 1024 1024), black fill; handy for custom rendering.
- `png/gpu-L{0..4}-{16,20,24,26,32,48,64,128}.png` - square, transparent RGBA (40 files).
- `ico/gpu-L{0..4}.ico` - multi-size ICO: 16, 24, 32, 48, 64, 128.
- `preview.png` - contact sheet (L0..L4, 26 px at 4x nearest + 128 px) on #202024.

Icons are square canvases; the silhouette is fitted by its larger dimension and centred.

## Windows notes
Tray icon is 16 px at 100% DPI, 20-24 px at 125-150%, 32+ at higher. Always ship the multi-size ICO and let Windows choose; do not scale a single bitmap. At 16 px the 1 px gaps are tight but legible.

## Implementation sketch (a): Python, pystray + pynvml
```python
import math, time, pynvml, pystray
from PIL import Image
icons = [Image.open(f"ico/gpu-L{i}.ico") for i in range(5)]   # or png/gpu-L{i}-32.png
pynvml.nvmlInit(); h = pynvml.nvmlDeviceGetHandleByIndex(0)
def lvl(p): return max(1, min(4, math.ceil(p / 25)))
tray = pystray.Icon("gpu", icons[0], "GPU")
def loop(icon):
    icon.visible = True
    while icon.visible:
        try: icon.icon = icons[lvl(pynvml.nvmlDeviceGetUtilizationRates(h).gpu)]
        except pynvml.NVMLError: icon.icon = icons[0]
        time.sleep(1)
tray.run(loop)
```
pystray takes a PIL image; load a 32 px PNG (`png/gpu-L{n}-32.png`) for best result since pystray rasterises one image itself.

## Implementation sketch (b): C# WinForms NotifyIcon
```csharp
var icons = Enumerable.Range(0,5).Select(i => new Icon($"ico/gpu-L{i}.ico", SystemInformation.SmallIconSize)).ToArray();
var ni = new NotifyIcon { Icon = icons[0], Visible = true, Text = "GPU" };
var t = new System.Windows.Forms.Timer { Interval = 1000 };
t.Tick += (s, e) => {
  int pct = ReadGpuPercent();   // NVML (nvml.dll via P/Invoke: nvmlDeviceGetUtilizationRates) or
                                // `nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits`
  ni.Icon = icons[pct < 0 ? 0 : Math.Max(1, Math.Min(4, (int)Math.Ceiling(pct / 25.0)))];
};
t.Start(); Application.Run();
```
Use the `Icon(string, Size)` constructor so the right ICO frame is picked for the current DPI. On failure (no driver) show L0.
