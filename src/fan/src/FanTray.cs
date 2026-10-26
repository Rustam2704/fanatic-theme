// Tray icon for the fan tool: shows the CPU temperature (number in the icon, tooltip with fan rpm and mode),
// right-click menu switches performance / balanced / eco by starting the msi-fan-<mode> scheduled tasks
// (runnable by any user, no UAC, no window). Reads the EC through BIOS WMI MSI_ACPI (needs elevation → logon task).
// EC reads run on a timer thread; only icon/text updates go to the UI thread. A non-activating OSD confirms a mode switch.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class FanTray
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int i, uint dpi);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ChangeWindowMessageFilter(uint msg, uint flag);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // fan-profile.json lives next to fan-tray.exe together with msi-fan.ps1 / msi-ec.ps1 (baseline layout <root>\fan-tray\).
    static readonly string ProfileFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fan-profile.json");
    static readonly string[] Modes = { "performance", "balanced", "eco" };
    // localised (strings\<lang>.json, shared language): menu label and local mode name per mode
    static string Label(int i) { return Strings.T("fan.label." + Modes[i]); }
    static readonly string[] OsdNames = { "Performance", "Balanced", "Eco" };
    static string ModeName(int i) { return Strings.T("fan.mode." + Modes[i]); }
    static string UaMode(string m) { int i = Array.IndexOf(Modes, m); return i < 0 ? m : ModeName(i); }
    static NotifyIcon tray; static ManagementObject acpi; static ManagementClass pkgClass; static ToolStripMenuItem[] items = new ToolStripMenuItem[3];
    // Tray icon size at the taskbar's DPI (per-monitor): a live display-scale change (125 % <-> 150 %) keeps the session's system DPI,
    // and an icon drawn for it would be stretched by the shell (blurred). Same as fanatic-bar.
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "FindWindowW")] static extern IntPtr FindTrayWindow(string cls, string title);
    static uint TaskbarDpi() { IntPtr t = FindTrayWindow("Shell_TrayWnd", null); uint d = t != IntPtr.Zero ? GetDpiForWindow(t) : 0; return d > 0 ? d : GetDpiForSystem(); }
    static int TrayIconSize() { int s = GetSystemMetricsForDpi(49, TaskbarDpi()); return s > 0 ? s : 16; }
    static void PerMonitorDpi() { if (!SetProcessDpiAwarenessContext((IntPtr)(-4))) SetProcessDPIAware(); }    static int lastTemp = -2, lastSize; static Icon lastIcon;
    static SynchronizationContext ui;
    static System.Threading.Timer pollTimer;
    static int busy;
    static FontFamily iconFamily, osdFamily;
    static Osd osd;
    static readonly object ecLock = new object();
    static Mutex fanMutex;
    static bool menuOpen; static string lastText = "Fan";
    static ToolStripMenuItem kbdRoot, boostItem; static ToolStripMenuItem[] kbdItems = new ToolStripMenuItem[4];
    static ContextMenuStrip menu, fanMenu; static ToolStripMenuItem fanInfo, fanBoost; static volatile int lastRpm = -1;

    static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fan-tray.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + m + Environment.NewLine); } catch { }
    }

    static FontFamily MakeFamily(string name)
    {
        try { return new FontFamily(name); } catch { return new FontFamily("Segoe UI"); }
    }

    // Small borderless, topmost, non-activating popup (never takes focus, click-through).
    class Osd : Form
    {
        string text = ""; Font font; Region rgn;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 32); Opacity = 0.9; TopMost = true; DoubleBuffered = true;
            timer.Tick += (s, e) => { timer.Stop(); Hide(); };
        }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x8 | 0x20 | 0x80000; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        public void ShowText(string t, int lw, int lh, int fontPx, int ms)
        {
            double sc = GetDpiForSystem() / 96.0;
            int w = (int)(lw * sc), h = (int)(lh * sc);
            text = t;
            if (osdFamily == null) osdFamily = MakeFamily("Bahnschrift SemiBold");
            if (font != null) font.Dispose();
            font = new Font(osdFamily, (float)(fontPx * sc), FontStyle.Regular, GraphicsUnit.Pixel);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Bottom - (int)(wa.Height * 0.15) - h, w, h);
            int d = (int)(2 * 14 * sc);
            using (var gp = new GraphicsPath())
            {
                gp.AddArc(0, 0, d, d, 180, 90); gp.AddArc(w - d, 0, d, d, 270, 90);
                gp.AddArc(w - d, h - d, d, d, 0, 90); gp.AddArc(0, h - d, d, d, 90, 90); gp.CloseFigure();
                var old = rgn; rgn = new Region(gp); Region = rgn; if (old != null) old.Dispose();
            }
            if (!Visible) { Opacity = 0; Show(); Update(); Opacity = 0.9; }   // paint while transparent: no white first frame
            else Invalidate();
            SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x2 | 0x1 | 0x10);   // topmost, NOMOVE|NOSIZE|NOACTIVATE
            timer.Stop(); timer.Interval = ms; timer.Start();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Color.White))
                e.Graphics.DrawString(text, font, b, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), sf);
        }
    }

    static void ShowOsd(string t)
    {
        try { if (osd == null) osd = new Osd(); osd.ShowText(t, 200, 70, 26, 900); } catch (Exception ex) { Log("osd: " + ex.Message); }
    }

    // "fan-tray.exe --mode eco": used by the Start-menu "Fan mode" shortcuts. Runs the task, shows the OSD, exits.
    // (A GUI exe instead of conhost.exe as the shortcut target, so the shortcuts don't claim every console window on the taskbar.)
    static void RunModeOnce(string mode)
    {
        int idx = Array.IndexOf(Modes, mode.ToLowerInvariant());
        if (idx < 0) return;
        SetProcessDPIAware(); Application.EnableVisualStyles();
        int code = RunModeTask(idx);
        osd = new Osd();
        osd.VisibleChanged += (s, e) => { if (!osd.Visible) Application.ExitThread(); };
        var guard = new System.Windows.Forms.Timer { Interval = 5000 };   // never linger if the OSD failed to show
        guard.Tick += (s, e) => Application.ExitThread(); guard.Start();
        ShowOsd(code == 0 ? OsdNames[idx] : Strings.T("fan.osd.error", OsdNames[idx]));
        Application.Run();
    }

    static int RunModeTask(int idx)
    {
        int code = -1;
        try
        {
            var p = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Run /TN msi-fan-" + Modes[idx]) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var pr = Process.Start(p)) { if (pr.WaitForExit(10000)) code = pr.ExitCode; }
        }
        catch (Exception ex) { Log("switch: " + ex.Message); }
        if (code != 0) Log("switch " + Modes[idx] + ": schtasks exit " + code);
        return code;
    }

    [STAThread]
    static void Main(string[] args)
    {
        Strings.Load(Strings.ResolveLang("en"));   // same shared language as the bar and the sky
        if (args.Length == 2 && args[0] == "--mode") { RunModeOnce(args[1]); return; }
        bool created;
        using (var m = new Mutex(true, "fanatic.FanTray", out created))
        {
            if (!created) return;
            PerMonitorDpi(); Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log("ThreadException: " + e.Exception);
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            // Elevated process: let Explorer's TaskbarCreated broadcast through UIPI so the icon returns after an explorer restart.
            ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), 1 /*MSGFLT_ADD*/);
            tray = new NotifyIcon { Visible = true, Text = "Fan" };
            menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = true };
            for (int i = 0; i < 3; i++) { int idx = i; items[i] = new ToolStripMenuItem(Label(i), null, (s, e) => Switch(idx)); menu.Items.Add(items[i]); }
            menu.Items.Add(new ToolStripSeparator());
            kbdRoot = new ToolStripMenuItem(Strings.T("power.kbd_backlight.title"));
            string[] kl = { Strings.T("fan.kbd.off"), "1", "2", "3" };
            for (int i = 0; i < 4; i++) { int lv = i; kbdItems[i] = new ToolStripMenuItem(kl[i], null, (s, e) => SetBacklight(lv)); kbdRoot.DropDownItems.Add(kbdItems[i]); }
            kbdRoot.DropDown.Renderer = new DarkMenuRenderer();
            menu.Items.Add(kbdRoot);
            boostItem = new ToolStripMenuItem(Strings.T("fan.boost"), null, (s, e) => ToggleBoost());
            menu.Items.Add(boostItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Strings.T("menu.exit"), null, (s, e) => Application.Exit());
            menu.Opening += (s, e) => { string a = ActiveMode(); for (int i = 0; i < 3; i++) items[i].Checked = Modes[i] == a; RefreshExtras(); RefreshEcAsync(); };
            // The tooltip would cover the open menu: blank it while the menu is shown.
            menu.Opened += (s, e) => { menuOpen = true; tray.Text = ""; };
            menu.Closed += (s, e) => { menuOpen = false; tray.Text = lastText; };
            tray.ContextMenuStrip = menu;
            BuildFanMenu();
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFanMenu(); };
            pollTimer = new System.Threading.Timer(_ => Poll(), null, 0, 3000);
            // less work when nobody looks: no EC polling while the session is locked, every 15 s on battery (3 s otherwise)
            Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) { locked = true; SetPollRate(); }
                else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) { locked = false; SetPollRate(); }
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) SetPollRate(); };
            SetPollRate();
            // brightness on hover (as fanatic-bar): the icon at 78 % brightness, 100 % while the pointer is over it
            tray.MouseMove += (s, e) => { if (!hot) { hot = true; Apply(lastTempShown, lastFullText); } };
            var hoverTimer = new System.Windows.Forms.Timer { Interval = 150 };
            hoverTimer.Tick += (s, e) => { if (hot && !PointerOnIcon()) { hot = false; Apply(lastTempShown, lastFullText); } };
            hoverTimer.Start();
            StartKbdBridge();
            Application.Run();
            hoverTimer.Dispose(); pollTimer.Dispose();
            tray.Visible = false;
        }
    }

    static ManagementObject FindAcpi()
    {
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM MSI_ACPI"))
            using (var all = s.Get())
            {
                ManagementObject found = null;
                foreach (ManagementObject o in all) { if (found == null) found = o; else o.Dispose(); }
                return found;
            }
        }
        catch { return null; }
    }
    // Drop the WMI objects (they are re-acquired on the next access) and free them.
    static void ResetWmi()
    {
        var a = acpi; var p = pkgClass; acpi = null; pkgClass = null;
        if (a != null) a.Dispose(); if (p != null) p.Dispose();
    }

    // All EC access is serialized by ecLock (timer thread, menu handlers).
    static int Ec(byte addr)
    {
        lock (ecLock) { return EcRead(addr); }
    }

    // Writes one byte through MSI_ACPI.Set_Data and verifies by reading it back; mismatches go to the log.
    // Also holds msi-fan.ps1's Global\fanatic.MsiFan mutex, so a tray write never interleaves with a profile apply.
    static bool EcWrite(byte addr, byte val)
    {
        lock (ecLock)
        {
            bool held = false;
            try
            {
                if (fanMutex == null) { bool created; fanMutex = new Mutex(false, @"Global\fanatic.MsiFan", out created); }
                try { held = fanMutex.WaitOne(5000); } catch (AbandonedMutexException) { held = true; }
                if (!held) { Log(string.Format("ecwrite 0x{0:X2}: msi-fan lock busy", addr)); return false; }
                if (acpi == null) acpi = FindAcpi();
                if (acpi == null) { Log("ecwrite: no MSI_ACPI"); return false; }
                if (pkgClass == null) pkgClass = new ManagementClass(@"root\wmi", "Package_32", null);
                using (var pkg = pkgClass.CreateInstance())
                using (var inp = acpi.GetMethodParameters("Set_Data"))
                {
                    var b = new byte[32]; b[0] = addr; b[1] = val; pkg["Bytes"] = b; inp["Data"] = pkg;
                    using (var outp = acpi.InvokeMethod("Set_Data", inp, null))
                    using (var data = (ManagementBaseObject)outp["Data"])
                    {
                        var r = (byte[])data["Bytes"];
                        if (r[0] != 1) { Log(string.Format("ecwrite 0x{0:X2}: Set_Data returned {1}", addr, r[0])); return false; }
                    }
                }
                int back = EcRead(addr);
                if (back != val) { Log(string.Format("ecwrite 0x{0:X2}: mismatch wrote {1} read {2}", addr, val, back)); return false; }
                return true;
            }
            catch (Exception ex) { ResetWmi(); Log("ecwrite: " + ex.Message); return false; }
            finally { if (held) fanMutex.ReleaseMutex(); }
        }
    }

    // Left click: cooling only (temperature + fan rpm, Cooler Boost). Modes and the rest stay on the right-click menu.
    static void BuildFanMenu()
    {
        fanMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = true };
        fanInfo = new ToolStripMenuItem("") { Enabled = false };
        fanBoost = new ToolStripMenuItem(Strings.T("fan.boost"), null, (s, e) => ToggleBoost());
        fanMenu.Items.Add(fanInfo); fanMenu.Items.Add(new ToolStripSeparator()); fanMenu.Items.Add(fanBoost);
        // the last polled values at once (a slow WMI never holds the menu), then a fresh read updates the open menu
        fanMenu.Opening += (s, e) => { FillFanMenu(); RefreshEcAsync(); };
        fanMenu.Opened += (s, e) => { menuOpen = true; tray.Text = ""; };
        fanMenu.Closed += (s, e) => { menuOpen = false; tray.Text = lastText; tray.ContextMenuStrip = menu; };
    }
    static void FillFanMenu()
    {
        int rpm = lastRpm, b98 = lastB98;
        fanInfo.Text = "CPU " + (lastTemp >= 0 ? lastTemp + " °C" : "—") + " · " + (rpm > 0 ? Strings.T("fan.rpm", rpm) : rpm == 0 ? Strings.T("fan.stopped") : Strings.T("fan.no_data"));
        fanBoost.Enabled = b98 >= 0; fanBoost.Checked = b98 >= 0 && (b98 & 0x80) != 0;
    }
    // EC values for the menus, kept by the poll (and refreshed when a menu opens); -1 = unknown
    static volatile int lastD3 = -1, lastB98 = -1;
    static int refreshing;
    static void RefreshEcAsync()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { lastD3 = Ec(0xD3); lastB98 = Ec(0x98); ui.Post(__ => { FillFanMenu(); RefreshExtras(); }, null); }
            finally { Interlocked.Exchange(ref refreshing, 0); }
        });
    }
    // NotifyIcon's own (private) ShowContextMenu positions the menu and makes it close on an outside click.
    static void ShowFanMenu()
    {
        try
        {
            tray.ContextMenuStrip = fanMenu;
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(tray, null);
        }
        catch (Exception ex) { tray.ContextMenuStrip = menu; Log("fan menu: " + ex.Message); }
    }

    // Bridge for fanatic-bar (non-elevated, cannot reach the EC): it writes the wanted backlight level (0..3) into
    // <root>\fanatic-bar\kbd-request.txt; we apply it and keep kbd-state.txt there current (read on every poll).
    // The bar is a sibling folder of ours (<root>\fan-tray\ next to <root>\fanatic-bar\), so no drive letter is assumed.
    static readonly string BarDir = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')), "fanatic-bar");
    static FileSystemWatcher kbdWatch; static int kbdShown = -2;
    static void StartKbdBridge()
    {
        try
        {
            if (!Directory.Exists(BarDir)) return;
            kbdWatch = new FileSystemWatcher(BarDir, "kbd-request.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            // one worker takes the requests in order: a burst of watcher events never runs two readers at once
            FileSystemEventHandler h = (s, e) => KbdKick();
            kbdWatch.Changed += h; kbdWatch.Created += h; kbdWatch.Renamed += (s, e) => KbdKick(); kbdWatch.EnableRaisingEvents = true;
            KbdKick();
        }
        catch (Exception ex) { Log("kbd bridge: " + ex.Message); }
    }
    static int kbdPending, kbdRunning;
    static void KbdKick()
    {
        Interlocked.Exchange(ref kbdPending, 1);
        if (Interlocked.Exchange(ref kbdRunning, 1) == 1) return;   // the running worker sees the pending flag
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { while (Interlocked.Exchange(ref kbdPending, 0) == 1) KbdRequest(); }
            finally
            {
                Interlocked.Exchange(ref kbdRunning, 0);
                if (Interlocked.CompareExchange(ref kbdPending, 0, 0) == 1) KbdKick();   // a request between the loop and the reset
            }
        });
    }
    // The bar writes kbd-request.tmp and renames it to kbd-request.txt (atomic: never a half-written file). We take the file by
    // renaming it away first, so a newer request written meanwhile is a new file and is not lost.
    static void KbdRequest()
    {
        string p = Path.Combine(BarDir, "kbd-request.txt"), taken = Path.Combine(BarDir, "kbd-request.taken"); int level;
        for (int i = 0; i < 5; i++)   // an older bar may still hold the file it writes directly
            try
            {
                if (!File.Exists(p)) return;
                if (File.Exists(taken)) File.Delete(taken);
                File.Move(p, taken);
                string txt = File.ReadAllText(taken).Trim(); File.Delete(taken);
                if (int.TryParse(txt, out level) && level >= 0 && level <= 3) { SetBacklightNow(level); Log("kbd request " + level); }
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (Exception ex) { Log("kbd request: " + ex.Message); return; }
    }
    static void PublishKbd(int d3)
    {
        int level = d3 < 0 ? -1 : (d3 & 0x80) != 0 ? d3 & 3 : 0;
        if (level == kbdShown) return; kbdShown = level;
        try { if (Directory.Exists(BarDir)) File.WriteAllText(Path.Combine(BarDir, "kbd-state.txt"), level.ToString()); } catch (Exception ex) { Log("kbd state: " + ex.Message); }
    }

    // UI thread: keyboard backlight (0xD3) and Cooler Boost (0x98) from the last read (menu opening re-reads them in the background).
    static void RefreshExtras()
    {
        int d3 = lastD3, b98 = lastB98;
        bool ok = d3 >= 0 && (d3 & 0x80) != 0;
        kbdRoot.Enabled = ok;
        kbdRoot.Text = ok ? Strings.T("power.kbd_backlight.title") : Strings.T("fan.kbd.unavailable");
        for (int i = 0; i < 4; i++) kbdItems[i].Checked = ok && (d3 & 3) == i;
        boostItem.Enabled = b98 >= 0;
        boostItem.Checked = b98 >= 0 && (b98 & 0x80) != 0;
    }

    static void SetBacklight(int level) { ThreadPool.QueueUserWorkItem(_ => SetBacklightNow(level)); }
    // Any thread; the bridge worker calls it directly, so requests are applied in the order they came.
    static void SetBacklightNow(int level)
    {
        bool ok = false;
        lock (ecLock)
        {
            int v = EcRead(0xD3);   // keep the other bits, change only enable (bit 7) + level (bits 0-1)
            if (v >= 0) ok = EcWrite(0xD3, (byte)((v & 0x7C) | 0x80 | (level & 3)));
            if (ok) lastD3 = EcRead(0xD3);
        }
        string t = ok ? Strings.T("fan.kbd.osd", level == 0 ? Strings.T("fan.word.off") : level.ToString()) : Strings.T("fan.kbd.error");
        ui.Post(__ => ShowOsd(t), null);
    }

    static void ToggleBoost()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok = false, on = false;
            lock (ecLock)
            {
                int v = EcRead(0x98);
                if (v >= 0) { byte nv = (byte)(v ^ 0x80); ok = EcWrite(0x98, nv); on = (nv & 0x80) != 0; if (ok) lastB98 = nv; }
            }
            string t = ok ? Strings.T("fan.boost.osd", on ? Strings.T("fan.word.on") : Strings.T("fan.word.off")) : Strings.T("fan.boost.error");
            ui.Post(__ => ShowOsd(t), null);
        });
    }

    static int EcRead(byte addr)
    {
        if (acpi == null) acpi = FindAcpi();   // WMI may not be ready right after logon
        if (acpi == null) return -1;
        try
        {
            if (pkgClass == null) pkgClass = new ManagementClass(@"root\wmi", "Package_32", null);
            using (var pkg = pkgClass.CreateInstance())
            using (var inp = acpi.GetMethodParameters("Get_Data"))
            {
                var b = new byte[32]; b[0] = addr; pkg["Bytes"] = b; inp["Data"] = pkg;
                using (var outp = acpi.InvokeMethod("Get_Data", inp, null))
                using (var data = (ManagementBaseObject)outp["Data"])
                {
                    var r = (byte[])data["Bytes"];
                    return r[0] == 1 ? r[1] : -1;
                }
            }
        }
        catch { ResetWmi(); return -1; }   // re-acquire on the next read
    }

    static string ActiveMode()
    {
        try { var m = Regex.Match(File.ReadAllText(ProfileFile), "\"activeMode\"\\s*:\\s*\"(\\w+)\""); return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "?"; } catch { return "?"; }
    }

    static void Switch(int idx)
    {
        tray.Text = Strings.T("fan.switching", ModeName(idx));
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // The OSD reports what schtasks said, not what was asked for.
            string t = RunModeTask(idx) == 0 ? OsdNames[idx] : Strings.T("fan.osd.error", OsdNames[idx]);
            ui.Post(__ => ShowOsd(t), null);
        });
    }

    static volatile bool locked;
    static void SetPollRate()
    {
        var t = pollTimer; if (t == null) return;
        bool battery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        if (locked) t.Change(Timeout.Infinite, Timeout.Infinite);
        else t.Change(0, battery ? 15000 : 3000);
    }

    // ---- brightness on hover: NotifyIcon has no "mouse left", so the pointer is checked against the icon's rectangle ----
    const float DimLevel = 0.78f;
    static bool hot, lastHot; static int lastTempShown = -1; static string lastFullText = "Fan";
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    static System.Reflection.FieldInfo trayWindowField, trayIdField;
    static bool PointerOnIcon()
    {
        try
        {
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            if (trayWindowField == null) { trayWindowField = typeof(NotifyIcon).GetField("window", bf); trayIdField = typeof(NotifyIcon).GetField("id", bf); }
            var nid = new NOTIFYICONIDENTIFIER { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = ((NativeWindow)trayWindowField.GetValue(tray)).Handle, uID = (uint)(int)trayIdField.GetValue(tray) };
            RECT r; if (Shell_NotifyIconGetRect(ref nid, out r) != 0) return false;
            var c = Cursor.Position; return c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;
        }
        catch { return false; }
    }

    // Timer thread: do the EC reads here, post only the UI update.
    static void Poll()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            int t = Ec(0x68), hi = Ec(0xC8), lo = Ec(0xC9);
            int d3 = Ec(0xD3); lastD3 = d3; lastB98 = Ec(0x98);
            PublishKbd(d3);
            int rpm = 0;
            if (hi >= 0 && lo >= 0) { int raw = hi * 256 + lo; if (raw > 0) rpm = 478000 / raw; }
            lastRpm = hi >= 0 && lo >= 0 ? rpm : -1;
            string text = t < 0 ? Strings.T("fan.tip.no_data") : "CPU " + t + " °C · " + (rpm > 0 ? Strings.T("fan.rpm_short", rpm) : Strings.T("fan.stopped")) + " · " + UaMode(ActiveMode());   // 0x71 is the EC curve value (0-150), not a percentage: show rpm only
            ui.Post(_ => { try { Apply(t, text); } catch (Exception ex) { Log("apply: " + ex.Message); } }, null);
        }
        catch (Exception ex) { Log("poll: " + ex.Message); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    // UI thread.
    static void Apply(int t, string text)
    {
        lastText = text.Length > 63 ? text.Substring(0, 63) : text;   // NotifyIcon.Text throws above 63 chars
        if (!menuOpen) tray.Text = lastText;
        lastTempShown = t; lastFullText = text;
        int sz = TrayIconSize();   // a display-scale change redraws at the new size
        if (t != lastTemp || sz != lastSize || hot != lastHot) { var ic = MakeIcon(t); tray.Icon = ic; if (lastIcon != null) lastIcon.Dispose(); lastIcon = ic; lastTemp = t; lastSize = sz; lastHot = hot; }
    }

    // Two-digit temperature drawn as large as the tray slot allows; colour: white <70, yellow 70-84, red 85+.
    static Icon MakeIcon(int temp)
    {
        int size = TrayIconSize();
        string text = temp < 0 ? "--" : temp.ToString();
        // White when normal (the layout indicator next to it is coloured), yellow when warm, red when hot.
        Color col = temp >= 85 ? Color.FromArgb(255, 90, 90) : temp >= 70 ? Color.FromArgb(255, 210, 80) : Color.White;
        if (!hot) col = Color.FromArgb((int)(col.R * DimLevel), (int)(col.G * DimLevel), (int)(col.B * DimLevel));
        if (iconFamily == null) iconFamily = MakeFamily("Bahnschrift SemiBold Condensed");
        using (var bmp = new Bitmap(size, size))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // A tiny degree ring in the top-right corner; the digits get the rest of the square (same tray slot).
            float ring = Math.Max(4.5f, size * 0.28f);
            float avail = size - ring + 1;
            var fam = iconFamily; float em = size * 1.2f; SizeF m;
            using (var sf = StringFormat.GenericTypographic)
            {
                while (true) { using (var f = new Font(fam, em, FontStyle.Regular, GraphicsUnit.Pixel)) m = g.MeasureString(text, f, 1000, sf); if ((m.Width <= avail && m.Height <= size * 1.15f) || em < 6) break; em -= 0.5f; }
                // Text as a path, filled and outlined in the same colour: noticeably bolder than plain DrawString.
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Winding))   // variable font: overlapping contours
                using (var b = new SolidBrush(col))
                using (var pen = new Pen(col, Math.Max(1f, size / 18f)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                using (var ringPen = new Pen(col, Math.Max(1f, size / 16f)))
                {
                    path.AddString(text, fam, (int)FontStyle.Regular, em, new PointF((avail - m.Width) / 2f, (size - m.Height) / 2f), sf);
                    g.FillPath(b, path); g.DrawPath(pen, path);
                    if (temp >= 0) g.DrawEllipse(ringPen, size - ring + 0.5f, 0.5f, ring - 1.5f, ring - 1.5f);
                }
            }
            IntPtr h = bmp.GetHicon(); var icon = (Icon)Icon.FromHandle(h).Clone(); DestroyIcon(h); return icon;
        }
    }
}

