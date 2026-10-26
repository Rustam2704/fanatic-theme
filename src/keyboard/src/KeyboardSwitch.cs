// Source is UTF-8 with BOM (Cyrillic menu text); build with csc /codepage:65001.
// Keyboard language switcher for fanatic.
//   Alt+Shift  : Ukrainian <-> last used of Russian/English
//   Ctrl+Shift : Russian <-> English (from Ukrainian: to the other of RU/EN than last used)
// Tray icon shows UA / RU / EN. Runs elevated (scheduled task) so it can switch layouts in
// admin windows too. Windows' own Alt+Shift / Ctrl+Shift hotkeys must be disabled.
// A small non-activating OSD shows the new layout code after each switch.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Hidden message-only window that receives WM_HOTKEY for global hotkeys.
sealed class HotkeyWindow : NativeWindow
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
    public HotkeyWindow() { CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /*HWND_MESSAGE*/ }); }
    public bool Register(int id, uint mod, uint vk, Action a)
    {
        if (!RegisterHotKey(Handle, id, mod, vk)) return false;
        actions[id] = a; return true;
    }
    protected override void WndProc(ref Message m)
    {
        Action a;
        if (m.Msg == 0x0312 /*WM_HOTKEY*/ && actions.TryGetValue((int)m.WParam, out a)) { try { a(); } catch { } }
        base.WndProc(ref m);
    }
}

// Precision touchpad watcher (Raw Input, HID digitizer page): remembers when exactly two fingers touched and when the
// physical touchpad button was pressed, so a right click produced by a two-finger TAP can be told from a corner PRESS.
sealed class Touchpad : NativeWindow
{
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pre, IntPtr report, uint len);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, [In, Out] ushort[] list, ref uint n, IntPtr pre, IntPtr report, uint len);
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    readonly Dictionary<IntPtr, IntPtr> preparsed = new Dictionary<IntPtr, IntPtr>();
    public static DateTime LastTwo = DateTime.MinValue, LastMore = DateTime.MinValue, LastButton = DateTime.MinValue;

    public Touchpad()
    {
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /*HWND_MESSAGE*/ });
        var d = new[] {
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05 /*touch pad*/, dwFlags = 0x100 /*INPUTSINK*/, hwndTarget = Handle },
        };
        if (!RegisterRawInputDevices(d, (uint)d.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) throw new InvalidOperationException("RegisterRawInputDevices " + Marshal.GetLastWin32Error());
    }

    // Exactly two fingers were on the pad just now (tap or press), not three or more.
    public static bool TwoFingersRecently()
    {
        DateTime now = DateTime.UtcNow;
        return (now - LastTwo).TotalMilliseconds < 400 && (now - LastMore).TotalMilliseconds > 400;
    }
    // The touchpad's physical button (the whole pad is a clickpad) was pressed just now.
    public static bool ButtonRecently() { return (DateTime.UtcNow - LastButton).TotalMilliseconds < 300; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF /*WM_INPUT*/) { try { Read(m.LParam); } catch { } }
        base.WndProc(ref m);
    }

    void Read(IntPtr hRaw)
    {
        int hdr = Marshal.SizeOf(typeof(IntPtr)) * 2 + 8;   // RAWINPUTHEADER: dwType, dwSize, hDevice, wParam
        uint size = 0;
        GetRawInputData(hRaw, 0x10000003 /*RID_INPUT*/, IntPtr.Zero, ref size, (uint)hdr);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, 0x10000003, buf, ref size, (uint)hdr) != size) return;
            if (Marshal.ReadInt32(buf, 0) != 2 /*RIM_TYPEHID*/) return;
            IntPtr dev = Marshal.ReadIntPtr(buf, 8);
            IntPtr pre = Preparsed(dev); if (pre == IntPtr.Zero) return;
            uint each = (uint)Marshal.ReadInt32(buf, hdr), count = (uint)Marshal.ReadInt32(buf, hdr + 4);
            for (uint i = 0; i < count; i++)
            {
                IntPtr rep = buf + hdr + 8 + (int)(i * each);

                uint contacts;
                if (HidP_GetUsageValue(0, 0x0D, 0, 0x54 /*contact count*/, out contacts, pre, rep, each) == HIDP_STATUS_SUCCESS)
                {
                    if (contacts == 2) LastTwo = DateTime.UtcNow;
                    else if (contacts > 2) LastMore = DateTime.UtcNow;
                }
                var usages = new ushort[8]; uint n = 8;
                if (HidP_GetUsages(0, 0x09 /*button*/, 0, usages, ref n, pre, rep, each) == HIDP_STATUS_SUCCESS && n > 0) LastButton = DateTime.UtcNow;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    IntPtr Preparsed(IntPtr dev)
    {
        IntPtr p;
        if (preparsed.TryGetValue(dev, out p)) return p;
        uint size = 0;
        GetRawInputDeviceInfo(dev, 0x20000005 /*RIDI_PREPARSEDDATA*/, IntPtr.Zero, ref size);
        if (size == 0) return IntPtr.Zero;
        p = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(dev, 0x20000005, p, ref size) == unchecked((uint)-1)) { Marshal.FreeHGlobal(p); return IntPtr.Zero; }
        preparsed[dev] = p;
        return p;
    }
}

