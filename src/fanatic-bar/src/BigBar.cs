// Big indicators (owner, bigind-TASK.md): Wi-Fi + ping, battery, sound, keyboard layout, date and clock drawn the full height of the
// taskbar in the whitebook style, including CPU / RAM / GPU, instead of separate tray buttons.
// Technique: invisible "spacer" tray icons at the right end of our block reserve the room - Explorer lays them out itself, so
// the room follows the taskbar, DPI and resolution - and a child window of Shell_TrayWnd is kept exactly over them.
// fanatic-bar.json { "bigIndicators": false } goes back to the tray icons. The native clock is hidden (HideClock policy) while on.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static partial class FanaticBar
{
    // ---- shared click actions (tray icons and big indicators): left = quick action / flyout, right = labelled menu ----
    static void SyncTime() { ThreadPool.QueueUserWorkItem(_ => { try { Exec("schtasks.exe", "/Run /TN fanatic-time-sync"); } catch (Exception ex) { Log("sync: " + ex.Message); } }); }
    static void NetClick(bool right, Rectangle box)
    {
        if (!right) { Open("ms-availablenetworks:"); return; }
        MenuAt(box, Item(Strings.T("menu.net.wifi"), "", () => Open("ms-availablenetworks:")),
            Item(Strings.T("menu.net.settings"), "", () => Open("ms-settings:network-status")),
            Item(Strings.T("menu.net.connections"), "", () => Run("control.exe", "ncpa.cpl")));
    }
    static void TaskMgrClick(bool right, Rectangle box)
    {
        if (!right) { Run("taskmgr.exe", ""); return; }
        MenuAt(box, Item(Strings.T("menu.sys.task_manager"), "", () => Run("taskmgr.exe", "")), Item(Strings.T("menu.sys.resource_monitor"), "", () => Run("resmon.exe", "")));
    }
    static void WeatherClick(bool right, Rectangle box)
    {
        if (!right) { SkyPopup.ToggleAt(box, () => new WeatherPopup()); return; }
        MenuAt(box, Item(Strings.T("menu.weather.forecast"), "", () => FlyAt(box, () => new WeatherPopup())),
            Item(Strings.T("menu.weather.refresh"), "", () => { RefreshWeatherSoon(); }));
    }
    static void PowerClick(bool right, Rectangle box)
    {
        if (!right) { SkyPopup.ToggleAt(box, () => new PowerPopup()); return; }
        MenuAt(box, Item(Strings.T("menu.power.popup"), "", () => FlyAt(box, () => new PowerPopup())),
            Item(Strings.T("menu.power.settings"), "", () => Open("ms-settings:powersleep")),
            Item(Strings.T("menu.power.saver"), "", () => Open("ms-settings:batterysaver")));
    }
    static void VolumeAtCursor() { var c = Cursor.Position; Run(System.IO.Path.Combine(Environment.SystemDirectory, "SndVol.exe"), "-f " + ((c.Y << 16) | (c.X & 0xFFFF))); }
    static void SoundClick(bool right, Rectangle box)
    {
        if (!right) { SkyPopup.ToggleAt(box, () => new AudioPopup()); return; }
        MenuAt(box, Item(Strings.T("menu.audio.popup"), "", () => FlyAt(box, () => new AudioPopup())),
            Item(Strings.T("menu.audio.windows_slider"), "", VolumeAtCursor),
            Item(Strings.T("menu.audio.mixer"), "", () => Run(System.IO.Path.Combine(Environment.SystemDirectory, "SndVol.exe"), "")),
            Item(Strings.T("menu.audio.playback_devices"), "", () => Run("control.exe", "mmsys.cpl")),
            Item(Strings.T("menu.audio.settings"), "", () => Open("ms-settings:sound")));
    }
    static void ClockClick(bool right, Rectangle box)
    {
        if (!right) { SkyPopup.ToggleAt(box, () => new ClockPopup()); return; }
        MenuAt(box, Item(Strings.T("menu.clock.popup"), "", () => FlyAt(box, () => new ClockPopup())),
            Item(Strings.T("menu.clock.sync"), "", SyncTime),
            Item(Strings.T("menu.clock.datetime_settings"), "", () => Open("ms-settings:dateandtime")));
    }
    static void CalClick(bool right, Rectangle box)
    {
        if (!right) { SkyPopup.ToggleAt(box, () => new CalendarPopup()); return; }
        MenuAt(box, Item(Strings.T("menu.calendar.popup"), "", () => FlyAt(box, () => new CalendarPopup())),
            Item(Strings.T("menu.calendar.datetime_settings"), "", () => Open("ms-settings:dateandtime")),
            Item(Strings.T("menu.calendar.region"), "", () => Open("ms-settings:regionformatting")));
    }

    // ---- keyboard layout of the foreground window (keyboard-switch keeps the hotkeys; its tray icon hides in big mode) ----
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadKeyboardLayout(string klid, uint flags);
    static readonly string[] LangCodes = { "UA", "RU", "EN" }, LangNames = { "Українська", "Русский", "English" }, LangKlids = { "00000422", "00000419", "00000409" };
    static readonly ushort[] LangIds = { 0x0422, 0x0419, 0x0409 };
    static readonly Color[] LangColours = { Hex("#ffd447"), Hex("#8f5b2e"), Hex("#3b7ddd") };   // theme.json "layouts"
    static int LayoutIndex(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return -1; uint pid; uint th = GetWindowThreadProcessId(fg, out pid);
        ushort lang = (ushort)((long)GetKeyboardLayout(th) & 0xFFFF);
        return Array.IndexOf(LangIds, lang);
    }
    // A window the user types in: not the taskbar / tray / desktop and not one of ours (like keyboard-switch's RememberApp).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    static bool IsAppWindow(IntPtr h)
    {
        uint pid; GetWindowThreadProcessId(h, out pid); if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;
        var c = new System.Text.StringBuilder(64); GetClassName(h, c, 64); string n = c.ToString();
        return n != "Shell_TrayWnd" && n != "Shell_SecondaryTrayWnd" && n != "NotifyIconOverflowWindow" && n != "TopLevelWindowForOverflowXamlIsland"
            && n != "Progman" && n != "WorkerW";
    }
    // Give the focus back to the app window (the click made the taskbar active) and switch ITS layout.
    static void SwitchLayout(int idx, IntPtr target)
    {
        IntPtr hkl = LoadKeyboardLayout(LangKlids[idx], 0);
        if (target == IntPtr.Zero || !IsWindow(target)) return;
        SetForegroundWindow(target);
        PostMessage(target, 0x0050 /*WM_INPUTLANGCHANGEREQUEST*/, IntPtr.Zero, hkl);
    }
    // Left click: the next layout in a full cycle UA -> RU -> EN -> UA (owner 26.10). Right click: the menu.
    static void LangClick(bool right, Rectangle box, IntPtr target)
    {
        if (!right)
        {
            if (RequestLayoutCycle()) return;
            int cur = LayoutIndex(target);
            SwitchLayout((cur < 0 ? 0 : cur + 1) % LangKlids.Length, target);
            return;
        }
        var items = new List<MenuItem>();
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            items.Add(Item(LangNames[i] + "  " + LangCodes[i], "", () => SwitchLayout(idx, target)));   // no glyph (owner 26.10)
        }
        items.Add(Item(Strings.T("menu.lang.settings"), "", () => Open("ms-settings:regionlanguage")));
        MenuAt(box, items.ToArray());
    }

    // ---- state for the big window, kept by the polls ----
    static volatile int bigWifiLit = -1, bigBatShown = -1, bigVol = -1; static volatile bool bigPlugged, bigMuted;
    static volatile int bigCpuPct, bigRamPct, bigGpuPct = -1;
    static bool bigMode;
    static BigBar big;
    static readonly List<Slot> spacers = new List<Slot>();
    static Mutex bigModeMutex;   // keyboard-switch hides its own tray icon while this exists
    static void BigChanged() { var b = big; if (b != null) ui.Post(_ => { if (!b.IsDisposed) b.Invalidate(); }, null); }

    static void StartBigMode()
    {
        bool created; bigModeMutex = new Mutex(false, "fanatic.Bar.BigMode", out created);
        foreach (var s in new[] { cpu, ram, gpu, wSym, wTemp, wifi, ping, bat, batPct, audio, day, week, hours, minutes }) s.Ni.Visible = false;
        SetHideClock(true);
        big = new BigBar();
        int height = 60; RECT tr;
        var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out tr)) height = tr.Bottom - tr.Top;
        big.Size = new Size(1800, height);
        int initial = ReadSpacerCount();
        SetSpacerCount(initial > 0 ? initial : big.SpacerCount(48));
        // Create a hidden handle; Place shows it only when the reserved room can hold the content.
        var handle = big.Handle;
    }
    // Hide / show Explorer's own clock (policy value; Explorer applies it on the policy-change broadcast).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, string l, uint f, uint t, out IntPtr r);
    static void SetHideClock(bool hide)
    {
        try
        {
            object cur;   // read first: the policy key is read-only for a non-elevated process; HideClock itself was set once, elevated
            using (var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer")) cur = rk != null ? rk.GetValue("HideClock") : null;
            bool hidden = cur is int && (int)cur == 1;
            if (hide == hidden) return;
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
            {
                if (hide) k.SetValue("HideClock", 1, RegistryValueKind.DWord); else k.DeleteValue("HideClock", false);
            }
            IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "Policy", 2, 3000, out r);
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "TraySettings", 2, 3000, out r);
            Log("native clock " + (hide ? "hidden" : "shown") + " (HideClock)");
        }
        catch (Exception ex) { Log("hide clock: " + ex.Message); }
    }

    static Icon blankIcon;
    static void AddSpacer()
    {
        if (blankIcon == null) using (var b = new Bitmap(IconSize(), IconSize())) blankIcon = ToIcon(b);
        var ni = new NotifyIcon { Icon = blankIcon, Text = "" };
        typeof(NotifyIcon).GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(ni, 7201 + spacers.Count);
        ni.Visible = true; spacers.Add(new Slot { Ni = ni });
    }
    static void SetSpacerCount(int n)
    {
        n = Math.Max(1, Math.Min(MaxSpacers, n));
        bool added = spacers.Count < n;
        while (spacers.Count < n) AddSpacer();
        if (added) RequestTrayPromotion();
        while (spacers.Count > n && spacers.Count > 1) { var s = spacers[spacers.Count - 1]; s.Ni.Visible = false; s.Ni.Dispose(); spacers.RemoveAt(spacers.Count - 1); }
        ui.Post(_ => ArrangeTray(), null);
    }
    // Use the laid-out spacers even if others are still in overflow. Never use IconRect's pointer fallback: it once made
    // the computed button width nonsense and the bar added spacers without end (353 tray buttons).
    static Rectangle SpacerRect(out int buttonWidth)
    {
        IntPtr tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        RECT tr = new RECT(); bool haveTray = tray != IntPtr.Zero && GetWindowRect(tray, out tr);
        // Overflow icons may have valid rectangles too: only accept rectangles inside the taskbar row.
        var rects = new List<Rectangle>(); foreach (var s in spacers) rects.Add(ShellIconRect(s.Ni));
        bool missing;
        var all = SpacerRoom(rects, haveTray ? Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom) : Rectangle.Empty, out buttonWidth, out missing);
        if (missing)
        {
            if (missingSpacersSince == DateTime.MinValue) missingSpacersSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - missingSpacersSince).TotalSeconds >= 2) RequestTrayPromotion();
        }
        else missingSpacersSince = DateTime.MinValue;
        return all;
    }
    static Rectangle SpacerRoom(List<Rectangle> rects, Rectangle tray, out int buttonWidth, out bool missing)
    {
        var valid = rects.FindAll(r => !tray.IsEmpty && !r.IsEmpty && r.Width >= 8 && r.Width <= 200 && r.Height <= 200 && r.Top >= tray.Top && r.Bottom <= tray.Bottom);
        valid.Sort((a, b) => a.Left.CompareTo(b.Left));
        Rectangle best = Rectangle.Empty, run = Rectangle.Empty; int bestCount = 0, count = 0;
        foreach (var r in valid)
        {
            if (!run.IsEmpty && r.Left == run.Right && r.Top == run.Top && r.Height == run.Height) { run = Rectangle.Union(run, r); count++; }
            else { run = r; count = 1; }
            if (count > bestCount) { best = run; bestCount = count; }
        }
        missing = bestCount != rects.Count;
        buttonWidth = bestCount > 0 ? best.Width / bestCount : 0;
        return best;
    }
    static DateTime missingSpacersSince;
    static string BarStatePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fanatic", "bar-state.json"); } }
    static int ReadSpacerCount(string path = null)
    {
        try
        {
            var state = new JavaScriptSerializer().Deserialize<Dictionary<string, int>>(File.ReadAllText(path ?? BarStatePath));
            int n; return state.TryGetValue("spacers", out n) && n >= 1 && n <= MaxSpacers ? n : 0;
        }
        catch { return 0; }
    }
    static bool SaveSpacerCount(int n, string path = null)
    {
        try
        {
            path = path ?? BarStatePath; string tmp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tmp, "{\"spacers\":" + n + "}");
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            return true;
        }
        catch (Exception ex) { Log("bar state: " + ex.Message); return false; }
    }
    static readonly System.Reflection.FieldInfo niWindow = typeof(NotifyIcon).GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
        niId = typeof(NotifyIcon).GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    static Rectangle ShellIconRect(NotifyIcon ni)
    {
        try
        {
            var win = (NativeWindow)niWindow.GetValue(ni);
            var nid = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = win.Handle, uID = (uint)(int)niId.GetValue(ni) };
            RECT r; if (Shell_NotifyIconGetRect(ref nid, out r) == 0 && r.Right > r.Left) return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch { }
        return Rectangle.Empty;
    }
    const int MaxSpacers = 30;
    static int FitSpacerCount(int need, int minimum, int bw)
    {
        int n = Math.Max(1, need / bw);
        if (n * bw < minimum) n++;
        return Math.Min(MaxSpacers, n);
    }

    delegate bool ChildVisitor(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, ChildVisitor visit, IntPtr data);
    static Rectangle TaskButtonRect()
    {
        var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        var band = FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null); RECT r;
        return tray != IntPtr.Zero && band != IntPtr.Zero && GetWindowRect(band, out r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
    }
    static int AvailableBarWidth(IntPtr tray, RECT tr, int room)
    {
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null); RECT nr;
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out nr)) return room;
        int right = nr.Right;
        EnumChildWindows(tray, (h, data) =>
        {
            var name = new System.Text.StringBuilder(64); GetClassName(h, name, name.Capacity);
            if (name.ToString() == "TrayShowDesktopButtonWClass" || name.ToString() == "TrayButton")
            { RECT r; if (GetWindowRect(h, out r)) right = Math.Max(right, r.Right); }
            return true;
        }, IntPtr.Zero);
        var band = TaskButtonRect();
        return BarWidthBudget(room, tr.Right, right, nr.Left, band.IsEmpty ? nr.Left : band.Right);
    }
    static int BarWidthBudget(int room, int taskbarRight, int tailRight, int notifyLeft, int taskBandRight)
    { return Math.Max(0, room + taskbarRight - tailRight + Math.Max(0, notifyLeft - taskBandRight)); }

    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT r, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    // ---- tooltip of the big indicators: monospace like the whitebook panel's, on the .sky-menu colours ----
    static Font tipFont;
    static Font TipFont() { return tipFont ?? (tipFont = new Font(Family("Consolas"), 10f, FontStyle.Regular, GraphicsUnit.Point)); }
    const TextFormatFlags TipFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    // room for a few more characters than the text needs: the tooltip keeps its size while the numbers in it change every second
    static Size TipSize(string text)
    {
        var t = TextRenderer.MeasureText(text, TipFont(), Size.Empty, TipFlags); int pad = Px(8);
        return new Size(t.Width + TextRenderer.MeasureText("0000", TipFont(), Size.Empty, TipFlags).Width + 2 * pad, t.Height + 2 * pad);
    }
    static void DrawTip(Graphics g, Rectangle r, string text)
    {
        using (var b = new SolidBrush(Background)) g.FillRectangle(b, r);
        using (var p = new Pen(SkyBorder)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        int pad = Px(8); TextRenderer.DrawText(g, text, TipFont(), new Point(r.X + pad, r.Y + pad), Foreground, TipFlags);
    }

    // Tooltip of the big indicators. A WinForms ToolTip hides and re-shows when its text is replaced while it is visible,
    // and the Wi-Fi / ping text changes every second -> a visible blink on every refresh (owner, 25.10). This window keeps
    // one surface and repaints the text in place (no blink), and carries the terminal's translucency. Non-activating,
    // topmost, click-through, double-buffered.
    sealed class TipWindow : Form
    {
        string text = "";
        public TipWindow()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Background;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x20 /*TRANSPARENT: never eats a click*/; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Glass(Handle); }
        // Replace the text and keep the box above the anchor (below it if there is no room); repaint in place, not hide / show.
        public void Update(string t, Rectangle anchor)
        {
            var sz = TipSize(t);
            if (Visible)
            {
                // Keep the top-left fixed and only grow (reset on Hide): changing numbers / units can never move or
                // shrink the box, so it neither blinks nor jitters while the pointer rests on it.
                int w = Math.Max(Width, sz.Width), h = Math.Max(Height, sz.Height); bool changed = t != text; text = t;
                bool grow = w != Width || h != Height; if (grow) Size = new Size(w, h); if (grow || changed) Invalidate();   // a Form does not repaint on resize (ResizeRedraw off): the old border / text would stay
                return;
            }
            var wa = Screen.FromRectangle(anchor).WorkingArea; int gap = Px(6);
            int x = Math.Max(wa.Left + gap, Math.Min(anchor.Left + anchor.Width / 2 - sz.Width / 2, wa.Right - sz.Width - gap));
            int y = anchor.Top - sz.Height - gap; if (y < wa.Top) y = anchor.Bottom + gap;
            text = t; Bounds = new Rectangle(x, y, sz.Width, sz.Height); Show();
        }
        protected override void OnPaint(PaintEventArgs e) { DrawTip(e.Graphics, new Rectangle(0, 0, Width, Height), text); }
    }

    // The full-height indicator strip. Drawn in "panel units" u = taskbar height / 66 (the whitebook panel is 66 px, its pictures 44).
    sealed class BigBar : Form
    {
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 250 };
        int shownSecond = -1, layout = -2, placeTicks; IntPtr lastFg;
        readonly List<KeyValuePair<Rectangle, Action<bool, Rectangle>>> zones = new List<KeyValuePair<Rectangle, Action<bool, Rectangle>>>();
        int needWidth, minimumWidth;
        float naturalGaps, minimumGaps;
        int dropLevel, savedCount, stableCount;
        DateTime stableSince, restoreSince;
        int lastBudget = -1;
        Rectangle taskButtons;
        // the zone under the pointer (-1: none); the Wi-Fi / ping zone shows the network tooltip, refreshed every second
        int hot = -1;
        TipWindow tipWin;

        readonly bool preview;
        public BigBar(bool preview = false)
        {
            this.preview = preview;
            if (preview) layout = 2;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Bounds = new Rectangle(-2000, -2000, 10, 10);
            tick.Tick += (s, e) => Tick(); if (!preview) tick.Start();
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void Dispose(bool d) { if (d) { tick.Dispose(); if (tipWin != null) tipWin.Dispose(); } base.Dispose(d); }

        // 250 ms normally (the colon follows the second closely), 500 ms on battery, 0 = stopped (idle)
        public void SetRate(int ms)
        {
            if (ms <= 0) { tick.Stop(); return; }
            tick.Interval = ms; if (!tick.Enabled) { tick.Start(); Invalidate(); }
        }
        string HotTip()
        {
            if (hot < 0 || hot >= zones.Count) return null;
            var action = zones[hot].Value;
            if (action == (Action<bool, Rectangle>)NetClick) return NetTip();
            if (action != (Action<bool, Rectangle>)TaskMgrClick) return null;
            if (hot == 0) return cpu != null && cpu.Tip.Length > 0 ? cpu.Tip : Strings.T("tip.cpu", bigCpuPct);
            if (hot == 1 && dropLevel < 3) return ram != null && ram.Tip.Length > 0 ? ram.Tip : Strings.T("slot.memory");
            return gpu != null && gpu.Tip.Length > 0 ? gpu.Tip : Strings.T("tip.gpu", bigGpuPct < 0 ? "--" : bigGpuPct.ToString());
        }
        void UpdateTip()
        {
            string t = HotTip();
            if (t == null) { if (tipWin != null) tipWin.Hide(); return; }
            if (tipWin == null || tipWin.IsDisposed) tipWin = new TipWindow();
            tipWin.Update(t, RectangleToScreen(zones[hot].Key));
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = zones.FindIndex(z => z.Key.Contains(e.Location));
            if (h != hot) { hot = h; UpdateTip(); Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hot = -1; UpdateTip(); Invalidate(); base.OnMouseLeave(e); }

        public void LayoutSignal() { Tick(); Invalidate(); }
        void Tick()
        {
            // keep the layout, the second (colon blink) and the place current; the other readings invalidate through BigChanged()
            var fg = GetForegroundWindow();
            if (fg != Handle && fg != IntPtr.Zero && IsAppWindow(fg)) lastFg = fg;   // the taskbar itself never counts (click on us = taskbar active)
            // the foreground thread's layout; consoles / terminals often report none: then our own thread's (one layout for all apps)
            int l = LayoutIndex(lastFg); if (l < 0) l = Array.IndexOf(LangIds, (ushort)((long)GetKeyboardLayout(0) & 0xFFFF));
            if (l != layout && l >= 0) { layout = l; Invalidate(); }
            int sec = DateTime.Now.Second; if (sec != shownSecond) { shownSecond = sec; Invalidate(); if (hot >= 0) UpdateTip(); }
            // Check shell geometry every 2 s, on task-band changes, and every tick until the room is ready.
            Rectangle band = TaskButtonRect();
            bool taskChanged = band != taskButtons; taskButtons = band;
            if (++placeTicks >= 8 || !Visible || taskChanged) { placeTicks = 0; Place(); }
        }
        // Over the spacers, the full height of the taskbar, as a child of Shell_TrayWnd (re-attached after an Explorer restart).
        void Place()
        {
            if (preview) return;
            IntPtr tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); if (tray == IntPtr.Zero) return;
            if (GetParent(Handle) != tray)
            {
                SetWindowLong(Handle, -16, (GetWindowLong(Handle, -16) & ~unchecked((int)0x80000000)) | 0x40000000 /*WS_CHILD*/);
                SetParent(Handle, tray); Log("big indicators: attached to the taskbar");
            }
            int bw; var sr = SpacerRect(out bw); RECT tr; if (sr.IsEmpty || bw < 8 || !GetWindowRect(tray, out tr)) return;   // not laid out yet: next tick
            int height = tr.Bottom - tr.Top;
            if (Height != height) Height = height;
            bool complete = missingSpacersSince == DateTime.MinValue && sr.Width == spacers.Count * bw;
            int want = SpacerCount(bw);
            if (complete)
            {
                int budget = AvailableBarWidth(tray, tr, spacers.Count * bw);
                want = ApplyBudget(budget, bw, DateTime.UtcNow);
                if (want != spacers.Count)
                {
                    stableSince = DateTime.UtcNow; SetSpacerCount(want); return;
                }
            }
            Measure();
            // A partial set can be used once it holds the minimum content. Never flash a single-button frame.
            if (sr.Width < minimumWidth) return;
            var r = new RECT { Left = sr.Left, Top = tr.Top, Right = sr.Right, Bottom = tr.Bottom };
            MapWindowPoints(IntPtr.Zero, tray, ref r, 2);
            int x = r.Left, y = r.Top, w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (Left != x || Top != y || Width != w || Height != h || !Visible)
            {
                Bounds = new Rectangle(x, y, w, h); Show();
                SetWindowPos(Handle, IntPtr.Zero /*HWND_TOP*/, x, y, w, h, 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOW*/);
                hot = -1; UpdateTip(); Invalidate();
            }
            if (complete)
            {
                if (stableCount != spacers.Count) { stableCount = spacers.Count; stableSince = DateTime.UtcNow; }
                if (savedCount != stableCount && (DateTime.UtcNow - stableSince).TotalSeconds >= 4)
                { if (SaveSpacerCount(stableCount)) savedCount = stableCount; }
            }
        }

        // Shrink immediately, restore only after eight seconds with a spare button of headroom.
        // All candidates use identical measurement and drawing code, including the two-pixel gap limit.
        public int ApplyBudget(int budget, int bw, DateTime now)
        {
            int original = dropLevel, current = SpacerCount(bw);
            int limit = Math.Max(1, budget / bw);
            if (current > limit)
            {
                while (dropLevel < 3 && current > limit) { dropLevel++; current = SpacerCount(bw); }
                restoreSince = DateTime.MinValue;
            }
            else if (dropLevel > 0)
            {
                int old = dropLevel; dropLevel--;
                int larger = SpacerCount(bw); dropLevel = old;
                if ((larger + 1) * bw <= budget)
                {
                    if (restoreSince == DateTime.MinValue || (lastBudget >= 0 && budget < lastBudget - bw / 2)) restoreSince = now;
                    if ((now - restoreSince).TotalSeconds >= 8) { dropLevel--; current = larger; restoreSince = DateTime.MinValue; }
                }
                else restoreSince = DateTime.MinValue;
            }
            lastBudget = budget;
            if (original != dropLevel)
            {
                hot = -1; UpdateTip(); Invalidate();
                if (!preview) Log("bar budget: " + budget + " px, " + current + " spacers; dropped " +
                    (dropLevel == 0 ? "none" : dropLevel == 1 ? "weather picture" : dropLevel == 2 ? "weather picture, GPU" : "weather picture, GPU, RAM"));
            }
            Measure(); return current;
        }

        // ---- drawing ----
        // Two tabular panel-font cells, ink centred on cy, cap height `cap`; the width is fixed whatever the value
        // ("--" = a dash in the same room). Returns the width of the two cells.
        static float Digits(Graphics g, string two, float x, float cy, float cap, Color col)
        {
            float s = cap / TextFont.CAP, w = DiabloTabularWidth(2) * s;
            if (two.Trim() == "--") { using (var pen = new Pen(col, Math.Max(1.5f, cap / 7)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, x + w * 0.2f, cy, x + w * 0.8f, cy); return w; }
            using (var p = DiabloTabular(two.Trim(), 2)) using (var m = new Matrix(s, 0, 0, s, x, cy - cap / 2)) using (var b = new SolidBrush(col)) { p.Transform(m); g.FillPath(b, p); }
            return w;
        }
        // Day of the month: 1..9 without the leading zero, but in the same two-cell room (its width comes from the font's digit
        // cell), the single digit centred in that room; 10..31 as two tabular cells. Returns the room width.
        static float DayDigits(Graphics g, int day, float x, float cy, float cap, Color col)
        {
            if (day >= 10) return Digits(g, day.ToString(), x, cy, cap, col);
            float s = cap / TextFont.CAP, room = DiabloTabularWidth(2) * s, one = DiabloTabularWidth(1) * s;
            using (var p = DiabloTabular(day.ToString(), 1)) using (var m = new Matrix(s, 0, 0, s, x + (room - one) / 2, cy - cap / 2)) using (var b = new SolidBrush(col)) { p.Transform(m); g.FillPath(b, p); }
            return room;
        }
        // Panel text in a slot of fixed width (the widest of `all`), centred in it: the month / weekday never move the neighbours.
        static float Slot(Graphics g, string s, string[] all, float x, float cy, float cap, Color col)
        {
            float slot = 0; foreach (var a in all) slot = Math.Max(slot, DiabloLine(null, a, 0, 0, cap, col));
            DiabloLine(g, s, x + (slot - DiabloLine(null, s, 0, 0, cap, col)) / 2, cy, cap, col);
            return slot;
        }
        void Zone(float x0, float x1, Action<bool, Rectangle> click) { zones.Add(new KeyValuePair<Rectangle, Action<bool, Rectangle>>(Rectangle.FromLTRB((int)Math.Round(x0), 0, (int)Math.Round(x1), Height), click)); }

        float Gap(float wanted, float scale)
        {
            wanted = Math.Max(2, wanted); naturalGaps += wanted; minimumGaps += 2;
            return 2 + (wanted - 2) * scale;
        }
        public void Measure()
        {
            // Measurement must never replace the fitted rectangles used by hover/click between repaints.
            var fitted = zones.ToArray();
            using (var bmp = new Bitmap(2400, Height)) using (var g = Graphics.FromImage(bmp))
            {
                float width = Render(g, 1);
                needWidth = (int)Math.Ceiling(width);
                minimumWidth = (int)Math.Ceiling(width - naturalGaps + minimumGaps);
            }
            zones.Clear(); zones.AddRange(fitted);
        }
        public int SpacerCount(int buttonWidth)
        {
            Measure();
            return FitSpacerCount(needWidth, minimumWidth, buttonWidth);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            Measure();
            float scale = naturalGaps > minimumGaps ? Math.Min(1, Math.Max(0, (Width - (needWidth - naturalGaps + minimumGaps)) / (naturalGaps - minimumGaps))) : 1;
            Render(e.Graphics, scale);
            using (var veil = new SolidBrush(Color.FromArgb((int)Math.Round(255 * (1 - DimLevel)), 0, 0, 0)))
                for (int i = 0; i < zones.Count; i++) if (i != hot) e.Graphics.FillRectangle(veil, zones[i].Key);
        }

        float Render(Graphics g, float scale)
        {
            g.Clear(Color.Black); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            zones.Clear();
            naturalGaps = minimumGaps = 0;
            float u = Height / 66f, cy = Height / 2f;
            float x = 0;
            var now = DateTime.Now;

            // Three pictures share compact 4-unit gaps, then the usual 6-unit gap to weather.
            float sys = 36 * u;
            using (var icon = DrawSys((int)Math.Round(sys), false, bigCpuPct)) g.DrawImage(icon, x, cy - sys / 2);
            Zone(x, x + sys, TaskMgrClick); x += sys;
            if (dropLevel < 3)
            {
                x += Gap(4 * u, scale);
                using (var icon = DrawSys((int)Math.Round(sys), true, bigRamPct)) g.DrawImage(icon, x, cy - sys / 2);
                Zone(x, x + sys, TaskMgrClick); x += sys;
            }
            if (dropLevel < 2)
            {
                x += Gap(4 * u, scale);
                using (var icon = DrawGpu((int)Math.Round(sys), GpuLevel(bigGpuPct))) g.DrawImage(icon, x, cy - sys / 2);
                Zone(x, x + sys, TaskMgrClick); x += sys;
            }
            x += Gap(6 * u, scale);

            // A fixed sign cell avoids moving neighbours on a temperature sign change.
            float weatherStart = x, picture = 44 * u;
            bool stale, weatherDay; int weatherCode; double? temperature;
            lock (weatherStateLock) { stale = wOk == DateTime.MinValue || (DateTime.Now - wOk).TotalMinutes > 60; temperature = wTempVal; weatherCode = wCode; weatherDay = wDay; }
            if (dropLevel < 1)
            {
                using (var icon = DrawWeatherSymbol((int)Math.Round(picture), weatherCode, stale, weatherDay)) g.DrawImage(icon, x, cy - picture / 2);
                x += picture + Gap(3 * u, scale);
            }
            Color tc = stale || !temperature.HasValue ? Muted : temperature.Value > 0 ? Warning : temperature.Value < 0 ? Light : TextCol;
            if (temperature.HasValue && Math.Round(temperature.Value) < 0) using (var pen = RoundPen(tc, 2.5f * u)) g.DrawLine(pen, x, cy, x + 7 * u, cy);
            x += 10 * u;
            x += Digits(g, temperature.HasValue ? Math.Min(99, Math.Abs((int)Math.Round(temperature.Value))).ToString() : "--", x, cy, 22 * u, tc);
            using (var pen = new Pen(tc, 1.8f * u)) g.DrawEllipse(pen, x + 2 * u, cy - 14 * u, 5 * u, 5 * u);
            x += 9 * u; Zone(weatherStart, x, WeatherClick); x += Gap(6 * u, scale);

            // 1) Wi-Fi fan (colour of the ping) + ping
            float x0 = x;
            Color fc = pingKnown ? PingColour(lastPing) : Light;
            float fan = 46 * u; using (var b = wired ? DrawEthernet((int)Math.Round(fan), fc) : DrawWifi((int)Math.Round(fan), bigWifiLit, fc)) g.DrawImage(b, x, cy - fan / 2);
            x += fan + Gap(3 * u, scale);
            int ms = pingKnown ? lastPing : -1;
            x += Digits(g, ms < 0 ? "--" : ms >= 100 ? "00" : ms.ToString(), x, cy, 15 * u, PingColour(ms));
            Zone(x0, x, NetClick); x += Gap(6 * u, scale);

            // 2) battery (case, segments, bolt) + percent in the panel font, coloured like the top lit segment
            if (bigBatShown >= 0)
            {
                x0 = x; float bs = 44 * u, bw = bs * 22.5f / 30f;   // the square picture centres the 22.5 x 30 battery: shift it to start at x
                using (var b = DrawBattery((int)Math.Round(bs), bigBatShown, bigPlugged)) g.DrawImage(b, x - (bs - bw) / 2, cy - bs / 2);
                x += bw + Gap(3 * u, scale);
                x += Digits(g, bigBatShown >= 100 ? "00" : bigBatShown.ToString(), x, cy, 22 * u, BatDigitColour(bigBatShown));
                Zone(x0, x, PowerClick); x += Gap(6 * u, scale);
            }

            // Microphone immediately before the speaker, in the same panel coordinate system.
            x0 = x; MicBody(g, x, cy, u, bigMicInUse, bigMicMuted); x += 34 * u;
            Zone(x0, x, MicClick); x += Gap(6 * u, scale);

            // 3) sound (Render-Audio): horn + three arcs lit by the volume, in speaker units = panel units; ink box x 5.7..59, y 12..52
            {
                x0 = x;
                var saved = g.Transform;
                using (var m = new Matrix()) { m.Translate(x - 5.7f * u, cy - 32 * u); m.Scale(u, u); g.MultiplyTransform(m); SpeakerBody(g, bigVol, bigMuted); }
                g.Transform = saved;
                x += 53.3f * u;
                Zone(x0, x, SoundClick); x += Gap(6 * u, scale);
            }

            // 4) keyboard layout badge (layouts.py): frame rx 3, FRAME 2, muted, 36 high; the code in its colour, centred by ink
            {
                // Panel letters (the OSD uses the same TextFont outlines); the frame a little wider than layouts.py (owner, batch 3)
                x0 = x; float cap = 20.4f * u, s = cap / TextFont.CAP, widest = 0, tw;
                foreach (var c in LangCodes) using (DiabloText(c, out tw)) widest = Math.Max(widest, tw * s);
                float bw = widest + 2 * (8 + 2) * u, bh = 36 * u;
                using (var p = RoundRectF(x + u, cy - bh / 2 + u, bw - 2 * u, bh - 2 * u, 3 * u)) using (var pen = new Pen(Muted, 2 * u)) g.DrawPath(pen, p);
                string code = layout >= 0 ? LangCodes[layout] : ""; Color col = layout >= 0 ? LangColours[layout] : Muted;
                using (var p = DiabloText(code, out tw)) using (var m = new Matrix(s, 0, 0, s, x + (bw - tw * s) / 2, cy - cap / 2)) using (var b = new SolidBrush(col)) { p.Transform(m); g.FillPath(b, p); }
                x += bw; Zone(x0, x, (right, box) => LangClick(right, box, lastFg)); x += Gap(6 * u, scale);
            }

            // 5) date "ЖОВ 25 НД" (Render-Date): month in the season colour, day white (larger), weekday in the rainbow colour
            {
                // whitebook dateicon.py 1:1 (owner 26.10): words at cap 20.4, day digits x1.25 (25.5), all three on ONE baseline,
                // Words use ink widths; the day reserves two full digit cells so 11 -> 12 never moves the weekday or clock.
                x0 = x; int wi = ((int)now.DayOfWeek + 6) % 7; float dcap = 25.5f * u, wcap = dcap / 1.25f;
                float wcy = cy + dcap / 2 - wcap / 2;   // a word whose bottom sits on the digits' baseline
                x += DiabloLine(g, UaMonthAbbr[now.Month - 1].ToUpper(), x, wcy, wcap, SeasonColour(now.Month)) + Gap(3 * u, scale);
                x += DayDigits(g, now.Day, x, cy, dcap, Foreground) + Gap(3 * u, scale);
                x += DiabloLine(g, UaWeek[wi], x, wcy, wcap, WeekdayColour[wi]);
                Zone(x0, x, CalClick); x += Gap(6 * u, scale);
            }

            // 6) clock (clockface.py): DSEG7 12-hour "h:MM" (blank leading cell), ghost "88:88" at 20 %, lit in light, the colon blinking with the seconds
            {
                x0 = x; float h = 36 * u, s = h / 1000f;
                using (var ghost = SegPaths("88:88", "")) using (var lit = SegPaths(Clock12(now, false), now.Second % 2 == 0 ? "" : ":"))
                using (var m = new Matrix(s, 0, 0, s, x, cy - h / 2))
                {
                    ghost.Transform(m); lit.Transform(m);
                    using (var b = new SolidBrush(Alpha(Foreground, 0.20))) g.FillPath(b, ghost);
                    using (var b = new SolidBrush(Light)) g.FillPath(b, lit);
                }
                x += SegWidth("88:88") * s;
                Zone(x0, x, ClockClick);
            }
            return x;
        }

        // A child window passes an unhandled WM_CONTEXTMENU to its parent: the taskbar would open its own menu over ours.
        protected override void WndProc(ref Message m) { if (m.Msg == 0x007B /*WM_CONTEXTMENU*/) return; base.WndProc(ref m); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            foreach (var z in zones)
                if (z.Key.Contains(e.Location))
                {
                    var screen = RectangleToScreen(z.Key);
                    z.Value(e.Button == MouseButtons.Right, screen);
                    break;
                }
            base.OnMouseUp(e);
        }
    }

    static GraphicsPath RoundRectF(float x, float y, float w, float h, float r) { return Rounded(new RectangleF(x, y, w, h), r); }
    // DSEG7 outlines of a string ("0".."9", ":") laid out with their advances, y down, ink from y 0 (top) to 1000.
    // 12-hour face text: " 1:05" / "12:30" (" 1:05:09" with seconds); a space is a blank digit cell for SegPaths.
    internal static string Clock12(DateTime t, bool seconds)
    {
        int h = t.Hour % 12; if (h == 0) h = 12;
        return h.ToString().PadLeft(2) + ":" + t.Minute.ToString("00") + (seconds ? ":" + t.Second.ToString("00") : "");
    }
    static float SegAdv(char c) { return c == ':' ? Glyphs.Dseg7ColonAdv : Glyphs.Dseg7Adv; }
    static float SegWidth(string t) { float w = 0; for (int i = 0; i < t.Length - 1; i++) w += SegAdv(t[i]); return w + Glyphs.Dseg7XMax - Glyphs.Dseg7XMin; }
    static GraphicsPath SegPaths(string text, string hide)
    {
        var all = new GraphicsPath(FillMode.Winding); float pen = -Glyphs.Dseg7XMin;
        foreach (char c in text)
        {
            if (c != ' ' && hide.IndexOf(c) < 0)   // ' ' = blank digit cell (12-hour leading hour)
                using (var p = Svg(c == ':' ? Glyphs.Dseg7Colon : Glyphs.Dseg7[c - '0'])) using (var m = new Matrix(1, 0, 0, -1, pen, 1000)) { p.Transform(m); all.AddPath(p, false); }
            pen += SegAdv(c);
        }
        return all;
    }
}