// Dark context menu matching the Windows dark theme.
sealed class DarkMenuColors : ProfessionalColorTable
{
    // .sky-menu palette of fanatic-bar (popmenu.py): black, border #1e262c, hover plate #12171a with a light (#48daf9) border
    static readonly Color Bg = Color.Black, Hover = ColorTranslator.FromHtml("#12171a"), Line = ColorTranslator.FromHtml("#1e262c"), Edge = ColorTranslator.FromHtml("#48daf9");
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuBorder { get { return Line; } }
    public override Color MenuItemBorder { get { return Edge; } }
    public override Color MenuItemSelected { get { return Hover; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
    public override Color SeparatorDark { get { return Line; } }
    public override Color SeparatorLight { get { return Bg; } }
    public override Color CheckBackground { get { return Hover; } }
    public override Color CheckSelectedBackground { get { return Hover; } }
    public override Color CheckPressedBackground { get { return Hover; } }
}
sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }
    static Font menuFont;
    protected override void Initialize(ToolStrip ts) { base.Initialize(ts); if (menuFont == null) menuFont = new Font("Consolas", 11f); ts.Font = menuFont; }   // Consolas as the .sky-menu
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? ColorTranslator.FromHtml("#dfe8ee") : ColorTranslator.FromHtml("#5b6b76"); base.OnRenderItemText(e); }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = e.Item.Enabled ? ColorTranslator.FromHtml("#dfe8ee") : ColorTranslator.FromHtml("#5b6b76"); base.OnRenderArrow(e); }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle; using (var p = new Pen(ColorTranslator.FromHtml("#48daf9"), 2f))
        { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; e.Graphics.DrawLines(p, new[] { new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f), new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f) }); }
    }
}