static class KeyboardSwitch
{
    static bool EverythingTaskExists()
    {
        try
        {
            var p = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\schtasks.exe"), "/Query /TN everything-search") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var pr = Process.Start(p)) { pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd(); pr.WaitForExit(5000); return pr.ExitCode == 0; }
        }
        catch { return false; }
    }
    // Opens a new Everything search window; the running tray client handles it (no second instance).
    static void OpenEverything()
    {
        // Through the on-demand task "everything-search" (Limited): Everything runs un-elevated like its tray client.
        // Off the UI thread: it also runs the low-level hooks, which Windows drops if they stall.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var p = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\schtasks.exe"), "/Run /TN everything-search") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (Process.Start(p)) { }
            }
            catch (Exception ex) { Log("everything-search: " + ex.Message); }
        });
    }

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int WM_INPUTLANGCHANGEREQUEST = 0x50, LLKHF_INJECTED = 0x10;
    const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint tid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr LoadKeyboardLayout(string id, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilter(uint msg, uint flag);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static readonly string[] Codes = { "UA", "RU", "EN" };
    static readonly string[] Names = { "Українська", "Русский", "English" };
    // Owner's colours (whitebook theme.json "layouts"): UA #ffd447, RU #8f5b2e, EN #3b7ddd (tray icon, OSD, menu).
    static readonly Color[] Colors = { ColorTranslator.FromHtml("#ffd447"), ColorTranslator.FromHtml("#8f5b2e"), ColorTranslator.FromHtml("#3b7ddd") };
    static readonly string[] Klids = { "00000422", "00000419", "00000409" };
    static readonly ushort[] LangIds = { 0x0422, 0x0419, 0x0409 };
    static readonly IntPtr[] Hkl = new IntPtr[3];

    static HookProc hookProc, mouseProc;    // keep the delegates alive
    static IntPtr kbHook, msHook;
    const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B, WM_MOUSEWHEEL = 0x20A;

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    static bool swallowRightUp, touchpadOk, pendingTwo, upSeen; static int twoMode;   // twoMode: 0 undecided, 1 right, 2 middle
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);

    static IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN || msg == WM_MOUSEWHEEL)
                if (held.Count > 0) dirty = true;
            bool injected = (Marshal.ReadInt32(lParam, 12) & 1) != 0;   // MSLLHOOKSTRUCT.flags & LLMHF_INJECTED
            // Touchpad, two fingers: Windows reports both a TAP and a physical PRESS (click) as a right click.
            // Owner's wish: tap = right click, press = middle click. The right click is held back for 40 ms so the raw
            // HID report with the physical button state is processed first, then the matching button is sent.
            if (!injected && msg == WM_RBUTTONDOWN && touchpadOk && Touchpad.TwoFingersRecently())
            {
                pendingTwo = true; twoMode = 0;
                var t = new System.Windows.Forms.Timer { Interval = 40 };
                t.Tick += (s, e) =>
                {
                    t.Stop(); t.Dispose();
                    bool press = Touchpad.ButtonRecently();
                    twoMode = press ? 2 : 1;
                    mouse_event(press ? 0x0020u /*MIDDLEDOWN*/ : 0x0008u /*RIGHTDOWN*/, 0, 0, 0, IntPtr.Zero);
                    if (press) Log("two-finger press -> middle click");
                    if (upSeen) { upSeen = false; pendingTwo = false; mouse_event(press ? 0x0040u : 0x0010u, 0, 0, 0, IntPtr.Zero); }
                };
                t.Start();
                return (IntPtr)1;
            }
            if (!injected && msg == 0x205 /*WM_RBUTTONUP*/ && pendingTwo)
            {
                if (twoMode == 0) upSeen = true;   // released before the decision: the timer sends both
                else { pendingTwo = false; mouse_event(twoMode == 2 ? 0x0040u /*MIDDLEUP*/ : 0x0010u /*RIGHTUP*/, 0, 0, 0, IntPtr.Zero); }
                return (IntPtr)1;
            }
            // Right click in the Claude console would paste the clipboard into the prompt (conhost QuickEdit): eat it.
            try
            {
                if (msg == WM_RBUTTONDOWN)
                {
                    var p = new POINT { X = Marshal.ReadInt32(lParam, 0), Y = Marshal.ReadInt32(lParam, 4) };
                    if (IsClaudeConsole(GetAncestor(WindowFromPoint(p), 2 /*GA_ROOT*/))) { swallowRightUp = true; return (IntPtr)1; }
                }
                else if (msg == 0x205 /*WM_RBUTTONUP*/ && swallowRightUp) { swallowRightUp = false; return (IntPtr)1; }
            }
            catch (Exception ex) { Log("right click: " + ex.Message); }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
    static readonly HashSet<int> held = new HashSet<int>();
    static bool dirty, fired;
    static int lastLatin = 2;               // EN
    static int shown = -1;
    static NotifyIcon tray;
    static bool menuOpen; static string lastText = "Keyboard";
    // Tray icon size at the taskbar's DPI (per-monitor): a live display-scale change (125 % <-> 150 %) keeps the session's system DPI,
    // and an icon drawn for it would be stretched by the shell (blurred). Same as fanatic-bar.
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] static extern IntPtr FindTrayWindow(string cls, string title);
    static uint TaskbarDpi() { IntPtr t = FindTrayWindow("Shell_TrayWnd", null); uint d = t != IntPtr.Zero ? GetDpiForWindow(t) : 0; return d > 0 ? d : GetDpiForSystem(); }
    static int TrayIconSize() { int s = GetSystemMetricsForDpi(49, TaskbarDpi()); return s > 0 ? s : 16; }
    static void PerMonitorDpi() { if (!SetProcessDpiAwarenessContext((IntPtr)(-4))) SetProcessDPIAware(); }    static readonly Icon[] icons = new Icon[3], dimIcons = new Icon[3];
    static int iconSize;
    static SynchronizationContext ui;
    static Osd osd;

    public static void LogPublic(string m) { Log(m); }
    static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keyboard-switch.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + m + Environment.NewLine); } catch { }
    }

    // (Re)install both low-level hooks; Windows silently drops them after timeouts / resume.
    static bool InstallHooks()
    {
        // Install the new hooks first, then remove the old ones: no gap in which a key could be missed.
        IntPtr newKb = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
        // A mouse button pressed while modifiers are held (Alt+Shift+drag, Ctrl+Shift+click) makes it not a pure chord.
        IntPtr newMs = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
        if (newKb == IntPtr.Zero) { if (newMs != IntPtr.Zero) UnhookWindowsHookEx(newMs); return kbHook != IntPtr.Zero; }
        if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
        if (msHook != IntPtr.Zero) UnhookWindowsHookEx(msHook);
        kbHook = newKb; msHook = newMs;
        return true;
    }

    static FontFamily MakeFamily(string name)
    {
        try { return new FontFamily(name); } catch { return new FontFamily("Segoe UI"); }
    }

    // Small borderless, topmost, non-activating popup (never takes focus, click-through).
    class Osd : Form
    {
        string text = ""; Color color = Color.White; Font font; Region rgn; static FontFamily fam;
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
        public void ShowText(string t, Color c, int lw, int lh, int fontPx, int ms)
        {
            double sc = GetDpiForSystem() / 96.0;
            int w = (int)(lw * sc), h = (int)(lh * sc);
            text = t; color = c;
            if (fam == null) fam = MakeFamily("Bahnschrift SemiBold");
            if (font != null) font.Dispose();
            font = new Font(fam, (float)(fontPx * sc), FontStyle.Regular, GraphicsUnit.Pixel);
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
            // Center the glyphs' real ink box (DrawString centering includes font leading, so text sat off-center).
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Winding fill: Bahnschrift is a variable font whose glyph contours overlap; the default Alternate fill punched holes into them.
            using (var path = new GraphicsPath(FillMode.Winding))
            using (var b = new SolidBrush(color))
            {
                path.AddString(text, font.FontFamily, (int)font.Style, font.Size, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF r = path.GetBounds();
                using (var m = new Matrix())
                {
                    m.Translate(ClientSize.Width / 2f - (r.X + r.Width / 2f), ClientSize.Height / 2f - (r.Y + r.Height / 2f));
                    path.Transform(m);
                }
                e.Graphics.FillPath(b, path);
            }
        }
    }

    static void ShowOsd(int idx)
    {
        try { if (osd == null) osd = new Osd(); osd.ShowText(Codes[idx], Colors[idx], 120, 80, 30, 700); } catch (Exception ex) { Log("osd: " + ex.Message); }
    }

    [STAThread]
    static void Main()
    {
        Strings.Load(Strings.ResolveLang("en"));   // same shared language as the bar and the sky
        bool created;
        using (var mutex = new Mutex(true, "fanatic.KeyboardSwitch", out created))
        {
            if (!created) return;
            PerMonitorDpi();
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log("ThreadException: " + e.Exception);
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);

            for (int i = 0; i < 3; i++) Hkl[i] = LoadKeyboardLayout(Klids[i], 0);
            MakeIcons();
            iconSize = TrayIconSize();
            var dpiTimer = new System.Windows.Forms.Timer { Interval = 2000 };   // display scale changed: new icons at the new size, no restart
            dpiTimer.Tick += (s, e) =>
            {
                // fanatic-bar in big-indicator mode draws the layout itself: hide our tray icon while its mutex exists
                Mutex bm; bool bigOn = Mutex.TryOpenExisting("fanatic.Bar.BigMode", out bm); if (bm != null) bm.Dispose();
                if (tray.Visible == bigOn) tray.Visible = !bigOn;
                int sz = TrayIconSize(); if (sz == iconSize) return; iconSize = sz;
                MakeIcons();
                int cur = shown; shown = -2; UpdateIcon(cur);
            };
            dpiTimer.Start();

            // Elevated process: let Explorer's TaskbarCreated broadcast through UIPI so the icon returns after an explorer restart.
            ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), 1 /*MSGFLT_ADD*/);
            tray = new NotifyIcon { Visible = true, Text = "Keyboard" };
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = true };
            // Like the Windows input indicator: the languages (current one checked), then the language/time settings.
            var langItems = new ToolStripMenuItem[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                langItems[i] = new ToolStripMenuItem(Names[i], MakeSwatch(Colors[i]), (s, e) => SwitchFromMenu(idx)) { ShortcutKeyDisplayString = Codes[i] };
                menu.Items.Add(langItems[i]);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Strings.T("kbd.menu.lang_settings"), null, (s, e) => OpenSettings("ms-settings:regionlanguage"));
            menu.Items.Add(Strings.T("kbd.menu.keyboard_settings"), null, (s, e) => OpenSettings("ms-settings:typing"));
            menu.Items.Add(Strings.T("kbd.menu.datetime"), null, (s, e) => OpenSettings("ms-settings:dateandtime"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Strings.T("menu.exit"), null, (s, e) => Application.Exit());
            // The current language is bold (a check mark would be drawn over its colour dot).
            var boldFont = new Font(menu.Font, FontStyle.Bold);
            menu.Opening += (s, e) => { for (int i = 0; i < 3; i++) langItems[i].Font = i == shown ? boldFont : menu.Font; };
            // The tooltip would cover the open menu: blank it while the menu is shown.
            menu.Opened += (s, e) => { menuOpen = true; tray.Text = ""; };
            menu.Closed += (s, e) => { menuOpen = false; tray.Text = lastText; };
            tray.ContextMenuStrip = menu;
            // left click: the next layout in a full cycle UA -> RU -> EN -> UA (owner 26.10); right click: the menu
            tray.MouseClick += (s, e) => { if (e.Button != MouseButtons.Left) return; int cur = Current(); if (cur < 0) cur = shown; SwitchFromMenu((cur + 1) % 3); };

            hookProc = Hook;
            mouseProc = MouseHook;
            if (!InstallHooks()) { Log("keyboard hook failed, exiting"); tray.Visible = false; return; }

            // the Claude-console detection runs on its own thread: the hooks only read its result
            var scan = new Thread(ScanLoop) { IsBackground = true, Name = "claude-scan" }; scan.Start();
            // brightness on hover (as fanatic-bar): 78 %, 100 % while the pointer is over the icon (checked with the 150 ms timer)
            tray.MouseMove += (s, e) => { if (!hot) { hot = true; RedrawIcon(); } };
            IntPtr lastFgSeen = IntPtr.Zero;
            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (s, e) =>
            {
                RememberApp(); UpdateIcon(Current());
                var fg = GetForegroundWindow(); if (fg != lastFgSeen) { lastFgSeen = fg; scanKick.Set(); }   // a new window: rescan at once
                if (hot && !PointerOnIcon()) { hot = false; RedrawIcon(); }
            };
            timer.Start();
            var hookTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            hookTimer.Tick += (s, e) => { if (!InstallHooks()) Log("hook reinstall failed (timer)"); };
            hookTimer.Start();
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) ui.Post(_ => { if (!InstallHooks()) Log("hook reinstall failed (resume)"); }, null);
            };
            Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) ui.Post(_ => { if (!InstallHooks()) Log("hook reinstall failed (unlock)"); }, null);
            };
            int cur0 = Current();
            UpdateIcon(cur0 < 0 ? 0 : cur0);

            // Ctrl+Alt+F opens an Everything search window (Everything's own hotkey does not register here). Optional: only
            // when the on-demand task "everything-search" exists — otherwise the key would do nothing and still be taken
            // away from every other program (README: optional Everything).
            var hotkeys = new HotkeyWindow();
            if (!EverythingTaskExists()) Log("no everything-search task: Ctrl+Alt+F left free");
            else if (!hotkeys.Register(1, 0x0002 | 0x0001 | 0x4000 /*CTRL|ALT|NOREPEAT*/, 0x46 /*F*/, OpenEverything))
                Log("Ctrl+Alt+F hotkey is taken by another program");

            Touchpad touchpad = null;
            try { touchpad = new Touchpad(); touchpadOk = true; } catch (Exception ex) { Log("touchpad raw input: " + ex.Message); }

            Application.Run();
            GC.KeepAlive(timer); GC.KeepAlive(hookTimer); GC.KeepAlive(hotkeys); GC.KeepAlive(touchpad);   // locals must live as long as the loop
            tray.Visible = false;
        }
    }

    // The layout badge of the whitebook panel (icons-src\layouts.py panel_markup): the code in its colour, centred by its ink,
    // inside a rounded frame (rx 3, FRAME 2 px, muted #5b6b76) as tall as the CPU icon (36 of the 44 px panel). One frame
    // for every code (the widest sets it), so a layout change never changes the picture's size. The whole badge is scaled
    // to the tray slot (system small-icon size at the current DPI).
    static void MakeIcons()
    {
        for (int i = 0; i < 3; i++)
        {
            Icon a = icons[i], b = dimIcons[i];
            icons[i] = MakeIcon(Codes[i], Colors[i], false); dimIcons[i] = MakeIcon(Codes[i], Colors[i], true);
            if (a != null) a.Dispose(); if (b != null) b.Dispose();
        }
    }
    static void RedrawIcon() { int cur = shown; shown = -2; UpdateIcon(cur); }
    static Color Dimmed(Color c, bool dim) { return dim ? Color.FromArgb(c.A, (int)(c.R * DimLevel), (int)(c.G * DimLevel), (int)(c.B * DimLevel)) : c; }
    static Icon MakeIcon(string text, Color color, bool dim)
    {
        color = Dimmed(color, dim);
        int size = TrayIconSize();
        const float CANVAS = 44, BADGE = 36, FRAME = 2, GAP_INNER = 6, CAP = 36f * 544 / 960;   // panel px: layouts.py, sky_theme.py; cap = 36 px em x glyphs CAP/EM
        using (var bmp = new Bitmap(size, size))
        using (var g = Graphics.FromImage(bmp))
        using (var family = MakeFamily("Bahnschrift SemiBold Condensed"))
        using (var sf = StringFormat.GenericTypographic)
        {
            g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // ink of every code at a cap height of CAP panel px; the widest one sets the frame
            Func<string, GraphicsPath> ink = str => { var p = new GraphicsPath(FillMode.Winding); p.AddString(str, family, (int)FontStyle.Regular, 100f, PointF.Empty, sf); return p; };
            float capUnits; using (var e = ink("E")) capUnits = e.GetBounds().Height;
            float k100 = CAP / capUnits, widest = 0;
            foreach (var c in Codes) using (var p = ink(c)) widest = Math.Max(widest, p.GetBounds().Width * k100);
            float width = widest + 2 * (GAP_INNER + FRAME);
            float s = size / Math.Max(width, BADGE);   // panel px -> tray px: the frame is as tall as the CPU icon, which fills the slot
            float ox = (size - width * s) / 2, oy = (size - CANVAS * s) / 2;
            float top = (CANVAS - BADGE) / 2;
            using (var frame = RoundRect(ox + FRAME / 2 * s, oy + (top + FRAME / 2) * s, (width - FRAME) * s, (BADGE - FRAME) * s, 3 * s))
            using (var pen = new Pen(Dimmed(ColorTranslator.FromHtml("#5b6b76"), dim), Math.Max(1f, FRAME * s)))
                g.DrawPath(pen, frame);
            using (var p = ink(text))
            using (var b = new SolidBrush(color))
            {
                var bb = p.GetBounds(); float k = k100 * s;
                using (var m = new Matrix())
                {   // centred by ink, horizontally in the frame and vertically on the canvas
                    m.Translate(ox + width * s / 2, oy + CANVAS * s / 2); m.Scale(k, k); m.Translate(-(bb.X + bb.Width / 2), -(bb.Y + bb.Height / 2));
                    p.Transform(m);
                }
                g.FillPath(b, p);
            }
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
    }
    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90); p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    static void UpdateIcon(int idx)
    {
        if (idx < 0) return;                // unknown (e.g. desktop / lock screen): keep the current icon
        if (idx != 0) lastLatin = idx;
        if (idx == shown && hot == shownHot) return;
        shown = idx; shownHot = hot;
        tray.Icon = (hot ? icons : dimIcons)[idx];
        lastText = Strings.T("kbd.tip.layout", Codes[idx]);
        if (!menuOpen) tray.Text = lastText;
    }

    static uint ForegroundThread(out IntPtr target)
    {
        IntPtr fg = GetForegroundWindow();
        uint tid = GetWindowThreadProcessId(fg, IntPtr.Zero);
        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
        target = (GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero) ? gi.hwndFocus : fg;
        return tid;
    }

    // Index of the foreground window's layout, or -1 if it can't be determined.
    static int Current()
    {
        IntPtr target;
        uint tid = ForegroundThread(out target);
        long hkl = (long)GetKeyboardLayout(tid);
        if (hkl == 0)
        {
            // Console windows report the client (cmd) thread, which has no layout; the IME window
            // belongs to the real input thread (conhost / terminal host).
            IntPtr ime = ImmGetDefaultIMEWnd(GetForegroundWindow());
            if (ime != IntPtr.Zero) hkl = (long)GetKeyboardLayout(GetWindowThreadProcessId(ime, IntPtr.Zero));
        }
        ushort lang = (ushort)(hkl & 0xFFFF);
        for (int i = 0; i < 3; i++) if (LangIds[i] == lang) return i;
        return -1;
    }

    static void Switch(int idx)
    {
        IntPtr target;
        ForegroundThread(out target);
        IntPtr fg = GetForegroundWindow();
        PostMessage(target, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, Hkl[idx]);
        if (target != fg) PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, Hkl[idx]);
        if (idx != 0) lastLatin = idx;
        UpdateIcon(idx);
        ShowOsd(idx);
    }

    // The window the user was typing in before clicking the tray (the menu itself / the taskbar don't count).
    static IntPtr lastApp = IntPtr.Zero;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    static void RememberApp()
    {
        IntPtr fg = GetForegroundWindow(); if (fg == IntPtr.Zero) return;
        uint pid; GetWindowThreadProcessId(fg, out pid);
        if (pid == (uint)Process.GetCurrentProcess().Id) return;
        var cls = new System.Text.StringBuilder(64); GetClassName(fg, cls, 64);
        string c = cls.ToString();
        if (c == "Shell_TrayWnd" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland" || c == "Progman" || c == "WorkerW") return;
        lastApp = fg;
    }

    static void SwitchFromMenu(int idx)
    {
        // Give the focus back to that window first: the layout change goes to the foreground window.
        if (lastApp != IntPtr.Zero && IsWindow(lastApp)) SetForegroundWindow(lastApp);
        var t = new System.Windows.Forms.Timer { Interval = 120 };
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); Switch(idx); };
        t.Start();
    }

    // Settings pages through explorer.exe, so they open un-elevated like from the Windows indicator.
    static void OpenSettings(string uri)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), uri) { UseShellExecute = false })) { } }
            catch (Exception ex) { Log("settings: " + ex.Message); }
        });
    }

    static Bitmap MakeSwatch(Color c)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        using (var b = new SolidBrush(c))
        { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(b, 3, 3, 10, 10); }
        return bmp;
    }

    static void AltShift() { int cur = Current(); if (cur < 0) cur = shown; Switch(cur == 0 ? lastLatin : 0); }
    static void CtrlShift() { int cur = Current(); if (cur < 0) cur = shown; int basis = cur == 0 ? lastLatin : cur; Switch(basis == 1 ? 2 : 1); }

    static bool IsShift(int vk) { return vk == VK_LSHIFT || vk == VK_RSHIFT; }
    static bool IsCtrl(int vk) { return vk == VK_LCONTROL || vk == VK_RCONTROL; }
    static bool IsAlt(int vk) { return vk == VK_LMENU || vk == VK_RMENU; }

    // ---- Claude Code in a classic console: conhost cannot tell Shift+Enter from Enter, and an image in the
    // clipboard is pasted with Alt+V there. So in a console window that runs claude.exe:
    //   Shift+Enter -> Ctrl+J (Claude Code: new line);  Ctrl+V with an image (no text) in the clipboard -> Alt+V.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32 { public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
    const int VK_RETURN = 0x0D, VK_V = 0x56, VK_J = 0x4A;
    static int swallowUp = -1;   // the key whose key-up must be eaten too (its key-down was replaced)

    // The console windows that host claude.exe, found by ScanLoop (a background thread): at once when the
    // foreground window changes (every 2 s otherwise, as the old 2 s cache). The set is replaced, never changed, so the hooks read it without a lock and without any
    // system call: a low-level hook must return fast (LowLevelHooksTimeout), the process walk used to run inside it.
    static volatile HashSet<IntPtr> claudeWindows = new HashSet<IntPtr>();
    static readonly AutoResetEvent scanKick = new AutoResetEvent(true);
    static bool IsClaudeConsole(IntPtr hwnd) { return hwnd != IntPtr.Zero && claudeWindows.Contains(hwnd); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    static void ScanLoop()
    {
        while (true)
        {
            scanKick.WaitOne(2000);
            try { claudeWindows = ScanClaude(); } catch { }
        }
    }
    // A classic console window whose owner process is claude.exe or one of its ancestors (claude runs inside the shell).
    static HashSet<IntPtr> ScanClaude()
    {
        var found = new HashSet<IntPtr>(); var consoles = new List<IntPtr>();
        for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "ConsoleWindowClass", null); h != IntPtr.Zero; h = FindWindowEx(IntPtr.Zero, h, "ConsoleWindowClass", null))
            consoles.Add(h);
        if (consoles.Count == 0) return found;   // no console: no process walk at all
        var parent = new Dictionary<uint, uint>(); var claude = new List<uint>();
        IntPtr snap = CreateToolhelp32Snapshot(2 /*TH32CS_SNAPPROCESS*/, 0);
        if (snap == (IntPtr)(-1)) return found;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                parent[e.th32ProcessID] = e.th32ParentProcessID;
                if (string.Equals(e.szExeFile, "claude.exe", StringComparison.OrdinalIgnoreCase)) claude.Add(e.th32ProcessID);
            }
        }
        finally { CloseHandle(snap); }
        if (claude.Count == 0) return found;
        foreach (var hwnd in consoles)
        {
            uint owner; GetWindowThreadProcessId(hwnd, out owner); bool yes = false;
            foreach (uint c in claude)
            {
                uint p = c;
                for (int i = 0; i < 8 && !yes; i++) { if (p == owner) yes = true; else if (!parent.TryGetValue(p, out p) || p == 0) break; }
                if (yes) break;
            }
            if (yes) found.Add(hwnd);
        }
        return found;
    }

    // ---- brightness on hover: NotifyIcon has no "mouse left", so the pointer is checked against the icon's rectangle ----
    const float DimLevel = 0.78f;
    static bool hot, shownHot;
    [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    static System.Reflection.FieldInfo trayWindowField, trayIdField;
    static bool PointerOnIcon()
    {
        try
        {
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            if (trayWindowField == null) { trayWindowField = typeof(NotifyIcon).GetField("window", bf); trayIdField = typeof(NotifyIcon).GetField("id", bf); }
            var nid = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = ((NativeWindow)trayWindowField.GetValue(tray)).Handle, uID = (uint)(int)trayIdField.GetValue(tray) };
            RECT r; if (Shell_NotifyIconGetRect(ref nid, out r) != 0) return false;
            var c = Cursor.Position; return c.X >= r.l && c.X < r.r && c.Y >= r.t && c.Y < r.b;
        }
        catch { return false; }
    }

    // Runs after the hook returned: release the physically held modifier, send the replacement, press it back.
    static void SendReplacement(byte heldMod, byte newMod, byte key)
    {
        const uint UP = 2;
        keybd_event(heldMod, 0, UP, IntPtr.Zero);
        keybd_event(newMod, 0, 0, IntPtr.Zero); keybd_event(key, 0, 0, IntPtr.Zero);
        keybd_event(key, 0, UP, IntPtr.Zero); keybd_event(newMod, 0, UP, IntPtr.Zero);
        if (held.Contains(heldMod)) keybd_event(heldMod, 0, 0, IntPtr.Zero);   // still physically held (the hook tracks real keys)
    }

    // Returns true when the key was replaced (the caller then swallows it).
    static bool ClaudeKeys(int vk, bool down, bool up)
    {
        if (up && vk == swallowUp) { swallowUp = -1; return true; }
        if (!down) return false;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0, ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0, alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        if (vk == VK_RETURN && shift && !ctrl && !alt && IsClaudeConsole(GetForegroundWindow()))
        {
            byte s = (byte)((GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0 ? VK_RSHIFT : VK_LSHIFT);
            swallowUp = vk; ui.Post(_ => SendReplacement(s, 0x11 /*Ctrl*/, VK_J), null); return true;
        }
        // Ctrl+Enter: conhost turns it into a line feed, so send Claude Code's "send now" chord Ctrl+X Ctrl+S instead.
        if (vk == VK_RETURN && ctrl && !shift && !alt && IsClaudeConsole(GetForegroundWindow()))
        {
            swallowUp = vk;
            ui.Post(_ =>
            {
                // Self-contained chord (Ctrl may already be released when this runs); re-press Ctrl if still held.
                keybd_event(VK_LCONTROL, 0, 0, IntPtr.Zero);
                keybd_event(0x58 /*X*/, 0, 0, IntPtr.Zero); keybd_event(0x58, 0, 2, IntPtr.Zero);
                keybd_event(0x53 /*S*/, 0, 0, IntPtr.Zero); keybd_event(0x53, 0, 2, IntPtr.Zero);
                keybd_event(VK_LCONTROL, 0, 2, IntPtr.Zero);
                foreach (int c in new[] { VK_LCONTROL, VK_RCONTROL }) if (held.Contains(c)) keybd_event((byte)c, 0, 0, IntPtr.Zero);
            }, null);
            return true;
        }
        if (vk == VK_V && ctrl && !shift && !alt && IsClipboardFormatAvailable(8 /*CF_DIB*/) && !IsClipboardFormatAvailable(13 /*CF_UNICODETEXT*/)
            && IsClaudeConsole(GetForegroundWindow()))
        {
            byte c = (byte)((GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0 ? VK_RCONTROL : VK_LCONTROL);
            swallowUp = vk; ui.Post(_ => SendReplacement(c, 0x12 /*Alt*/, VK_V), null); return true;
        }
        return false;
    }

    static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam, vk = k.vkCode;
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN, up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                try { if (ClaudeKeys(vk, down, up)) return (IntPtr)1; } catch (Exception ex) { Log("claude keys: " + ex.Message); }
                bool mod = IsShift(vk) || IsCtrl(vk) || IsAlt(vk);
                if (down)
                {
                    // Drop modifiers whose key-up we never saw (e.g. lost across Win+L / UAC).
                    held.RemoveWhere(h => h != vk && (GetAsyncKeyState(h) & 0x8000) == 0);
                    if (held.Count == 0) { dirty = false; fired = false; }
                    if (!mod) { if (held.Count > 0) dirty = true; }   // another key joined: not a pure chord
                    else held.Add(vk);
                }
                else if (up)
                {
                    if (mod && !dirty && !fired && held.Count == 2)
                    {
                        bool shift = false, ctrl = false, alt = false;
                        foreach (int h in held) { shift |= IsShift(h); ctrl |= IsCtrl(h); alt |= IsAlt(h); }
                        // Right Alt is AltGr on Ukrainian - only Left Alt counts.
                        if (shift && alt && held.Contains(VK_LMENU)) { fired = true; ui.Post(_ => AltShift(), null); }
                        else if (shift && ctrl) { fired = true; ui.Post(_ => CtrlShift(), null); }
                    }
                    held.Remove(vk);
                    if (held.Count == 0) { dirty = false; fired = false; }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
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