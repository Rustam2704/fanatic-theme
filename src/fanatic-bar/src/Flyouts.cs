// Flyout windows of the bar, ported from the whitebook menus (the prototype's menus-src):
// popmenu.py (.sky-menu style; closes on a click outside, Escape or focus loss; one instance, a second click toggles),
// timemenu.py + clockface.py (clock), panel-calendar.py (calendar), powermenu.py (power), and a 10-day weather list (new).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static partial class FanaticBar
{
    // ---- .sky-menu palette (popmenu.py STYLE, theme.json) ----
    static readonly Color SkyBorder = Hex("#1e262c"), SkySurface = Hex("#12171a"), SkyRaised = Hex("#1b2329"), SkyAccent = Hex("#0d8ecb"), SkyDim = Hex("#3f4a52");
    static float SkyK { get { return GetDpiForSystem() / 120f; } }
    static int Px(float v) { return (int)Math.Round(v * SkyK); }
    static Font SkyFont(float pt, bool bold) { return new Font(Family("Consolas"), pt, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
    // Windows 11 ships "Segoe Fluent Icons"; Windows 10 only "Segoe MDL2 Assets" (same code points for the glyphs we use)
    static readonly string IconFont = Array.Exists(new System.Drawing.Text.InstalledFontCollection().Families, f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = Math.Min(2 * rad, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // NotifyIcon -> its screen rectangle (Shell_NotifyIconGetRect), or a small box at the pointer.
    static Rectangle IconRect(NotifyIcon ni)
    {
        try
        {
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var win = (NativeWindow)typeof(NotifyIcon).GetField("window", bf).GetValue(ni);
            var nid = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = win.Handle, uID = (uint)(int)typeof(NotifyIcon).GetField("id", bf).GetValue(ni) };
            RECT r; if (Shell_NotifyIconGetRect(ref nid, out r) == 0) return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch { }
        var c = Cursor.Position; return new Rectangle(c.X - 8, c.Y - 8, 16, 16);
    }

    // ================= the popup frame (popmenu.py PopMenu) =================
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    delegate IntPtr LowLevelProc(int code, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }

    abstract class SkyPopup : Form
    {
        static readonly Dictionary<Type, SkyPopup> open = new Dictionary<Type, SkyPopup>();
        static readonly Dictionary<Type, DateTime> closedAt = new Dictionary<Type, DateTime>();
        protected Rectangle IconBox;
        protected readonly int M = Px(16), Gap = Px(12);   // Gtk.Grid margin 16, row spacing 12
        IntPtr hook; LowLevelProc hookProc;

        static readonly Dictionary<Type, Rectangle> closedBox = new Dictionary<Type, Rectangle>();
        // Open the popup of this type at the icon, or close it when it is already open for this icon (or was just closed by
        // this same click). The same type opened from another icon (the right-click menus) moves there instead.
        public static void Toggle<T>(NotifyIcon ni, Func<T> make) where T : SkyPopup { ToggleAt(ni != null ? IconRect(ni) : Rectangle.Empty, make); }
        // The same for an anchor rectangle on screen (the big indicators of BigBar.cs).
        public static void ToggleAt<T>(Rectangle box, Func<T> make) where T : SkyPopup
        {
            SkyPopup cur; DateTime t; Rectangle cb;
            if (open.TryGetValue(typeof(T), out cur) && cur != null && !cur.IsDisposed) { bool same = cur.IconBox == box; cur.Close(); if (same) return; }
            else if (closedAt.TryGetValue(typeof(T), out t) && (DateTime.Now - t).TotalMilliseconds < 400
                     && closedBox.TryGetValue(typeof(T), out cb) && cb == box) return;   // the click on this icon that closed it
            var p = make(); p.IconBox = box; open[typeof(T)] = p; p.Popup();
        }
        public static void OpenAt<T>(Rectangle anchor, Func<T> make) where T : SkyPopup
        {
            SkyPopup cur; if (open.TryGetValue(typeof(T), out cur) && cur != null && !cur.IsDisposed) return;
            var p = make(); p.IconBox = anchor; open[typeof(T)] = p; p.Popup();
        }

        protected SkyPopup()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Background; ForeColor = Foreground; KeyPreview = true; DoubleBuffered = true; AutoScaleMode = AutoScaleMode.None;
            Font = SkyFont(11, false);
        }
        // glass background with DWM's rounded corners (a Region would cut the accent's surface)
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Glass(Handle); }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*TOOLWINDOW*/; cp.ClassStyle |= 0x20000 /*DROPSHADOW*/; return cp; }
        }

        void Popup()
        {
            Relayout();
            Show(); Activate(); SetForegroundWindow(Handle);
            hookProc = MouseHook; hook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, hookProc, GetModuleHandle(null), 0);
        }
        // A press anywhere outside closes it (the seat grab's "pressed" in popmenu.py); works even if activation was refused.
        IntPtr MouseHook(int code, IntPtr w, IntPtr l)
        {
            int m = (int)w;
            if (code >= 0 && (m == 0x201 || m == 0x204 || m == 0x207))
            {
                var s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                if (!Bounds.Contains(s.X, s.Y)) CloseSoon();
            }
            return CallNextHookEx(hook, code, w, l);
        }
        bool closing;
        protected override void OnFormClosing(FormClosingEventArgs e) { closing = true; base.OnFormClosing(e); }
        void CloseSoon() { if (!closing && !IsDisposed && IsHandleCreated) BeginInvoke(new Action(Close)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); CloseSoon(); }
        protected override bool ProcessCmdKey(ref Message msg, Keys key)
        {
            if (key == Keys.Escape && !EscapeHandled()) { Close(); return true; }
            return OnKey(key) || base.ProcessCmdKey(ref msg, key);
        }
        protected virtual bool EscapeHandled() { return false; }
        protected virtual bool OnKey(Keys key) { return false; }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            SkyPopup cur; if (open.TryGetValue(GetType(), out cur) && cur == this) open.Remove(GetType());
            closedAt[GetType()] = DateTime.Now; closedBox[GetType()] = IconBox;
            base.OnFormClosed(e);
        }

        // Rows from Build() stacked in one column, as in the Gtk.Grid of popmenu.py; every row gets the column's width.
        protected abstract List<Control> Build();
        protected void Relayout()
        {
            SuspendLayout();
            var gone = new Control[Controls.Count]; Controls.CopyTo(gone, 0); Controls.Clear();
            foreach (var c in gone) c.Dispose();
            var rows = Build(); int w = 0;
            foreach (var c in rows) w = Math.Max(w, c.Width);
            int y = M;
            foreach (var c in rows) { if (!(c is Label)) c.Width = w; else if (c.Width < w && ((Label)c).TextAlign != ContentAlignment.TopLeft) c.Width = w; c.Location = new Point(M, y); y += c.Height + Gap; Controls.Add(c); }
            ClientSize = new Size(w + 2 * M, y - Gap + M);
            Place();
            ResumeLayout(); Invalidate();
        }
        // Above the taskbar at the icon (below it when the taskbar is on top), centred on it, kept on the screen.
        void Place()
        {
            var wa = Screen.FromRectangle(IconBox).WorkingArea; var Anchor = IconBox; int gap = Px(6);
            int x = Math.Max(wa.Left + gap, Math.Min(Anchor.Left + Anchor.Width / 2 - Width / 2, wa.Right - Width - gap));
            int y = Anchor.Top >= wa.Bottom - 2 ? wa.Bottom - Height - gap : Anchor.Bottom <= wa.Top + 2 ? wa.Top + gap : Anchor.Top - Height - gap;
            Location = new Point(x, Math.Max(wa.Top, y));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Px(8))) using (var pen = new Pen(SkyBorder, 1)) e.Graphics.DrawPath(pen, p);
        }

        // ---- row factories ----
        protected Label MakeLabel(string s, Color c, float pt, bool bold)
        {
            var l = new Label { Text = s, ForeColor = c, BackColor = Background, Font = SkyFont(pt, bold), AutoSize = true, UseMnemonic = false, TextAlign = ContentAlignment.TopLeft };
            l.Size = l.GetPreferredSize(Size.Empty); l.AutoSize = false; return l;
        }
        protected Label RowLabel(string s) { return MakeLabel(s, Muted, 9, false); }   // .row-label
    }

    // A centred label in Diablo capitals (the clock flyout's date), the cap height of the Consolas text it replaces.
    sealed class DiabloLabel : Label
    {
        public static DiabloLabel Make(string s, Color c, float pt)
        {
            var l = new DiabloLabel { Text = s, ForeColor = c, BackColor = Background, Font = SkyFont(pt, false), AutoSize = true, UseMnemonic = false };
            var sz = l.GetPreferredSize(Size.Empty); l.AutoSize = false;
            l.Size = new Size(Math.Max(sz.Width, (int)Math.Ceiling(DiabloLine(null, s, 0, 0, l.Font.Height * 0.55f, c)) + 4), sz.Height); return l;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float cap = Font.Height * 0.55f; DiabloLine(g, Text, (Width - DiabloLine(null, Text, 0, 0, cap, ForeColor)) / 2, Height / 2f, cap, ForeColor);
        }
    }

    // A .sky-menu button: surface plate, border, hover border light; checked = accent plate with black text; optional icon and sub-line.
    sealed class SkyButton : Control
    {
        public string Glyph, Sub; public bool Checked, Marked; bool hover;
        public SkyButton(string text, string glyph)
        {
            Text = text; Glyph = glyph; DoubleBuffered = true; BackColor = Background; Font = SkyFont(11, false); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, false);
        }
        public Size Preferred()
        {
            using (var g = CreateGraphics())
            {
                var t = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
                int w = t.Width, h = t.Height;
                if (Sub != null) { using (var f = SkyFont(9, false)) { var s = TextRenderer.MeasureText(g, Sub, f, Size.Empty, TextFormatFlags.NoPadding); w = Math.Max(w, s.Width); h += Px(2) + s.Height; } }
                if (Glyph != null) w += Px(24) + Px(12);
                return new Size(w + 2 * Px(12) + 2, Math.Max(h, Glyph != null ? Px(24) : 0) + 2 * Px(6) + 2);
            }
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var p = Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Px(8)))
            using (var b = new SolidBrush(Checked ? SkyAccent : SkySurface)) using (var pen = new Pen(hover && Enabled ? Light : SkyBorder, 1))
            { g.FillPath(b, p); g.DrawPath(pen, p); }
            Color fg = !Enabled ? Muted : Checked ? Color.Black : Marked ? Light : Foreground;
            int x = Px(12) + 1;
            if (Glyph != null)
            {
                using (var f = new Font(IconFont, Px(15), GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, Glyph, f, new Rectangle(x, 0, Px(24), Height), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += Px(24) + Px(12);
            }
            var t = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            if (Sub == null)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            else
                using (var f = SkyFont(9, false))
                {
                    var s = TextRenderer.MeasureText(g, Sub, f, Size.Empty, TextFormatFlags.NoPadding);
                    int y = (Height - t.Height - Px(2) - s.Height) / 2;
                    TextRenderer.DrawText(g, Text, Font, new Point(x, y), fg, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, Sub, f, new Point(x, y + t.Height + Px(2)), Checked ? Color.Black : Muted, TextFormatFlags.NoPadding);
                }
        }
    }
    static SkyButton Button(string text, string glyph, Action click)
    {
        var b = new SkyButton(text, glyph); b.Size = b.Preferred();
        b.Click += (s, e) => { var f = b.FindForm(); if (f != null) f.BeginInvoke(click); else click(); };   // never rebuild the popup inside the clicked button's own handler
        return b;
    }

    // ================= right-click menus: labelled choices instead of an immediate action (owner, batch 2 B1) =================
    sealed class MenuItem { public string Text, Glyph; public Action Do; public MenuItem(string text, string glyph, Action a) { Text = text; Glyph = glyph; Do = a; } }
    sealed class MenuPopup : SkyPopup
    {
        readonly MenuItem[] items;
        public MenuPopup(MenuItem[] items) { this.items = items; }
        protected override List<Control> Build()
        {
            var rows = new List<Control>();
            foreach (var it in items) { var a = it.Do; rows.Add(Button(it.Text, it.Glyph, () => { Close(); a(); })); }
            return rows;
        }
    }
    static void Menu(NotifyIcon ni, params MenuItem[] items) { SkyPopup.Toggle(ni, () => new MenuPopup(items)); }
    static void MenuAt(Rectangle box, params MenuItem[] items) { SkyPopup.ToggleAt(box, () => new MenuPopup(items)); }
    static MenuItem Item(string text, string glyph, Action a) { return new MenuItem(text, glyph, a); }
    // A flyout opened from a menu item, at the icon the menu belonged to.
    static void Fly<T>(NotifyIcon ni, Func<T> make) where T : SkyPopup { SkyPopup.Toggle(ni, make); }
    static void FlyAt<T>(Rectangle box, Func<T> make) where T : SkyPopup { SkyPopup.ToggleAt(box, make); }

    // ================= clock (timemenu.py + clockface.py, with seconds) =================
    // IANA names as in timemenu.py ZONES, mapped to Windows time zone ids.
    static readonly string[][] Zones = {
        new[] { "Europe/Kyiv", "FLE Standard Time" }, new[] { "Europe/Warsaw", "Central European Standard Time" },
        new[] { "Europe/Berlin", "W. Europe Standard Time" }, new[] { "Europe/London", "GMT Standard Time" }, new[] { "UTC", "UTC" },
        new[] { "Asia/Tbilisi", "Georgian Standard Time" }, new[] { "Asia/Yerevan", "Caucasus Standard Time" }, new[] { "America/New_York", "Eastern Standard Time" } };
    static string ZoneName(string winId) { foreach (var z in Zones) if (z[1] == winId) return z[0]; return winId; }

    // The big seven-segment clock: ghost "88:88:88" at 20 % foreground, the time lit in light; the colons blink (lit on even seconds).
    sealed class ClockFace : Control
    {
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        int shown = -1;
        public ClockFace()
        {
            DoubleBuffered = true; BackColor = Background;
            float h = Px(56), s = h / 1000f; Size = new Size((int)Math.Ceiling(InkWidth() * s) + 2, (int)h + 2);
            timer.Tick += (o, e) => { int sec = DateTime.Now.Second; if (sec != shown) { shown = sec; Invalidate(); } }; timer.Start();
        }
        protected override void Dispose(bool d) { if (d) timer.Dispose(); base.Dispose(d); }
        static float Adv(char c) { return c == ':' ? Glyphs.Dseg7ColonAdv : Glyphs.Dseg7Adv; }
        static float InkWidth() { const string G = "88:88:88"; float w = 0; for (int i = 0; i < G.Length - 1; i++) w += Adv(G[i]); return w + Glyphs.Dseg7XMax - Glyphs.Dseg7XMin; }
        static GraphicsPath Paths(string text, string hide)
        {
            var all = new GraphicsPath(FillMode.Winding); float pen = -Glyphs.Dseg7XMin;
            foreach (char c in text)
            {
                if (hide.IndexOf(c) < 0)
                    using (var p = Svg(c == ':' ? Glyphs.Dseg7Colon : Glyphs.Dseg7[c - '0'])) using (var m = new Matrix(1, 0, 0, -1, pen, 1000)) { p.Transform(m); all.AddPath(p, false); }
                pen += Adv(c);
            }
            return all;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var now = DateTime.Now; float s = (Height - 2) / 1000f;
            using (var ghost = Paths("88:88:88", "")) using (var lit = Paths(now.ToString("HH:mm:ss"), now.Second % 2 == 0 ? "" : ":"))
            using (var m = new Matrix(s, 0, 0, s, 1 + (Width - 2 - InkWidth() * s) / 2, 1))
            {
                ghost.Transform(m); lit.Transform(m);
                using (var b = new SolidBrush(Alpha(Foreground, 0.20))) g.FillPath(b, ghost);
                using (var b = new SolidBrush(Light)) g.FillPath(b, lit);
            }
        }
    }

    // "--shot-fly out.png": the flyout parts with Diablo text drawn off screen (no clicks): the clock face with its date line, the
    // calendar's days and months faces, side by side on the flyout background.
    static void ShotFly(string file)
    {
        // the network tooltip of the big Wi-Fi / ping indicator with live readings (6 s of pings) -> <file>-net-tip.png
        PollIface();
        var prof = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
        wifiName = prof != null ? prof.ProfileName : null; wired = prof != null && !prof.IsWlanConnectionProfile;
        for (int i = 0; i < 6; i++)
        {
            string gw = gateway; var ti = PingOnce(InternetHost, 900); var tr = gw != null ? PingOnce(gw, 500) : null;
            internetPings.Add(ti.Result); if (tr != null) routerPings.Add(tr.Result); PollBytes();
            Thread.Sleep(1000);
        }
        string tipText = NetTip(); var ts = TipSize(tipText);
        using (var tb = new Bitmap(ts.Width, ts.Height)) using (var tg = Graphics.FromImage(tb))
        {
            DrawTip(tg, new Rectangle(Point.Empty, ts), tipText);
            tb.Save(System.IO.Path.ChangeExtension(file, null) + "-net-tip.png", System.Drawing.Imaging.ImageFormat.Png);
        }

        var parts = new List<Control>();
        using (var face = new ClockFace()) using (var date = DiabloLabel.Make(UaWeekFull[((int)DateTime.Now.DayOfWeek + 6) % 7] + ", " + DateTime.Now.Day + " " + UaMonthFull[DateTime.Now.Month - 1] + " " + DateTime.Now.Year, Foreground, 12))
        using (var days = new CalendarView(null)) using (var months = new CalendarView(null))
        {
            months.ShowMonths();
            int pad = Px(12), w = Math.Max(face.Width, date.Width) + days.Width + months.Width + pad * 5 + Px(560), h = Math.Max(Math.Max(face.Height + date.Height + pad, days.Height), Px(36) * 7) + pad * 2;
            using (var bmp = new Bitmap(w, h)) using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Background); int x = pad;
                Action<Control, int, int> put = (c, cx, cy) => { using (var b = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height)); g.DrawImageUnscaled(b, cx, cy); } };
                int cw = Math.Max(face.Width, date.Width);
                put(face, x + (cw - face.Width) / 2, pad); put(date, x + (cw - date.Width) / 2, pad + face.Height + pad); x += cw + pad;
                put(days, x, pad); x += days.Width + pad; put(months, x, pad); x += months.Width + pad;
                int[] codes = { 0, 2, 3, 61, 45, 71, 95, 51, 80, 3, 0, 1 }; var demo = new List<HourForecast>(); var h0 = DateTime.Today.AddHours(DateTime.Now.Hour);
                for (int i = 0; i < 24; i++) demo.Add(new HourForecast { Time = h0.AddHours(i), Code = codes[i % codes.Length], Temp = 9 - i * 0.6, Day = h0.AddHours(i).Hour >= 7 && h0.AddHours(i).Hour < 18 });
                using (var r = new HourList(demo)) put(r, x, pad);
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
    sealed class ClockPopup : SkyPopup
    {
        string page = "main", status = ""; bool busy;
        readonly System.Windows.Forms.Timer dayTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        public ClockPopup()
        {
            status = Strings.T("clock.sync.checking");
            int day = DateTime.Now.Day; dayTimer.Tick += (s, e) => { if (DateTime.Now.Day != day) { day = DateTime.Now.Day; Relayout(); } }; dayTimer.Start();
        }
        protected override void Dispose(bool d) { if (d) dayTimer.Dispose(); base.Dispose(d); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); QueryNtp(); }
        static string FullDate() { var n = DateTime.Now; return UaWeekFull[((int)n.DayOfWeek + 6) % 7] + ", " + n.Day + " " + UaMonthFull[n.Month - 1] + " " + n.Year; }
        protected override List<Control> Build()
        {
            var rows = new List<Control>();
            rows.Add(new ClockFace());
            rows.Add(DiabloLabel.Make(FullDate(), Foreground, 12));
            TimeZoneInfo.ClearCachedData(); string zone = TimeZoneInfo.Local.Id;
            var z = RowLabel(ZoneName(zone) + " · " + TimeZoneInfo.Local.DisplayName.Split(' ')[0]); z.TextAlign = ContentAlignment.MiddleCenter; rows.Add(z);
            if (page == "main")
            {
                rows.Add(Button(Strings.T("clock.btn.sync_now"), "\uE72C", SyncNow));
                rows.Add(Button(Strings.T("clock.btn.timezone"), "\uE775", () => { page = "zones"; Relayout(); }));
                rows.Add(Button(Strings.T("clock.btn.calendar"), "\uE787", () => { Close(); OpenAt<CalendarPopup>(IconBox, () => new CalendarPopup()); }));
                rows.Add(Button(Strings.T("clock.btn.datetime_settings"), "\uE713", () => { Close(); Open("ms-settings:dateandtime"); }));
            }
            else
            {
                foreach (var zz in Zones) { var id = zz[1]; var b = Button(zz[0], "\uE775", () => SetZone(id)); b.Marked = id == zone; rows.Add(b); }
                rows.Add(Button(Strings.T("clock.btn.other_zone"), "\uE713", () => { Close(); Open("ms-settings:dateandtime"); }));
                rows.Add(Button(Strings.T("common.back"), "\uE72B", () => { page = "main"; Relayout(); }));
            }
            foreach (var c in rows) if (c is SkyButton) c.Enabled = !busy;
            var st = RowLabel(status); rows.Add(st);
            return rows;
        }
        protected override bool EscapeHandled() { if (page == "main") return false; page = "main"; Relayout(); return true; }

        void Work(string doing, Func<string> action)
        {
            busy = true; status = doing; Relayout();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r; try { r = action(); } catch (Exception ex) { r = ex.Message; }
                string ntp = NtpLine();
                try { BeginInvoke(new Action(() => { busy = false; status = r + (ntp != "" ? " · " + ntp : ""); if (!IsDisposed) Relayout(); })); } catch { }
            });
        }
        void QueryNtp() { ThreadPool.QueueUserWorkItem(_ => { string n = NtpLine(); try { BeginInvoke(new Action(() => { status = n != "" ? n : Strings.T("clock.sync.no_data"); Relayout(); })); } catch { } }); }
        void SyncNow()
        {
            Work(Strings.T("clock.sync.working"), () =>
            {   // w32tm /resync needs admin: the fanatic-time-sync task (SYSTEM, runnable by any user) does it
                int code = Exec("schtasks.exe", "/Run /TN fanatic-time-sync"); Thread.Sleep(3000);
                return code == 0 ? Strings.T("clock.sync.requested") : Strings.T("clock.sync.error", code);
            });
        }
        void SetZone(string id)
        {
            Work(Strings.T("clock.tz.changing"), () =>
            {
                int code = Exec("tzutil.exe", "/s \"" + id + "\"");
                TimeZoneInfo.ClearCachedData();
                return code == 0 ? Strings.T("clock.tz.changed", ZoneName(id)) : Strings.T("clock.tz.error", code);
            });
        }
        // "Остання синхронізація 0:01:28 · time.windows.com" from w32tm /query /status (English output on this build).
        static string NtpLine()
        {
            string o = ExecOut("w32tm.exe", "/query /status");
            var t = Regex.Match(o, @"Last Successful Sync Time:\s*(.+)"); var s = Regex.Match(o, @"Source:\s*(.+)");
            if (!t.Success) return o.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ? Strings.T("clock.sync.no_access") : "";
            DateTime dt; string when = DateTime.TryParse(t.Groups[1].Value.Trim(), out dt) ? (dt.Date == DateTime.Today ? dt.ToString("H:mm:ss") : dt.ToString("d.MM H:mm")) : t.Groups[1].Value.Trim();
            string src = s.Success ? Regex.Replace(s.Groups[1].Value.Trim(), @",0x\w+$", "") : "";
            return src != "" ? Strings.T("clock.sync.synced_src", when, src) : Strings.T("clock.sync.synced", when);
        }
    }
    static int Exec(string exe, string args)
    {
        var p = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var pr = Process.Start(p)) { pr.StandardOutput.ReadToEnd(); return pr.WaitForExit(30000) ? pr.ExitCode : -1; }
    }
    static string ExecOut(string exe, string args)
    {
        try
        {
            var p = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var pr = Process.Start(p)) { string o = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd(); pr.WaitForExit(10000); return o; }
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ================= calendar (panel-calendar.py MonthView) =================
    // UaMonthName / UaMonthAbbr / UaDayAbbr are declared and filled in FanaticBar.cs (LoadLocalizedArrays, A9).

    // Calendar arithmetic that does not stop at DateTime's years 1..9999 (days-from-civil, proleptic Gregorian; astronomical years, 1 BC = 0).
    static long FloorDiv(long a, long b) { long q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }
    static long DaysFromCivil(long y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0; long era = FloorDiv(y, 400), yoe = y - era * 400;
        long doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        return era * 146097 + yoe * 365 + yoe / 4 - yoe / 100 + doy - 719468;
    }
    static void CivilFromDays(long z, out long y, out int m, out int d)
    {
        z += 719468; long era = FloorDiv(z, 146097), doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365, doy = doe - (365 * yoe + yoe / 4 - yoe / 100), mp = (5 * doy + 2) / 153;
        d = (int)(doy - (153 * mp + 2) / 5 + 1); m = (int)(mp + (mp < 10 ? 3 : -9)); y = yoe + era * 400 + (m <= 2 ? 1 : 0);
    }
    static int WeekdayOf(long days) { return (int)(((days + 3) % 7 + 7) % 7); }   // Monday = 0
    static int IsoWeek(long days)
    {
        long thursday = days - WeekdayOf(days) + 3; long y; int m, d; CivilFromDays(thursday, out y, out m, out d);
        return (int)((thursday - DaysFromCivil(y, 1, 1)) / 7 + 1);
    }

    sealed class CalendarPopup : SkyPopup
    {
        CalendarView view;
        protected override List<Control> Build() { if (view == null) view = new CalendarView(this); return new List<Control> { view }; }
        protected override bool EscapeHandled() { return view != null && view.Escape(); }
        protected override bool OnKey(Keys k) { return view != null && view.Key(k); }
        protected override void OnKeyPress(KeyPressEventArgs e) { if (view != null) view.Char(e.KeyChar); base.OnKeyPress(e); }
    }

    sealed class CalendarView : Control
    {
        const int ROWS = 6, COLS = 7, LIST_AROUND = 30, LIST_ROWS = ROWS + 1, MIN_YEAR = -6665, MAX_YEAR = 6767, PROLEPTIC_BEFORE = 1583;
        const float SCALE = 1.5f, CELL_ALPHA = 1f / 3, BAND_ALPHA = 0.12f;
        readonly CalendarPopup host; readonly Font font, bold;
        readonly int cw, ch, pad; readonly float radius;
        readonly long todayN; long year; int month;
        string mode = "days"; int[] hover; string hot = ""; int hotArg;
        string editText = ""; string editEra = "AD"; bool fresh; int listTop;
        readonly ToolTip tip = new ToolTip();

        public CalendarView(CalendarPopup host)
        {
            this.host = host; DoubleBuffered = true; BackColor = Background;
            font = SkyFont(11 * SCALE, false); bold = SkyFont(11 * SCALE, true);
            var sz = TextRenderer.MeasureText("00", font, Size.Empty, TextFormatFlags.NoPadding);
            cw = (int)Math.Round(sz.Width * 1.7); ch = (int)Math.Round(sz.Height * 1.5); pad = Px(8 * SCALE); radius = Px(6 * SCALE);
            Size = new Size(pad * 2 + cw * (COLS + 1), pad * 2 + ch * (ROWS + 2));
            var t = DateTime.Today; todayN = DaysFromCivil(t.Year, t.Month, t.Day); year = t.Year; month = t.Month;
        }
        protected override void Dispose(bool d) { if (d) { font.Dispose(); bold.Dispose(); tip.Dispose(); } base.Dispose(d); }

        static string YearLabel(long y, bool era) { return y > 0 ? (era ? Strings.T("cal.year_ad", y) : y.ToString()) : Strings.T("cal.year_bc", (1 - y)); }
        static string EraOf(long y) { return y > 0 ? "AD" : "BC"; }
        static long ToAstro(long n, string era) { long y = era == "AD" ? n : 1 - n; return Math.Max(MIN_YEAR, Math.Min(MAX_YEAR, y)); }
        long EditYear() { return editText == "" ? year : ToAstro(Math.Max(1, long.Parse(editText)), editEra); }
        List<long> Items() { var l = new List<long>(); long c = EditYear(); for (long y = Math.Max(MIN_YEAR, c - LIST_AROUND); y <= Math.Min(MAX_YEAR, c + LIST_AROUND); y++) l.Add(y); return l; }
        void CenterList() { var it = Items(); int at = it.IndexOf(EditYear()); listTop = Math.Max(0, Math.Min(it.Count - LIST_ROWS, at - LIST_ROWS / 2)); }

        // ---- geometry (Grid) ----
        Rectangle Cell(int week, int day) { return new Rectangle(pad + cw * (day + 1), pad + ch * (week + 2), cw, ch); }
        int[] Hit(int x, int y)
        {
            int col = (int)Math.Floor((x - pad) / (double)cw) - 1, row = (int)Math.Floor((y - pad) / (double)ch) - 2;
            if (x < pad || y < pad || row < 0 || row >= ROWS || col < 0 || col >= COLS) return null;
            return new[] { row, col };
        }
        Rectangle ArrowRect(int step) { int w = cw * 5 / 4; return new Rectangle(step < 0 ? pad : Width - pad - w, pad, w, ch); }
        RectangleF MonthRect(int i) { float w = (Width - 2 * pad) / 3f, h = ch * (ROWS + 1) / 4f; return new RectangleF(pad + w * (i % 3), pad + ch + h * (i / 3), w, h); }
        // year face: entry, then the two era buttons ("н.е." / "до н.е." need more room than AD/BC: the entry is narrower)
        Rectangle EntryRect() { return new Rectangle(pad + cw / 2, pad, cw * 3, ch); }
        Rectangle EraRect(string era) { return era == "AD" ? new Rectangle(pad + cw * 37 / 10, pad, cw * 8 / 5, ch) : new Rectangle(pad + cw * 11 / 2, pad, cw * 23 / 10, ch); }
        Rectangle ListRect(int row) { return new Rectangle(pad, pad + ch * (1 + row), Width - 2 * pad, ch); }
        int ListHit(int x, int y) { int row = (int)Math.Floor((y - pad) / (double)ch) - 1; return pad <= x && x < Width - pad && y >= pad && row >= 0 && row < LIST_ROWS ? row : -1; }

        void TitleRects(out RectangleF monthR, out RectangleF yearR)
        {
            float mw = DiabloLine(null, UaMonthName[month - 1], 0, 0, Cap(bold), Foreground);
            float yw = TextRenderer.MeasureText(YearLabel(year, false), bold, Size.Empty, TextFormatFlags.NoPadding).Width;
            float gap = TextRenderer.MeasureText(" ", bold, Size.Empty, TextFormatFlags.NoPadding).Width;
            float h = ch * 0.8f, y = pad + (ch - h) / 2;
            if (mode == "months") { monthR = RectangleF.Empty; yearR = new RectangleF((Width - yw) / 2 - 4, y, yw + 8, h); return; }
            float x = pad + (Width - 2 * pad - (mw + gap + yw)) / 2;
            monthR = new RectangleF(x - 4, y, mw + 8, h); yearR = new RectangleF(x + mw + gap - 4, y, yw + 8, h);
        }

        long[,] Weeks()
        {
            var r = new long[ROWS, COLS]; long first = DaysFromCivil(year, month, 1), start = first - WeekdayOf(first);
            for (int w = 0; w < ROWS; w++) for (int d = 0; d < COLS; d++) r[w, d] = start + 7 * w + d;
            return r;
        }

        // ---- state ----
        void SetHover(int[] cell, string h, int arg)
        {
            bool same = h == hot && arg == hotArg && ((cell == null && hover == null) || (cell != null && hover != null && cell[0] == hover[0] && cell[1] == hover[1]));
            if (!same) { hover = cell; hot = h; hotArg = arg; Invalidate(); }
        }
        void UpdateTip() { tip.SetToolTip(this, year < PROLEPTIC_BEFORE ? Strings.T("cal.tip.proleptic") : null); }
        void SetMode(string m)
        {
            mode = m; hover = null; hot = "";
            if (m == "year") { editText = (year > 0 ? year : 1 - year).ToString(); editEra = EraOf(year); fresh = true; CenterList(); }
            Invalidate();
        }
        void Go(long y, int m) { year = Math.Max(MIN_YEAR, Math.Min(MAX_YEAR, y)); if (m > 0) month = m; mode = "days"; hover = null; hot = ""; UpdateTip(); Invalidate(); }
        void Step(int delta)
        {
            long idx = year * 12 + month - 1 + delta, y = FloorDiv(idx, 12); int m = (int)(idx - y * 12) + 1;
            if (y >= MIN_YEAR && y <= MAX_YEAR) { year = y; month = m; }
            hover = null; hot = ""; UpdateTip(); Invalidate();
        }
        void StepYear(int delta) { year = Math.Max(MIN_YEAR, Math.Min(MAX_YEAR, year + delta)); hover = null; hot = ""; UpdateTip(); Invalidate(); }
        void TypeDigit(char c)
        {
            string text = fresh || editText.Length >= 4 ? c.ToString() : editText + c;
            long top = editEra == "AD" ? MAX_YEAR : 1 - MIN_YEAR;
            editText = Math.Min(long.Parse(text), top).ToString(); fresh = false; CenterList(); Invalidate();
        }
        void SetEra(string era)
        {
            editEra = era;
            if (editText != "") { long c = ToAstro(Math.Max(1, long.Parse(editText)), era); editText = (c <= 0 ? Math.Abs(c) + 1 : c).ToString(); }
            CenterList(); Invalidate();
        }

        // ---- keys (Escape in a picker goes back to the days) ----
        public void ShowMonths() { SetMode("months"); }   // --shot-fly
        public bool Escape() { if (mode == "days") return false; mode = "days"; hover = null; hot = ""; Invalidate(); return true; }
        public bool Key(Keys k)
        {
            if (mode == "days") { if (k == Keys.Left) { Step(-1); return true; } if (k == Keys.Right) { Step(1); return true; } return false; }
            if (mode == "months") { if (k == Keys.Left) StepYear(-1); else if (k == Keys.Right) StepYear(1); else return false; return true; }
            if (k == Keys.Enter) { Go(EditYear(), 0); return true; }
            if (k == Keys.Back) { if (editText.Length > 0) editText = editText.Substring(0, editText.Length - 1); fresh = false; CenterList(); Invalidate(); return true; }
            if (k == Keys.Up || k == Keys.Down)
            {
                long y = Math.Max(MIN_YEAR, Math.Min(MAX_YEAR, EditYear() + (k == Keys.Up ? -1 : 1)));
                editText = (y > 0 ? y : 1 - y).ToString(); editEra = EraOf(y); fresh = false; CenterList(); Invalidate(); return true;
            }
            return false;
        }
        public void Char(char c)
        {
            if (mode != "year") return;
            if (char.IsDigit(c)) TypeDigit(c);
            else if (c == 'a' || c == 'A' || c == 'н' || c == 'Н') SetEra("AD");
            else if (c == 'b' || c == 'B' || c == 'д' || c == 'Д') SetEra("BC");
        }

        // ---- pointer ----
        void TargetAt(int x, int y, out int[] cell, out string h, out int arg)
        {
            cell = null; h = ""; arg = 0;
            if (mode == "year")
            {
                foreach (var era in new[] { "AD", "BC" }) if (EraRect(era).Contains(x, y)) { h = "era"; arg = era == "AD" ? 0 : 1; return; }
                int row = ListHit(x, y); if (row >= 0 && listTop + row < Items().Count) { h = "row"; arg = row; }
                return;
            }
            foreach (int step in new[] { -1, 1 }) if (ArrowRect(step).Contains(x, y)) { h = "arrow"; arg = step; return; }
            RectangleF mr, yr; TitleRects(out mr, out yr);
            if (mode == "months")
            {
                if (yr.Contains(x, y)) { h = "year"; return; }
                for (int i = 0; i < 12; i++) if (MonthRect(i).Contains(x, y)) { h = "month"; arg = i; return; }
                return;
            }
            if (mr.Contains(x, y)) { h = "month-name"; return; }
            if (yr.Contains(x, y)) { h = "year"; return; }
            cell = Hit(x, y);
        }
        protected override void OnMouseMove(MouseEventArgs e) { int[] c; string h; int a; TargetAt(e.X, e.Y, out c, out h, out a); SetHover(c, h, a); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { SetHover(null, "", 0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int[] cell; string h; int a; TargetAt(e.X, e.Y, out cell, out h, out a);
            if (mode == "year") { if (h == "era") SetEra(a == 0 ? "AD" : "BC"); else if (h == "row") Go(Items()[listTop + a], 0); }
            else if (mode == "months") { if (h == "arrow") StepYear(a); else if (h == "year") SetMode("year"); else if (h == "month") Go(year, a + 1); }
            else if (h == "arrow") Step(a);
            else if (h == "month-name") SetMode("months");
            else if (h == "year") SetMode("year");
            base.OnMouseDown(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int delta = e.Delta > 0 ? -1 : e.Delta < 0 ? 1 : 0;
            if (mode == "year") { listTop = Math.Max(0, Math.Min(Items().Count - LIST_ROWS, listTop + delta)); Invalidate(); }
            else if (mode == "months") StepYear(delta); else Step(delta);
            base.OnMouseWheel(e);
        }

        // ---- drawing ----
        void Txt(Graphics g, string s, RectangleF r, Color c, bool b) { TextRenderer.DrawText(g, s, b ? bold : font, Rectangle.Round(r), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding); }
        // month and weekday names: Diablo capitals (DiabloCyr) at the cap height of the Consolas text, centred in r
        static float Cap(Font f) { return f.Height * 0.55f; }
        void DTxt(Graphics g, string s, RectangleF r, Color c, bool b) { float cap = Cap(b ? bold : font); DiabloLine(g, s, r.X + (r.Width - DiabloLine(null, s, 0, 0, cap, c)) / 2, r.Y + r.Height / 2, cap, c); }
        void Plate(Graphics g, RectangleF r, Color c, float a) { using (var p = Rounded(r, radius)) using (var b = new SolidBrush(Color.FromArgb((int)(255 * a), c))) g.FillPath(b, p); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Background);
            if (mode == "year") { DrawYear(g); return; }
            if (hover != null)
            {
                int week = hover[0], day = hover[1], top = pad + ch;
                Plate(g, new Rectangle(pad + cw * (day + 1), top, cw, ch * (ROWS + 1)), Light, BAND_ALPHA);
                Plate(g, new Rectangle(pad, pad + ch * (week + 2), cw * (COLS + 1), ch), Light, BAND_ALPHA);
                Plate(g, Cell(week, day), Light, CELL_ALPHA);
            }
            foreach (int step in new[] { -1, 1 })
            {
                var r = ArrowRect(step); if (hot == "arrow" && hotArg == step) Plate(g, r, Light, CELL_ALPHA);
                Txt(g, step < 0 ? "<" : ">", r, Muted, false);
            }
            RectangleF mr, yr; TitleRects(out mr, out yr);
            if (mode == "days")
            {
                if (hot == "month-name") Plate(g, mr, Light, CELL_ALPHA);
                if (hot == "year") Plate(g, yr, Light, CELL_ALPHA);
                DTxt(g, UaMonthName[month - 1], mr, Foreground, true); Txt(g, YearLabel(year, false), yr, Foreground, true);
            }
            else
            {
                if (hot == "year") Plate(g, yr, Light, CELL_ALPHA);
                Txt(g, YearLabel(year, false), new RectangleF(pad, pad, Width - 2 * pad, ch), Foreground, true);
                for (int i = 0; i < 12; i++)
                {
                    var r = MonthRect(i);
                    if (hot == "month" && hotArg == i) Plate(g, new RectangleF(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6), Light, CELL_ALPHA);
                    bool now = i + 1 == month; DTxt(g, UaMonthAbbr[i], r, now ? Light : Foreground, now);
                }
                return;
            }
            for (int d = 0; d < COLS; d++) DTxt(g, UaDayAbbr[d], new Rectangle(pad + cw * (d + 1), pad + ch, cw, ch), Muted, false);
            var weeks = Weeks();
            for (int w = 0; w < ROWS; w++)
            {
                Txt(g, IsoWeek(weeks[w, 0]).ToString(), new Rectangle(pad, pad + ch * (w + 2), cw, ch), SkyDim, false);
                for (int d = 0; d < COLS; d++)
                {
                    var r = Cell(w, d); long y; int m, dd; CivilFromDays(weeks[w, d], out y, out m, out dd);
                    if (weeks[w, d] == todayN) { Plate(g, r, Light, 1); Txt(g, dd.ToString(), r, Background, true); }
                    else Txt(g, dd.ToString(), r, m == month ? Foreground : SkyDim, false);
                }
            }
        }
        void DrawYear(Graphics g)
        {
            var entry = EntryRect(); Plate(g, entry, Light, BAND_ALPHA);
            int w = TextRenderer.MeasureText(g, editText, bold, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (editText != "") TextRenderer.DrawText(g, editText, bold, new Rectangle(entry.X + Px(8), entry.Y, entry.Width - Px(8), entry.Height), Foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var b = new SolidBrush(Light)) g.FillRectangle(b, entry.X + Px(8) + w + 1, entry.Y + ch * 0.2f, 2, ch * 0.6f);   // the caret
            foreach (var era in new[] { "AD", "BC" })
            {
                var r = EraRect(era); string label = era == "AD" ? Strings.T("cal.era.ad") : Strings.T("cal.era.bc");
                if (era == editEra) { Plate(g, r, Light, 1); Txt(g, label, r, Background, true); }
                else { if (hot == "era" && hotArg == (era == "AD" ? 0 : 1)) Plate(g, r, Light, CELL_ALPHA); Txt(g, label, r, Muted, false); }
            }
            var items = Items();
            for (int row = 0; row < LIST_ROWS; row++)
            {
                int at = listTop + row; if (at >= items.Count) break;
                var r = ListRect(row); if (hot == "row" && hotArg == row) Plate(g, r, Light, CELL_ALPHA);
                bool now = items[at] == EditYear(); Txt(g, YearLabel(items[at], true), r, now ? Light : Foreground, now);
            }
        }
    }

    // ================= power (powermenu.py, Windows power-mode overlays) =================
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid g);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid g);
    // { overlay-id/"auto", string-key prefix }; name/desc are localised at the menu site via Strings.T(prefix+".name"/".desc").
    static readonly string[][] PowerModes = {
        new[] { "auto", "power.mode.auto" },
        new[] { "961cc777-2547-4f9d-8174-7d86181b8a7a", "power.mode.saver" },
        new[] { "00000000-0000-0000-0000-000000000000", "power.mode.balanced" },
        new[] { "ded574b5-45a0-4f42-8737-46345c09c238", "power.mode.performance" } };
    static readonly Guid OverlayBalanced = Guid.Empty, OverlayPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

    // The chosen mode ("auto" by default, as powermenu.py's Automatic) is kept in power-mode.txt next to the exe; "auto" picks
    // the overlay from the power source and is re-applied on every AC/DC change (and checked every 30 s).
    static string PowerModeFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "power-mode.txt"); } }
    static string SavedPowerMode()
    {
        try { var m = File.Exists(PowerModeFile) ? File.ReadAllText(PowerModeFile).Trim() : "auto"; foreach (var p in PowerModes) if (p[0] == m) return m; } catch { }
        return "auto";
    }
    static Guid CurrentOverlay() { Guid g; return PowerGetEffectiveOverlayScheme(out g) == 0 ? g : Guid.Empty; }
    static Guid WantedOverlay(string mode) { return mode == "auto" ? (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online ? OverlayPerformance : OverlayBalanced) : new Guid(mode); }
    static readonly object powerLock = new object();
    static bool ApplyPowerMode(string mode)
    {
        lock (powerLock)
        {
            Guid want = WantedOverlay(mode);
            if (CurrentOverlay() == want) return true;
            uint r = PowerSetActiveOverlayScheme(want);
            if (r != 0) Log("power overlay " + want + ": 0x" + r.ToString("X")); else Log("power mode " + mode + " -> overlay " + want);
            return r == 0;
        }
    }
    static System.Threading.Timer StartAutoPower()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) ThreadPool.QueueUserWorkItem(_ => ApplyPowerMode(SavedPowerMode())); };
        return new System.Threading.Timer(_ => { try { ApplyPowerMode(SavedPowerMode()); } catch (Exception ex) { Log("power: " + ex.Message); } }, null, 2000, 30000);
    }

    // Keyboard backlight: the EC (0xD3) is reachable only by the elevated fan-tray; the bar writes kbd-request.txt here,
    // fan-tray applies it (FileSystemWatcher) and keeps kbd-state.txt up to date (0 = off, 1..3).
    static string KbdFile(string n) { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, n); }
    static int KbdLevel() { try { int v; return int.TryParse(File.ReadAllText(KbdFile("kbd-state.txt")).Trim(), out v) && v >= 0 && v <= 3 ? v : -1; } catch { return -1; } }

    sealed class PowerPopup : SkyPopup
    {
        readonly List<SkyButton> modes = new List<SkyButton>(); bool busy;
        static string BatteryLine()
        {
            var ps = SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0) return Strings.T("battery.none");
            int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
            string st = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0 ? Strings.T("battery.state.charging")
                : ps.PowerLineStatus == PowerLineStatus.Online ? (pct >= 100 ? Strings.T("battery.state.full") : Strings.T("battery.state.plugged_not_charging"))
                : (ps.BatteryLifeRemaining > 0 ? Strings.T("battery.state.discharging_remaining", (ps.BatteryLifeRemaining / 3600), (ps.BatteryLifeRemaining % 3600 / 60)) : Strings.T("battery.state.discharging"));
            return Strings.T("battery.summary", pct, st);
        }
        protected override List<Control> Build()
        {
            var rows = new List<Control>(); modes.Clear();
            rows.Add(RowLabel(BatteryLine()));
            rows.Add(new BrightnessRow());
            // Keyboard backlight (A6): off by default — it needs the MSI-EC fan-tray bridge. Enable per machine with
            // "keyboardBacklight": true in fanatic-bar.json once the hardware/fan-tray is present.
            if (Setting("keyboardBacklight", false)) rows.Add(new KbdRow());
            string saved = SavedPowerMode();
            foreach (var m in PowerModes)
            {
                var b = new SkyButton(Strings.T(m[1] + ".name"), null) { Sub = Strings.T(m[1] + ".desc"), Checked = m[0] == saved, Tag = m[0] };
                b.Size = b.Preferred(); b.Click += (s, e) => Choose((SkyButton)s); modes.Add(b); rows.Add(b);
            }
            return rows;
        }
        void Choose(SkyButton b)
        {
            if (busy || b.Checked) return;   // a click on the active mode keeps it
            busy = true; foreach (var x in modes) x.Enabled = false;
            var mode = (string)b.Tag;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = ApplyPowerMode(mode);
                if (ok) try { File.WriteAllText(PowerModeFile, mode); } catch (Exception ex) { Log("power mode save: " + ex.Message); }
                string now = SavedPowerMode();
                try { BeginInvoke(new Action(() => { busy = false; foreach (var x in modes) { x.Enabled = true; x.Checked = (string)x.Tag == now; x.Invalidate(); } })); } catch { }
            });
        }
    }

    // "Підсвітка клавіатури": off / 1 / 2 / 3 as pills (EC levels of fan-tray's backlight menu).
    sealed class KbdRow : Control
    {
        readonly SkyButton[] pills = new SkyButton[4];
        public KbdRow()
        {
            BackColor = Background; var label = new Label { Text = Strings.T("power.kbd_backlight.title"), ForeColor = Muted, BackColor = Background, Font = SkyFont(9, false), AutoSize = true, Location = new Point(0, 0) };
            Controls.Add(label); int y = label.PreferredHeight + Px(4), x = 0, lv = KbdLevel();
            string[] names = { Strings.T("power.kbd_backlight.off"), "1", "2", "3" };
            for (int i = 0; i < 4; i++)
            {
                int level = i; var b = new SkyButton(names[i], null) { Checked = i == lv, Enabled = lv >= 0 };
                b.Size = new Size(Math.Max(b.Preferred().Width, Px(64)), b.Preferred().Height); b.Location = new Point(x, y); x += b.Width + Px(8);
                b.Click += (s, e) =>
                {
                    try   // written aside and renamed into place: fan-tray never reads a half-written request
                    {
                        string tmp = KbdFile("kbd-request.tmp"), req = KbdFile("kbd-request.txt");
                        File.WriteAllText(tmp, level.ToString());
                        if (File.Exists(req)) File.Replace(tmp, req, null); else File.Move(tmp, req);
                    }
                    catch (Exception ex) { Log("kbd request: " + ex.Message); return; }
                    foreach (var p in pills) { p.Checked = p == s; p.Invalidate(); }
                };
                pills[i] = b; Controls.Add(b);
            }
            if (lv < 0) Controls.Add(new Label { Text = Strings.T("power.kbd_backlight.no_link"), ForeColor = Muted, BackColor = Background, Font = SkyFont(9, false), AutoSize = true, Location = new Point(x + Px(4), y + Px(8)) });
            Size = new Size(x, y + pills[0].Height);
        }
    }
    // Icon + slider, as the sound menu's: screen brightness through WMI (WmiMonitorBrightness / WmiSetBrightness).
    sealed class BrightnessRow : Control
    {
        const int Min = 1;   // 0 is a black screen with no way to see the slider
        int value = 50; bool known, drag; System.Windows.Forms.Timer pending;
        public BrightnessRow()
        {
            DoubleBuffered = true; BackColor = Background; Font = SkyFont(11, false);
            Size = new Size(Px(32) + Px(12) + Px(320) + Px(12) + TextRenderer.MeasureText("100", Font).Width, Px(32));
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int v = -1;
                try { using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness")) foreach (ManagementObject o in s.Get()) { v = Convert.ToInt32(o["CurrentBrightness"]); break; } }
                catch (Exception ex) { Log("brightness: " + ex.Message); }
                try { BeginInvoke(new Action(() => { if (v >= 0) { value = Math.Max(Min, v); known = true; } Invalidate(); })); } catch { }
            });
        }
        Rectangle Track() { int x0 = Px(32) + Px(12); return new Rectangle(x0, (Height - Px(8)) / 2, Px(320), Px(8)); }
        void SetFromX(int x)
        {
            var t = Track(); int v = Math.Max(Min, Math.Min(100, (int)Math.Round(Min + (x - t.X) * (100.0 - Min) / t.Width)));
            if (v == value) return; value = v; Invalidate();
            if (pending == null) { pending = new System.Windows.Forms.Timer { Interval = 60 }; pending.Tick += (s, e) => { pending.Stop(); Write(value); }; }
            if (!pending.Enabled) pending.Start();   // one write per 60 ms at most
        }
        static void Write(int v)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                        foreach (ManagementObject o in s.Get()) { o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v }); break; }
                }
                catch (Exception ex) { Log("brightness set: " + ex.Message); }
            });
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (known && e.Button == MouseButtons.Left) { drag = true; SetFromX(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { if (known) { var t = Track(); SetFromX(t.X + (int)((Math.Max(Min, Math.Min(100, value + (e.Delta > 0 ? 5 : -5))) - Min) * t.Width / (100.0 - Min))); } base.OnMouseWheel(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var f = new Font(IconFont, Px(22), GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "\uE706", f, new Rectangle(0, 0, Px(32), Height), known ? Foreground : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var t = Track();
            using (var p = Rounded(t, t.Height / 2f)) using (var b = new SolidBrush(SkySurface)) using (var pen = new Pen(SkyBorder)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            float fx = t.X + (value - Min) * t.Width / (100f - Min);
            if (known) using (var p = Rounded(new RectangleF(t.X, t.Y, fx - t.X, t.Height), t.Height / 2f)) using (var b = new SolidBrush(Light)) g.FillPath(b, p);
            float k = Px(22); using (var b = new SolidBrush(known ? Foreground : Muted)) g.FillEllipse(b, fx - k / 2, (Height - k) / 2, k, k);
            TextRenderer.DrawText(g, known ? value.ToString() : "—", Font, new Rectangle(t.Right + Px(12), 0, Width - t.Right - Px(12), Height), Foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ================= weather: 10 days from Open-Meteo, symbols of Render-Weather, names of $WMO_UA =================
    // The rendered icons sit on black with wide margins: cut a square around everything brighter than near-black.
    static Bitmap CropDark(Bitmap src)
    {
        int x0 = src.Width, y0 = src.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                if (c.R + c.G + c.B > 30) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            }
        if (x1 < 0) return src;
        int side = Math.Max(x1 - x0, y1 - y0) + 3, cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        var r = Rectangle.Intersect(new Rectangle(cx - side / 2, cy - side / 2, side, side), new Rectangle(0, 0, src.Width, src.Height));
        var dst = new Bitmap(side, side);
        using (var g = Graphics.FromImage(dst)) { g.Clear(Color.Black); g.DrawImage(src, new Rectangle(r.X - (cx - side / 2), r.Y - (cy - side / 2), r.Width, r.Height), r, GraphicsUnit.Pixel); }
        src.Dispose(); return dst;
    }    // one forecast hour (owner 26.10: hourly for 24 h instead of 10 days)
    sealed class HourForecast { public DateTime Time; public int Code; public double Temp; public bool Day = true; }
    // WMO weather code -> Meteocons icon (rendered once to weather-icons\png\*.png on black, 128 px).
    static readonly Dictionary<string, Image> meteocons = new Dictionary<string, Image>();
    static Image MeteoconFor(int code, bool day = true)
    {
        // night: a clear / partly cloudy hour uses the *-night picture when the set has one
        if (!day && code <= 2) { var night = WeatherPng(code == 0 ? "clear-night" : "partly-cloudy-night"); if (night != null) return night; }
        string n = code == 0 ? "clear-day" : code <= 2 ? "partly-cloudy-day" : code == 3 ? "overcast" : code == 45 || code == 48 ? "fog"
            : code >= 51 && code <= 55 ? "drizzle" : code == 56 || code == 57 || code == 66 || code == 67 ? "sleet" : code >= 61 && code <= 65 ? "rain"
            : code >= 71 && code <= 77 ? "snow" : code == 80 || code == 81 ? "partly-cloudy-day-rain" : code == 82 ? "rain"
            : code == 85 || code == 86 ? "partly-cloudy-day-snow" : code == 95 ? "thunderstorms" : code == 96 || code == 99 ? "thunderstorms-rain" : "not-available";
        return WeatherPng(n);
    }
    static Image WeatherPng(string n)
    {
        Image img;
        if (!meteocons.TryGetValue(n, out img))
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weather-icons", "png", n + ".png");
            try { img = File.Exists(p) ? CropDark(new Bitmap(new MemoryStream(File.ReadAllBytes(p)))) : null; } catch { img = null; }
            meteocons[n] = img;
        }
        return img;
    }
    static List<HourForecast> forecast; static DateTime forecastAt = DateTime.MinValue; static string forecastError;

    static List<HourForecast> FetchForecast()
    {
        EnsureLocation();   // first run / offline start: resolve the place before asking for its weather
        if (!HasLocation) throw new Exception(Strings.T("geo.unknown"));
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using (var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 })
        {
            // the next 24 hours from the current one, in the place's own time zone
            string js = wc.DownloadString("https://api.open-meteo.com/v1/forecast?" + GeoQuery + "&hourly=weather_code,temperature_2m,is_day&forecast_hours=24&timezone=auto");
            Func<string, string[]> arr = name => { var m = Regex.Match(js, "\"" + name + "\"\\s*:\\s*\\[([^\\]]*)\\]"); return m.Success ? m.Groups[1].Value.Split(',') : new string[0]; };
            var t = arr("time"); var c = arr("weather_code"); var tp = arr("temperature_2m"); var dy = arr("is_day");
            var res = new List<HourForecast>();
            for (int i = 0; i < t.Length && i < c.Length && i < tp.Length; i++)
            {
                if (c[i].Trim() == "null" || tp[i].Trim() == "null") continue;
                res.Add(new HourForecast { Time = DateTime.ParseExact(t[i].Trim().Trim('"'), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), Code = int.Parse(c[i].Trim()),
                    Temp = double.Parse(tp[i].Trim(), CultureInfo.InvariantCulture), Day = i >= dy.Length || dy[i].Trim() != "0" });
            }
            return res;
        }
    }

    // "See more" opens the default browser to a weather page for the current location (generic — works for any place).
    static void OpenWeatherPage()
    {
        string q = Uri.EscapeDataString((string.IsNullOrEmpty(geoPlace) ? "" : geoPlace + " ") + "weather");
        Open("https://www.google.com/search?q=" + q);
    }

    sealed class WeatherPopup : SkyPopup
    {
        bool loading = forecast == null || (DateTime.Now - forecastAt).TotalMinutes > 30;
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (loading)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { var f = FetchForecast(); forecast = f; forecastAt = DateTime.Now; forecastError = null; }
                    catch (Exception ex) { forecastError = ex.Message; Log("forecast: " + ex.Message); }
                    try { BeginInvoke(new Action(() => { loading = false; if (!IsDisposed) Relayout(); })); } catch { loading = false; }
                });
            }
        }
        protected override List<Control> Build()
        {
            var rows = new List<Control>();
            rows.Add(MakeLabel(Strings.T("weather.forecast.title", GeoPlace), Foreground, 11, true));
            // manual location refresh (A5): resolve by IP, then refetch the forecast + re-render the tray weather.
            rows.Add(Button(Strings.T("geo.update"), "", () =>
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool ok = ResolveByIp();
                    if (ok) { try { forecast = FetchForecast(); forecastAt = DateTime.Now; forecastError = null; } catch (Exception ex) { forecastError = ex.Message; Log("forecast: " + ex.Message); } }
                    try { ui.Post(__ => RenderWeather(), null); } catch { }
                    try { BeginInvoke(new Action(() => { if (!IsDisposed) Relayout(); })); } catch { }
                });
            }));
            if (forecast != null && forecast.Count > 0) rows.Add(new HourList(forecast));
            var status = RowLabel(loading ? Strings.T("weather.loading") : forecast == null ? (forecastError != null ? Strings.T("weather.no_data_err", forecastError) : Strings.T("weather.no_data"))
                : Strings.T("weather.updated", forecastAt.ToString("H:mm")));
            // footer: the status on the left, the "more" link on the right (a web weather page for the current place)
            var link = new LinkLabel { Text = Strings.T("weather.more") + " ↗", Font = SkyFont(9, false), AutoSize = true, BackColor = Background, LinkColor = Light, ActiveLinkColor = Foreground, LinkBehavior = LinkBehavior.HoverUnderline };
            link.LinkClicked += (s, e) => { Close(); OpenWeatherPage(); };
            var footer = new Panel { BackColor = Background, Height = Math.Max(status.Height, link.PreferredHeight) };
            status.Location = new Point(0, 0); footer.Controls.Add(status); footer.Controls.Add(link);
            footer.Width = status.Width + Px(24) + link.PreferredWidth;
            footer.Resize += (s, e) => link.Location = new Point(footer.Width - link.PreferredWidth, 0);
            link.Location = new Point(footer.Width - link.PreferredWidth, 0);
            rows.Add(footer);
            return rows;
        }
    }

    // 24 forecast hours, 10 visible, mouse wheel scrolls (owner 26.10). One owner-drawn control: no child rows, a thin
    // themed scroll bar instead of the grey system one. Row: time, weather picture, name, temperature (colour by sign).
    sealed class HourList : Control
    {
        const int Rows = 10;
        readonly List<HourForecast> hours; readonly Font font, bold; readonly int rowH, timeW; int top;
        public HourList(List<HourForecast> hours)
        {
            this.hours = hours; DoubleBuffered = true; BackColor = Background; font = SkyFont(11, false); bold = SkyFont(11, true);
            SetStyle(ControlStyles.Selectable | ControlStyles.UserMouse, true);
            rowH = Px(36); timeW = TextRenderer.MeasureText("00:00", bold, Size.Empty, TextFormatFlags.NoPadding).Width + Px(16);
            Size = new Size(timeW + Px(30) + Px(12) + Px(190) + Px(70) + Px(12), rowH * Math.Min(Rows, hours.Count));
        }
        protected override void Dispose(bool disp) { if (disp) { font.Dispose(); bold.Dispose(); } base.Dispose(disp); }
        int MaxTop { get { return Math.Max(0, hours.Count - Rows); } }
        void ScrollTo(int t) { t = Math.Max(0, Math.Min(MaxTop, t)); if (t != top) { top = t; Invalidate(); } }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }   // the wheel goes to the focused control
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(top - Math.Sign(e.Delta) * 2); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) ScrollTo(top + 1); else if (e.KeyCode == Keys.Up) ScrollTo(top - 1);
            else if (e.KeyCode == Keys.PageDown) ScrollTo(top + Rows); else if (e.KeyCode == Keys.PageUp) ScrollTo(top - Rows);
            base.OnKeyDown(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)   // a click on the scroll track pages up / down
        {
            base.OnMouseDown(e); if (MaxTop == 0 || e.X < Width - Px(12)) return;
            ScrollTo(e.Y < Height / 2 ? top - Rows : top + Rows);
        }
        static Color TempColour(double t) { return t > 0 ? Warning : t < 0 ? Light : TextCol; }
        static string T(double t) { int r = (int)Math.Round(t); return (r > 0 ? "+" : "") + r + "°"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int r = 0; r < Rows && top + r < hours.Count; r++)
            {
                var h = hours[top + r]; int y = r * rowH; bool now = top + r == 0;
                TextRenderer.DrawText(g, h.Time.ToString("HH:mm"), now ? bold : font, new Rectangle(Px(4), y, timeW, rowH), now ? Light : h.Day ? Foreground : Muted, f);
                var pic = MeteoconFor(h.Code, h.Day);
                if (pic != null) g.DrawImage(pic, timeW - Px(2), y + (rowH - Px(34)) / 2, Px(34), Px(34));
                else using (var icon = DrawWeatherSymbol(Px(28), h.Code, false)) g.DrawImage(icon, timeW + (Px(30) - Px(28)) / 2, y + (rowH - Px(28)) / 2);
                TextRenderer.DrawText(g, WmoName(h.Code), font, new Rectangle(timeW + Px(30) + Px(12), y, Px(190), rowH), TextCol, f);
                TextRenderer.DrawText(g, T(h.Temp), bold, new Rectangle(Width - Px(70) - Px(12), y, Px(70), rowH), TempColour(h.Temp), f | TextFormatFlags.Right);
            }
            if (MaxTop > 0)
            {   // thin themed scroll bar on the right edge
                int track = Height - Px(8), thumb = Math.Max(Px(18), track * Rows / hours.Count), ty = Px(4) + (track - thumb) * top / MaxTop, x = Width - Px(5);
                using (var b = new SolidBrush(Color.FromArgb(70, Muted))) g.FillRectangle(b, x, Px(4), Px(3), track);
                using (var b = new SolidBrush(Light)) g.FillRectangle(b, x, ty, Px(3), thumb);
            }
        }
    }

}
