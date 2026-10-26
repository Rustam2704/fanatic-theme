// "sky-stars.exe --settings": a small window in the .sky-menu style of the bar's flyouts, opened from the desktop's context
// menu ("Небо…", HKCU\Software\Classes\DesktopBackground\Shell\SkyStars). Every click saves sky-stars.json and signals
// fanatic.SkyStars.Reload; the running sky applies it at once. Closes on Escape, on focus loss or with a click outside.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

static partial class SkyStars
{
    const string ReloadEvent = "fanatic.SkyStars.Reload";

    sealed class SettingsWindow : Form
    {
        static readonly Color Bg = Color.Black, Border = Hex("#1e262c"), Surface = Hex("#12171a"), Accent = Hex("#0d8ecb"), Fg = Hex("#dfe8ee"), Muted = Hex("#5b6b76"), Light = Hex("#48daf9");
        // presets of the strength rows: (string-key, line level) / (string-key, field dim, glow dim) / (string-key, milky dim)
        static readonly object[][] LineLevels = { new object[] { "sky.line_level.weak", 0.08 }, new object[] { "sky.line_level.medium", 0.16 }, new object[] { "sky.line_level.bright", 0.28 } };
        static readonly object[][] StarLevels = { new object[] { "sky.star_level.pale", 0.5, 0.7 }, new object[] { "sky.star_level.medium", 0.62, 0.85 }, new object[] { "sky.star_level.bright", 0.75, 1.0 } };
        static readonly object[][] MwLevels = { new object[] { "sky.mw_level.dim", 0.2 }, new object[] { "sky.mw_level.medium", 0.4 }, new object[] { "sky.mw_level.bright", 0.7 } };
        // Languages = every strings\<code>.json next to the exe, named by its own "lang.self" key: adding a language is
        // dropping one translated file there (see TRANSLATE.md), no code change. en, ua, ru first, the rest by code.
        static string[][] LangOptions()
        {
            var list = new List<string[]>();
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(System.IO.Path.Combine(Dir, "strings"), "*.json"))
                {
                    string code = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    var m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(f, System.Text.Encoding.UTF8), "\"lang\\.self\"\\s*:\\s*\"([^\"]*)\"");
                    list.Add(new[] { code, m.Success ? m.Groups[1].Value : code });
                }
            }
            catch { }
            if (list.Count == 0) list.Add(new[] { "en", "English" });
            string[] first = { "en", "ua", "ru" };
            list.Sort((x, y) => { int a = Array.IndexOf(first, x[0]), b = Array.IndexOf(first, y[0]); a = a < 0 ? 99 : a; b = b < 0 ? 99 : b; return a != b ? a.CompareTo(b) : string.CompareOrdinal(x[0], y[0]); });
            return list.ToArray();
        }
        const int M = 16;
        readonly List<Pill> pills = new List<Pill>();
        int y;

        public static void Run()
        {
            bool created;
            using (new Mutex(true, "fanatic.SkyStars.Settings", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles(); LoadSettings();
                Application.Run(new SettingsWindow());
            }
        }

        // "--shot-settings out.png": the window rendered off-screen (never shown), for checks and docs.
        public static void Shot(string file)
        {
            LoadSettings();
            using (var w = new SettingsWindow())
            {
                var h = w.Handle;   // a never-shown form does not paint its children: force their handles and draw each in place
                using (var bmp = new Bitmap(w.Width, w.Height))
                {
                    w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
                    foreach (Control c in w.Controls) { var hc = c.Handle; c.DrawToBitmap(bmp, c.Bounds); }
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
        SettingsWindow()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Bg; KeyPreview = true; DoubleBuffered = true; Font = new Font("Consolas", 11f); AutoScaleMode = AutoScaleMode.None;
            Rebuild(true);
        }

        // Build (or rebuild, after a language switch) the rows. place=true also positions the window near the cursor.
        void Rebuild(bool place)
        {
            SuspendLayout();
            Controls.Clear(); pills.Clear(); y = M;
            Header(Strings.T("sky.header"));
            Toggle(Strings.T("sky.lines_on"), () => showLines, v => showLines = v);
            Choice(Strings.T("sky.line_brightness"), LineLevels, i => Math.Abs(lineLevel - (double)LineLevels[i][1]) < 0.001, i => lineLevel = (double)LineLevels[i][1]);
            Toggle(Strings.T("sky.labels_on"), () => showLabels, v => showLabels = v);
            Toggle(Strings.T("sky.label_bg"), () => labelBg, v => labelBg = v);   // hideable black plate behind the names
            Choice(Strings.T("sky.stars"), StarLevels, i => Math.Abs(fieldDim - (double)StarLevels[i][1]) < 0.001, i => { fieldDim = (double)StarLevels[i][1]; glowDim = (double)StarLevels[i][2]; });
            Toggle(Strings.T("sky.milkyway_on"), () => showMilkyway, v => showMilkyway = v);
            Choice(Strings.T("sky.milkyway_brightness"), MwLevels, i => Math.Abs(milkywayDim - (double)MwLevels[i][1]) < 0.001, i => milkywayDim = (double)MwLevels[i][1]);
            LangRow();
            var note = new Label { Text = Strings.T("sky.note_applied"), ForeColor = Muted, BackColor = Bg, Font = new Font("Consolas", 9f), AutoSize = true, Location = new Point(M, y) };
            Controls.Add(note); y += note.PreferredHeight + M;
            int w = 0; foreach (Control c in Controls) w = Math.Max(w, c.Right);
            ClientSize = new Size(w + M, y);
            if (place)
            {
                var cur = Cursor.Position; var wa = Screen.FromPoint(cur).WorkingArea;
                Location = new Point(Math.Max(wa.Left + 8, Math.Min(cur.X, wa.Right - Width - 8)), Math.Max(wa.Top + 8, Math.Min(cur.Y, wa.Bottom - Height - 8)));
            }
            using (var p = Round(new RectangleF(0, 0, Width, Height), 8)) Region = new Region(p);
            ResumeLayout();
        }

        // Language picker (A9): switches the theme's language. Writes the shared language file and signals the sky and the bar.
        void LangRow()
        {
            var l = new Label { Text = Strings.T("sky.language"), ForeColor = Muted, BackColor = Bg, Font = new Font("Consolas", 9f), AutoSize = true, Location = new Point(M, y) };
            Controls.Add(l); y += l.PreferredHeight + 4;
            int x = M; var row = new List<Pill>();
            var langs = LangOptions();
            for (int i = 0; i < langs.Length; i++)
            {
                string code = langs[i][0]; var p = new Pill(langs[i][1], false) { Location = new Point(x, y) }; p.Size = p.Preferred();
                p.Checked = Strings.Lang == code;
                p.Click += (s, e) => { Strings.SaveSharedLang(code); Strings.Load(code); SignalBarLang(); Apply(); BeginInvoke(new Action(() => Rebuild(false))); };
                Controls.Add(p); row.Add(p); pills.Add(p); x += p.Width + 8;
            }
            y += row[0].Height + 12;
        }
        static void SignalBarLang()
        {
            try { EventWaitHandle ev; if (EventWaitHandle.TryOpenExisting("fanatic.Bar.ReloadLang", out ev)) using (ev) ev.Set(); } catch { }
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); BeginInvoke(new Action(Close)); }
        protected override bool ProcessCmdKey(ref Message msg, Keys k) { if (k == Keys.Escape) { Close(); return true; } return base.ProcessCmdKey(ref msg, k); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8)) using (var pen = new Pen(Border)) e.Graphics.DrawPath(pen, p);
        }

        void Header(string t)
        {
            var l = new Label { Text = t, ForeColor = Fg, BackColor = Bg, Font = new Font("Consolas", 11f, FontStyle.Bold), AutoSize = true, Location = new Point(16, y) };
            Controls.Add(l); y += l.PreferredHeight + 12;
        }
        void Toggle(string t, Func<bool> get, Action<bool> set)
        {
            var p = new Pill(t, true) { Location = new Point(16, y) }; p.Size = new Size(Math.Max(300, p.Preferred().Width), p.Preferred().Height);
            p.Checked = get(); p.Click += (s, e) => { set(!get()); p.Checked = get(); Apply(); };
            Controls.Add(p); pills.Add(p); y += p.Height + 12;
        }
        void Choice(string t, object[][] options, Func<int, bool> isOn, Action<int> pick)
        {
            var l = new Label { Text = t, ForeColor = Muted, BackColor = Bg, Font = new Font("Consolas", 9f), AutoSize = true, Location = new Point(16, y) };
            Controls.Add(l); y += l.PreferredHeight + 4;
            int x = 16; var row = new List<Pill>();
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i; var p = new Pill(Strings.T((string)options[i][0]), false) { Location = new Point(x, y) }; p.Size = p.Preferred();
                p.Checked = isOn(i);
                p.Click += (s, e) => { pick(idx); for (int j = 0; j < row.Count; j++) row[j].Checked = isOn(j); foreach (var q in row) q.Invalidate(); Apply(); };
                Controls.Add(p); row.Add(p); pills.Add(p); x += p.Width + 8;
            }
            y += row[0].Height + 12;
        }
        static void Apply()
        {
            try { SaveSettings(); } catch (Exception ex) { Log("settings save: " + ex.Message); return; }
            EventWaitHandle ev; if (EventWaitHandle.TryOpenExisting(ReloadEvent, out ev)) using (ev) ev.Set();
        }

        // .sky-menu button: surface plate, border, hover border light; checked = accent plate, black text. Toggles show a switch.
        sealed class Pill : Control
        {
            public bool Checked; readonly bool isSwitch; bool hover;
            public Pill(string text, bool isSwitch) { Text = text; this.isSwitch = isSwitch; DoubleBuffered = true; BackColor = Color.Black; Font = new Font("Consolas", 11f); Cursor = Cursors.Hand; }
            // measured with the same flags it is drawn with (the default padding made the plate wider than the text needs)
            public Size Preferred() { var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding); return new Size(t.Width + 28 + (isSwitch ? 52 : 0), t.Height + 14); }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                bool plate = Checked && !isSwitch;
                using (var p = Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8)) using (var b = new SolidBrush(plate ? Accent : Surface)) using (var pen = new Pen(hover ? Light : Border))
                { g.FillPath(b, p); g.DrawPath(pen, p); }
                // choice pills: text centred on the plate (owner 26.10); toggles: text left of the switch
                var tr = isSwitch ? new Rectangle(14, 0, Width - 14 - 52, Height) : new Rectangle(0, 0, Width, Height);
                TextRenderer.DrawText(g, Text, Font, tr, plate ? Color.Black : Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | (isSwitch ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
                if (isSwitch)
                {   // GTK-like switch: track accent when on, knob foreground
                    var r = new RectangleF(Width - 12 - 40, (Height - 20) / 2f, 40, 20);
                    using (var p = Round(r, 10)) using (var b = new SolidBrush(Checked ? Accent : Surface)) using (var pen = new Pen(Border)) { g.FillPath(b, p); g.DrawPath(pen, p); }
                    using (var b = new SolidBrush(Fg)) g.FillEllipse(b, Checked ? r.Right - 18 : r.X + 2, r.Y + 2, 16, 16);
                }
            }
        }
    }
    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = 2 * rad;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}
