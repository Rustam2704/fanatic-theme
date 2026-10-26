// Fn brightness: the lowest ON level is 10. "Down" from 10 (Windows lowers to 0) -> the display turns off and the
// brightness goes back to 10; "up" while it is off -> the display comes back (at 10). Real keyboard / mouse input wakes
// it as usual. Why not "down at 0": Windows sends nothing when the panel is already at 0, and MSI_Event fires on the Fn
// key itself (same code for every Fn combination), so neither can tell that press apart. MSI_Event is only logged.
// Log: screen-off.log next to the exe (every brightness change and MSI_Event).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

static class ScreenOff
{
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;
    const uint WM_SYSCOMMAND = 0x112; const int SC_MONITORPOWER = 0xF170;

    static HashSet<long> codes = new HashSet<long> { 2228235 };
    static int holdMs = 1000, cooldownMs = 3000;
    static readonly object sync = new object();
    static readonly object logLock = new object();
    static int brightness = -1;
    static DateTime zeroSince = DateTime.MaxValue, lastOff = DateTime.MinValue;
    static string logFile;
    static ManagementEventWatcher msi, wb;
    static readonly System.Collections.Concurrent.BlockingCollection<int> requests = new System.Collections.Concurrent.BlockingCollection<int>();

    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "fanatic.ScreenOff", out created))
        {
            if (!created) return;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            logFile = Path.Combine(dir, "screen-off.log");
            LoadConfig(Path.Combine(dir, "screen-off.json"));
            Log("start codes=" + string.Join(",", codes) + " holdMs=" + holdMs);
            Poll();
            // one consumer takes the brightness requests in arrival order: fast Fn presses never race (off / on / restore)
            new Thread(() => { foreach (int b in requests.GetConsumingEnumerable()) OnBrightness(b); }) { IsBackground = true }.Start();
            DateTime lastTick = DateTime.UtcNow;
            while (true)
            {
                // A loop iteration far longer than the 5 s sleep means the machine slept: WMI event watchers can go
                // quiet after resume without raising Stopped, so recreate both.
                if ((DateTime.UtcNow - lastTick).TotalSeconds > 30) { Log("resume detected, restarting watchers"); StopWatcher(ref msi); StopWatcher(ref wb); }
                lastTick = DateTime.UtcNow;
                // WMI may not be ready right after logon (or winmgmt restarted): (re)start the watchers until both run.
                // MSI_Event is logged only: this EC sends the same code for brightness up, down (and other Fn keys),
                // so it cannot tell the direction. The decision uses the brightness requests themselves.
                if (msi == null) msi = StartWatcher("SELECT * FROM MSI_Event", (s, e) => { long c; using (var ev = e.NewEvent) c = ToLong(ev["MSIEvt"]); Log("MSI_Event " + c + " brightness=" + brightness); }, true);
                if (wb == null) wb = StartWatcher("SELECT * FROM WmiMonitorBrightnessEvent", (s, e) => { int b; using (var ev = e.NewEvent) b = Convert.ToInt32(ev["Brightness"]); requests.Add(b); }, false);
                Thread.Sleep(5000); Poll();   // events are instant; the poll is only a safety net
            }
        }
    }

    static ManagementEventWatcher StartWatcher(string query, EventArrivedEventHandler handler, bool isMsi)
    {
        ManagementEventWatcher w = null;
        try
        {
            w = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new EventQuery(query));
            w.EventArrived += handler;
            w.Stopped += (s, e) =>
            {
                Log("watcher stopped: " + query);
                if (isMsi) { if (ReferenceEquals(msi, w)) msi = null; }
                else { if (ReferenceEquals(wb, w)) wb = null; }
                ThreadPool.QueueUserWorkItem(_ => { try { w.Dispose(); } catch { } });
            };
            w.Start(); Log("watcher started: " + query); return w;
        }
        catch (Exception ex)
        {
            Log("watcher failed (retry in 5 s): " + query + " - " + ex.Message);
            if (w != null) { try { w.Dispose(); } catch { } }
            return null;
        }
    }


    static void StopWatcher(ref ManagementEventWatcher w)
    {
        var old = w; w = null;   // cleared first, so its Stopped handler finds nothing to reset
        if (old != null) { try { old.Stop(); } catch { } try { old.Dispose(); } catch { } }
    }

    static long ToLong(object v)
    {
        if (v is Array) { var a = (Array)v; return a.Length > 0 ? Convert.ToInt64(a.GetValue(0)) : -1; }
        try { return Convert.ToInt64(v); } catch { return -1; }
    }

    static void LoadConfig(string f)
    {
        try
        {
            if (!File.Exists(f)) return;
            string t = File.ReadAllText(f);
            var m = Regex.Match(t, "\"eventCodes\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (m.Success)
            {
                var set = new HashSet<long>();
                foreach (Match n in Regex.Matches(m.Groups[1].Value, "\\d+")) { long v; if (long.TryParse(n.Value, out v)) set.Add(v); }
                if (set.Count > 0) codes = set;
            }
            int iv;
            var h = Regex.Match(t, "\"holdMs\"\\s*:\\s*(\\d+)"); if (h.Success && int.TryParse(h.Groups[1].Value, out iv)) holdMs = iv;
            var c = Regex.Match(t, "\"cooldownMs\"\\s*:\\s*(\\d+)"); if (c.Success && int.TryParse(c.Groups[1].Value, out iv)) cooldownMs = iv;
        }
        catch (Exception ex) { Log("LoadConfig failed: " + ex.Message); }
    }

    static void Poll()
    {
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            using (var res = s.Get())
                foreach (ManagementObject o in res)
                    using (o) { Update(Convert.ToInt32(o["CurrentBrightness"])); return; }
        }
        catch { }
    }

    static void Update(int b)
    {
        lock (sync)
        {
            if (b == 0 && brightness != 0) zeroSince = DateTime.Now;
            if (b != 0) zeroSince = DateTime.MaxValue;
            brightness = b;
        }
    }

    [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    static bool displayOff; static uint offInputTick;

    static uint LastInputTick() { var i = new LASTINPUTINFO { cbSize = 8 }; GetLastInputInfo(ref i); return i.dwTime; }

    // One brightness request from the Fn keys (the consumer thread, one at a time):
    //   panel already at 0 for a moment and asked for 0 again   -> "down" at the minimum -> display off;
    //   display off by us and a request above 0 arrives          -> "up"                  -> display on.
    // The lowest level the screen stays ON at. A key press below it (to 0) means "off"; Windows sends nothing for a
    // press when the panel is already at 0, so 0 itself can't be the trigger.
    const int MinLevel = 10;
    static DateTime ownSetUntil = DateTime.MinValue;   // brightness events caused by our own restore are ignored

    static void OnBrightness(int b)
    {
        bool off = false, on = false, restore = false; int prev;
        lock (sync)
        {
            prev = brightness;
            if (DateTime.Now < ownSetUntil && b == MinLevel) { Update(b); Log("brightness " + prev + " -> " + b + " (own restore)"); return; }
            // Windows wakes the display by itself on real keyboard / mouse input.
            if (displayOff && LastInputTick() != offInputTick) displayOff = false;
            if (displayOff)
            {
                if (b > prev) { on = true; displayOff = false; restore = true; }   // "up" while off: back on at the minimum
                else if (b == 0) restore = true;                                   // "down" again while off: stay off
            }
            else if (b == 0 && prev > 0) { off = true; restore = true; }           // "down" from the minimum: off
            Update(b);
        }
        Log("brightness " + prev + " -> " + b + (off ? " : display OFF" : on ? " : display ON" : ""));
        IntPtr r;
        if (off)
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2, 0x2 /*ABORTIFHUNG*/, 1000, out r);
            lock (sync) { displayOff = true; offInputTick = LastInputTick(); lastOff = DateTime.Now; }
        }
        else if (on)
        {
            // A zero-distance mouse move is "input": the display comes back exactly like after touching the mouse.
            mouse_event(0x0001 /*MOVE*/, 0, 0, 0, IntPtr.Zero);
            SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1), 0x2, 1000, out r);
        }
        if (restore) SetBrightness(MinLevel);
    }

    static void SetBrightness(int level)
    {
        try
        {
            ownSetUntil = DateTime.Now.AddMilliseconds(1500);
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
            using (var res = s.Get())
                foreach (ManagementObject o in res) using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
        }
        catch (Exception ex) { Log("set brightness failed: " + ex.Message); }
    }

    static void Log(string m)
    {
        try
        {
            lock (logLock)
            {
                if (File.Exists(logFile) && new FileInfo(logFile).Length > 256 * 1024)
                {
                    var all = File.ReadAllLines(logFile);
                    File.WriteAllLines(logFile, all.Skip(Math.Max(0, all.Length - 200)).ToArray());
                }
                File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + m + Environment.NewLine);
            }
        }
        catch { }
    }
}
