// Extends the laptop brightness range below the panel's hardware minimum.
// Windows brightness (Fn keys) >= Threshold: normal. Below it, a gamma-ramp dim is blended in, down to
// MinFactor of the hardware-minimum light at 0 %. Config (optional): screen-dim.json next to the exe:
//   { "threshold": 25, "minFactor": 0.30 }
// Requires HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM GdiIcmGammaRange = 256 (else Windows
// refuses strong ramps). Runs in the user session (logon task). The ramp at start (a calibration, if any) is the base:
// dimming scales it, and it is put back on exit. One thread applies ramps (the main loop); WMI events only wake it.
using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

static class ScreenDim
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(Func<int, bool> h, bool add);

    static int threshold = 25;
    static double minFactor = 0.30;
    static volatile int brightness = 100;
    static double applied = -1;
    static int appliedTop = -1;             // ramp[255] actually set (may be clamped by Windows)
    static ushort[] original;               // the ramp found at start (restored on exit)
    static readonly AutoResetEvent changed = new AutoResetEvent(false);
    static readonly object rampLock = new object();

    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "fanatic.ScreenDim", out created))
        {
            if (!created) return;
            LoadConfig();
            original = ReadRamp() ?? Ramp(null, 1.0);
            brightness = ReadBrightness();
            var watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += (s, e) => { using (var ev = e.NewEvent) brightness = Convert.ToInt32(ev["Brightness"]); changed.Set(); };
            watcher.Start();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Restore();
            // Re-check every 2 s: display changes, sleep, lock screen or other apps can reset the ramp; a brightness event at once.
            int tick = 0;
            while (true)
            {
                bool ev = changed.WaitOne(2000);
                if (!ev && ++tick % 15 == 0) brightness = ReadBrightness();   // safety net every 30 s
                Apply(ev);
            }
        }
    }

    static void LoadConfig()
    {
        string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screen-dim.json");
        if (!File.Exists(f)) return;
        string t = File.ReadAllText(f);
        var m1 = Regex.Match(t, "\"threshold\"\\s*:\\s*(\\d+)"); if (m1.Success) threshold = Math.Max(1, Math.Min(100, int.Parse(m1.Groups[1].Value)));
        var m2 = Regex.Match(t, "\"minFactor\"\\s*:\\s*([0-9.]+)"); if (m2.Success) minFactor = Math.Max(0.10, Math.Min(1.0, double.Parse(m2.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    static int ReadBrightness()
    {
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            using (var all = s.Get())
                foreach (ManagementObject o in all) using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return brightness;
    }

    static double Factor(int b) { return b >= threshold ? 1.0 : minFactor + (1.0 - minFactor) * b / threshold; }

    static void Apply(bool force)
    {
        double f = Factor(brightness);
        if (!force && Math.Abs(f - applied) < 0.001 && RampMatches(f)) return;
        SetFactor(f);
    }

    // The base ramp (the original one, or linear when null) scaled by f.
    static ushort[] Ramp(ushort[] baseRamp, double f)
    {
        var r = new ushort[768];
        for (int i = 0; i < 768; i++) r[i] = (ushort)Math.Min(65535, (int)((baseRamp != null ? baseRamp[i] : (i % 256) * 257) * f));
        return r;
    }
    static ushort[] ReadRamp()
    {
        var cur = new ushort[768]; IntPtr dc = GetDC(IntPtr.Zero);
        try { return GetDeviceGammaRamp(dc, cur) ? cur : null; } finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    static void Restore()
    {
        lock (rampLock) { IntPtr dc = GetDC(IntPtr.Zero); try { SetDeviceGammaRamp(dc, original); } finally { ReleaseDC(IntPtr.Zero, dc); } }
    }

    static bool RampMatches(double f)
    {
        var cur = new ushort[768]; IntPtr dc = GetDC(IntPtr.Zero);
        try { if (!GetDeviceGammaRamp(dc, cur)) return false; } finally { ReleaseDC(IntPtr.Zero, dc); }
        return Math.Abs(cur[255] - appliedTop) < 600;   // re-apply only if someone reset the ramp
    }

    // Windows rejects ramps that deviate "too much" unless GdiIcmGammaRange=256 is active (needs a reboot
    // after setting it); fall back to the strongest dim it accepts right now.
    static void SetFactor(double f)
    {
        lock (rampLock)
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                for (double t = f; t <= 1.0001; t += 0.02)
                {
                    var r = Ramp(original, Math.Min(1.0, t));
                    if (SetDeviceGammaRamp(dc, r)) { applied = f; appliedTop = r[255]; return; }
                }
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
    }
}
