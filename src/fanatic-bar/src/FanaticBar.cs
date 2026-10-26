// fanatic bar: the right-hand indicators of the whitebook panel as tray icons (one NotifyIcon per slot),
// shapes and colours ported 1:1 from the prototype's bar.ps1. Click opens the native Windows flyout.
// Runs as the user (not elevated). Data is read on timer threads; only icon/text updates go to the UI thread.
// "fanatic-bar.exe --shot out.png" renders every indicator state into a preview PNG and exits.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Windows.Networking.Connectivity;

static partial class FanaticBar
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int i, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)] struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    // whitebook theme (bar.ps1 $THEME)
    static readonly Color Light = Hex("#48daf9"), Success = Hex("#28c840"), Warning = Hex("#febc2e"), Danger = Hex("#ff5f57"),
        Foreground = Hex("#dfe8ee"), TextCol = Hex("#c6d0d9"), Muted = Hex("#5b6b76"), Background = Hex("#000000"),
        FanBase = Hex("#21343d"), BatDead = Hex("#23404a"), Violet = Hex("#8a4dff"), TerminalBlue = Hex("#348CD2"), Orange = Hex("#e0772a");
    static Color Hex(string h) { return ColorTranslator.FromHtml(h); }
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    const string QuitEvent = "fanatic.Bar.Quit";
    const string ControlCenter ="ms-actioncenter:controlcenter/&showFooter=true";

    // Draw = the last picture (redrawn when the pointer comes or goes: Hot = full brightness, otherwise dimmed, owner batch 4)
    class Slot { public NotifyIcon Ni; public string Key = ""; public Func<int, Bitmap> Draw; public bool Hot; }
    static SynchronizationContext ui;
    static Slot wifi, ping, cpu, ram, wSym, wTemp, bat, batPct, audio, gear, day, week, hours, minutes;

    static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanatic-bar.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + m + Environment.NewLine); } catch { }
    }

    // Tray icon size at the TASKBAR's current DPI (per-monitor): after a live change of the display scale (125 % <-> 150 %) the
    // system DPI of the session stays the old one, and an icon drawn for it is stretched by the shell (faded, blurred).
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    static uint TaskbarDpi()
    {
        IntPtr tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        uint d = tray != IntPtr.Zero ? GetDpiForWindow(tray) : 0;
        return d > 0 ? d : GetDpiForSystem();
    }
    static int IconSize() { int s = GetSystemMetricsForDpi(49, TaskbarDpi()); return s > 0 ? s : 16; }
    // Checked every 2 s: a new icon size redraws every slot (new HICONs, no restart).
    static int shownIconSize;
    static void CheckIconSize()
    {
        int s = IconSize();
        if (shownIconSize == 0) { shownIconSize = s; return; }
        if (s == shownIconSize) return;
        Log("tray icon size " + shownIconSize + " -> " + s + " px (display scale changed): redrawing");
        shownIconSize = s;
        ui.Post(_ =>
        {
            if (gear != null) { var old = gear.Ni.Icon; using (var b = DrawGear(s)) gear.Ni.Icon = ToIcon(b); if (old != null) old.Dispose(); }
            RefreshAllIcons();
        }, null);
    }

    static Icon ToIcon(Bitmap bmp) { IntPtr h = bmp.GetHicon(); var icon = (Icon)Icon.FromHandle(h).Clone(); DestroyIcon(h); return icon; }

    // Post a new picture/tooltip to a slot; redraws only when key (picture state + tooltip) changes.
    static void Show(Slot s, string key, Func<int, Bitmap> draw, string tip)
    {
        // the picture is redrawn only when its data (key) changes; a new tooltip text alone just replaces the text
        ui.Post(_ =>
        {
            if (key != s.Key) { s.Key = key; s.Draw = draw; Paint(s); }
            string t = tip.Length > 63 ? tip.Substring(0, 63) : tip;   // NotifyIcon.Text throws above 63 chars
            if (s.Ni.Text != t) s.Ni.Text = t;
        }, null);
    }

    // ---- brightness on hover (owner, batch 4): indicators at 78 % brightness, the one under the pointer at 100 %, no veil ----
    const float DimLevel = 0.78f;
    // Flyouts / menus / tooltips: see-through background, solid text, like the terminal at opacity < 100 without acrylic.
    // A flat Form.Opacity faded the text too, and the DWM acrylic backdrop blurred the sky's stars away (prototype 25.10, b9-*).
    // ACCENT_ENABLE_TRANSPARENTGRADIENT turns the pure-black Background into GlassTint over the desktop; anything else stays solid.
    const int GlassTint = unchecked((int)0xB0000000);   // AABBGGRR: black, alpha 0xB0 (69 %); 0x99 is more see-through
    const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    static void Glass(IntPtr h)
    {
        int round = 2; DwmSetWindowAttribute(h, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);   // DWMWCP_ROUND
        Accent(h, new AccentPolicy { State = ACCENT_ENABLE_TRANSPARENTGRADIENT, Flags = 2, Color = GlassTint });
    }
    static void Paint(Slot s)
    {
        if (s.Draw == null) return;
        var bmp = s.Draw(IconSize());
        if (!s.Hot) Dim(bmp);
        var old = s.Ni.Icon; s.Ni.Icon = ToIcon(bmp); bmp.Dispose(); if (old != null) old.Dispose();
    }
    // RGB x DimLevel, alpha kept (premultiplied-looking result on the black taskbar = the same picture, darker)
    static void Dim(Bitmap bmp)
    {
        using (var copy = (Bitmap)bmp.Clone()) using (var g = Graphics.FromImage(bmp)) using (var ia = new System.Drawing.Imaging.ImageAttributes())
        {
            var cm = new System.Drawing.Imaging.ColorMatrix(); cm.Matrix00 = cm.Matrix11 = cm.Matrix22 = DimLevel;
            ia.SetColorMatrix(cm);
            g.Clear(Color.Transparent); g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(copy, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
        }
    }
    static Slot hotSlot;
    static void HoverSlot(Slot s)
    {
        if (hotSlot == s) return;
        var old = hotSlot; hotSlot = s;
        if (old != null) { old.Hot = false; Paint(old); }
        s.Hot = true; Paint(s);
    }
    // NotifyIcon has no "mouse left": the pointer is checked against the icon's rectangle a few times a second
    static void CheckHoverLeft()
    {
        var s = hotSlot; if (s == null) return;
        var r = ShellIconRect(s.Ni);
        if (r.IsEmpty || !r.Contains(Cursor.Position)) { hotSlot = null; s.Hot = false; Paint(s); }
    }

    // Open a URI through ShellExecute (explorer.exe <uri> turns "ms-actioncenter:..." into a Documents window).
    // Both start on a background STA thread (ShellExecute wants STA): a slow start never freezes the bar or an open flyout.
    static void Open(string uri)
    {
        OnStaThread(() => { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { Log("open " + uri + ": " + ex.Message); } });
    }
    static void Run(string exe, string args)
    {
        OnStaThread(() => { try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch (Exception ex) { Log("run " + exe + ": " + ex.Message); } });
    }
    static void OnStaThread(Action a) { var t = new Thread(() => a()) { IsBackground = true }; t.SetApartmentState(ApartmentState.STA); t.Start(); }

    // ---- drawing helpers ----
    static Bitmap NewIcon(int size, out Graphics g)
    {
        var bmp = new Bitmap(size, size); g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return bmp;
    }
    // Uniform fit of a viewBox into the square, centred.
    static Matrix Fit(float bx, float by, float bw, float bh, int size)
    {
        float k = size / Math.Max(bw, bh); var m = new Matrix();
        m.Translate((size - bw * k) / 2f, (size - bh * k) / 2f); m.Scale(k, k); m.Translate(-bx, -by);
        return m;
    }
    // Fill the square: each axis scales on its own, but one may stretch at most maxStretch times the other (1 = uniform Fit).
    static Matrix FitFill(float bx, float by, float bw, float bh, int size, float maxStretch)
    {
        float kx = size / bw, ky = size / bh;
        if (kx > ky) kx = Math.Min(kx, ky * maxStretch); else ky = Math.Min(ky, kx * maxStretch);
        var m = new Matrix();
        m.Translate((size - bw * kx) / 2f, (size - bh * ky) / 2f); m.Scale(kx, ky); m.Translate(-bx, -by);
        return m;
    }
    // Scale the ink of a large drawing into the square (fill, stretch limited); the large bitmap is disposed.
    static Bitmap CropToInk(Bitmap big, int size, float maxStretch)
    {
        int w = big.Width, h = big.Height, x0 = w, y0 = h, x1 = -1, y1 = -1;
        var d = big.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[w * h]; Marshal.Copy(d.Scan0, px, 0, px.Length); big.UnlockBits(d);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (((uint)px[y * w + x] >> 24) > 24) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        using (big)
            if (x1 >= 0)
            {
                var src = new RectangleF(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
                using (var m = FitFill(src.X, src.Y, src.Width, src.Height, size, maxStretch))
                { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.Transform = m; g.DrawImage(big, src, src, GraphicsUnit.Pixel); }
            }
        return bmp;
    }
    static Pen RoundPen(Color c, float w) { return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; }
    static Color Alpha(Color c, double a) { return Color.FromArgb((int)(255 * a), c); }

    // SVG path data -> GraphicsPath (M L H V C S Q T Z, absolute and relative, implicit repeats).
    static readonly Regex SvgTok = new Regex(@"[MmLlHhVvCcSsQqTtZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
    // Parsed once per path string (the glyphs are drawn many times a second): callers get a copy they may transform and dispose.
    static readonly Dictionary<string, GraphicsPath> svgCache = new Dictionary<string, GraphicsPath>();
    static GraphicsPath Svg(string d)
    {
        lock (svgCache)
        {
            GraphicsPath p;
            if (!svgCache.TryGetValue(d, out p)) svgCache[d] = p = ParseSvg(d);
            return (GraphicsPath)p.Clone();
        }
    }
    static GraphicsPath ParseSvg(string d)
    {
        var p = new GraphicsPath(FillMode.Winding);
        var t = new List<string>(); foreach (Match m in SvgTok.Matches(d)) t.Add(m.Value);
        int i = 0; char cmd = ' ', prev = ' '; PointF cur = PointF.Empty, start = PointF.Empty, ctrl = PointF.Empty;
        Func<float> N = () => float.Parse(t[i++], Inv);
        while (i < t.Count)
        {
            if (char.IsLetter(t[i][0])) { cmd = t[i][0]; i++; if (cmd == 'Z' || cmd == 'z') { p.CloseFigure(); cur = start; prev = 'Z'; continue; } }
            bool rel = char.IsLower(cmd); float ox = rel ? cur.X : 0, oy = rel ? cur.Y : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M': { var pt = new PointF(ox + N(), oy + N()); p.StartFigure(); cur = start = pt; cmd = rel ? 'l' : 'L'; prev = 'M'; continue; }
                case 'L': { var pt = new PointF(ox + N(), oy + N()); p.AddLine(cur, pt); cur = pt; break; }
                case 'H': { var pt = new PointF((rel ? cur.X : 0) + N(), cur.Y); p.AddLine(cur, pt); cur = pt; break; }
                case 'V': { var pt = new PointF(cur.X, (rel ? cur.Y : 0) + N()); p.AddLine(cur, pt); cur = pt; break; }
                case 'C':
                    {
                        var c1 = new PointF(ox + N(), oy + N()); var c2 = new PointF(ox + N(), oy + N()); var pt = new PointF(ox + N(), oy + N());
                        p.AddBezier(cur, c1, c2, pt); ctrl = c2; cur = pt; break;
                    }
                case 'S':
                    {
                        var c1 = (prev == 'C' || prev == 'S') ? new PointF(2 * cur.X - ctrl.X, 2 * cur.Y - ctrl.Y) : cur;
                        var c2 = new PointF(ox + N(), oy + N()); var pt = new PointF(ox + N(), oy + N());
                        p.AddBezier(cur, c1, c2, pt); ctrl = c2; cur = pt; break;
                    }
                case 'Q':
                case 'T':
                    {
                        PointF q;
                        if (char.ToUpperInvariant(cmd) == 'Q') q = new PointF(ox + N(), oy + N());
                        else q = (prev == 'Q' || prev == 'T') ? new PointF(2 * cur.X - ctrl.X, 2 * cur.Y - ctrl.Y) : cur;
                        var pt = new PointF(ox + N(), oy + N());
                        p.AddBezier(cur, new PointF(cur.X + 2f / 3 * (q.X - cur.X), cur.Y + 2f / 3 * (q.Y - cur.Y)),
                                         new PointF(pt.X + 2f / 3 * (q.X - pt.X), pt.Y + 2f / 3 * (q.Y - pt.Y)), pt);
                        ctrl = q; cur = pt; break;
                    }
                default: i++; break;
            }
            prev = char.ToUpperInvariant(cmd);
        }
        return p;
    }

    // Two tabular Diablo characters in the cells of "88" (the weekday: the widest weekday). Each number type has its own look so they
    // read apart at a glance: w = ink width and h = ink height, both as a share of the square (h <= 0: keep the font's proportions).
    // Weight > 0 strokes the glyph outline on top of the fill to fatten it (the Diablo glyphs are fixed filled paths, so
    // FontStyle.Bold alone does nothing): width = Weight * icon size, in the drawn pixels.
    class TextStyle { public FontStyle Style; public float W, H, Weight; }
    static readonly TextStyle DayText  = new TextStyle { Style = FontStyle.Bold, W = 1f, H = 1f },      // biggest: the whole square
        WeekText = new TextStyle { Style = FontStyle.Bold, W = 1f, H = 0.80f },
        TempText = new TextStyle { Style = FontStyle.Bold | FontStyle.Italic, W = 1f, H = 0.80f, Weight = 0.05f },   // owner: thicker weather digits
        BatText  = new TextStyle { Style = FontStyle.Bold, W = 1f, H = 0.74f },
        PingText = new TextStyle { Style = FontStyle.Bold, W = 0.62f, H = 0 };                     // smallest
    static readonly Dictionary<string, FontFamily> fonts = new Dictionary<string, FontFamily>();
    static FontFamily Family(string name)
    {
        FontFamily f;
        if (!fonts.TryGetValue(name, out f)) { try { f = new FontFamily(name); } catch { f = name == "Consolas" ? FontFamily.GenericMonospace : Family("Consolas"); } fonts[name] = f; }
        return f;
    }
    // ---- Diablo digits (owner, batch 3): every number but the DSEG7 clock is drawn in the whitebook panel font ----
    // Tabular like glyphs.tabular_paths(): cells of the widest digit, DIGIT_GAP (3 px at the 36 px em = 80 units) apart, a short
    // number takes the right cells; so a value change never moves anything. Units: font units, y down, ink from 0 to CAP.
    const float DiabloGap = Diablo.DIGIT_GAP * Diablo.EM / 36f;
    static float diabloCell = -1;
    static float DiabloCell()
    {
        if (diabloCell < 0) foreach (char c in "0123456789") { var g = Diablo.Glyphs[c]; diabloCell = Math.Max(diabloCell, g.XMax - g.XMin); }
        return diabloCell;
    }
    static float DiabloTabularWidth(int cells) { return cells * DiabloCell() + (cells - 1) * DiabloGap; }
    static GraphicsPath DiabloTabular(string text, int cells)
    {
        var all = new GraphicsPath(FillMode.Winding); float cell = DiabloCell(); int first = cells - text.Length;
        for (int i = 0; i < text.Length; i++)
        {
            Diablo.G g; if (!Diablo.Glyphs.TryGetValue(text[i], out g)) continue;
            float left = (first + i) * (cell + DiabloGap) + (cell - (g.XMax - g.XMin)) / 2;
            using (var p = Svg(g.D)) using (var m = new Matrix(1, 0, 0, -1, left - g.XMin, Diablo.CAP + Diablo.BASE)) { p.Transform(m); all.AddPath(p, false); }
        }
        return all;
    }
    // A glyph of the panel font: digits and Latin from glyphs.json, Ukrainian capitals from DiabloCyr.ttf (tools\build-cyr.ps1).
    // Diablo has capitals only: lowercase is drawn as capitals, ʼ as '; anything else is a space.
    static Diablo.G DiabloGlyph(char c)
    {
        Diablo.G g; if (c == 'ʼ') c = '\'';
        if (Diablo.Glyphs.TryGetValue(c, out g) || DiabloCyr.Glyphs.TryGetValue(c, out g)) return g;
        char u = char.ToUpperInvariant(c);
        return u != c ? DiabloGlyph(u) : DiabloCyr.Glyphs[' '];
    }
    // Proportional Diablo text (layout codes, month, weekday): ink from x 0; width in units through `width`.
    static GraphicsPath DiabloText(string text, out float width)
    {
        var all = new GraphicsPath(FillMode.Winding); width = 0; if (text.Length == 0) return all;
        float pen = -DiabloGlyph(text[0]).XMin;
        for (int i = 0; i < text.Length; i++)
        {
            var g = DiabloGlyph(text[i]);
            if (g.D.Length > 0) using (var p = Svg(g.D)) using (var m = new Matrix(1, 0, 0, -1, pen, Diablo.CAP + Diablo.BASE)) { p.Transform(m); all.AddPath(p, false); }
            if (i < text.Length - 1) pen += g.Adv; else width = pen + (g.D.Length > 0 ? g.XMax : 0);
        }
        return all;
    }
    // Diablo text with its cap height `cap` centred on cy, from x (g = null: only measure); returns the ink width.
    static readonly Dictionary<string, float> diabloWidths = new Dictionary<string, float>();   // measuring only: no paths built
    static float DiabloLine(Graphics g, string s, float x, float cy, float cap, Color col)
    {
        float w, k = cap / Diablo.CAP;
        if (g == null)
        {
            lock (diabloWidths) if (diabloWidths.TryGetValue(s, out w)) return w * k;
            using (DiabloText(s, out w)) { }
            lock (diabloWidths) diabloWidths[s] = w;
            return w * k;
        }
        using (var p = DiabloText(s, out w))
        {
            if (g != null) using (var m = new Matrix(k, 0, 0, k, x, cy - cap / 2)) using (var b = new SolidBrush(col)) { p.Transform(m); g.FillPath(b, p); }
        }
        return w * k;
    }
    // A word of `all` (weekday) filling the tray square like the digits (ts.W, ts.H), scaled by the ink box of all the words together:
    // every word gets the same size and the same centre line, so a new day never changes the look of the icon.
    static Bitmap DrawDiabloWord(int size, string t, string[] all, Color col, TextStyle ts)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        {
            RectangleF box = RectangleF.Empty; float w, tw = 0;
            foreach (var a in all) using (var p = DiabloText(a, out w)) { var b = p.GetBounds(); box = box.IsEmpty ? b : RectangleF.Union(box, b); }
            using (var path = DiabloText(t, out tw))
            {
                float kx = Math.Min(ts.W * size / box.Width, size / box.Height), ky = ts.H * size / box.Height;
                using (var m = new Matrix())
                {
                    m.Translate((size - tw * kx) / 2f, (size - box.Height * ky) / 2f); m.Scale(kx, ky); m.Translate(0, -box.Y); path.Transform(m);
                }
                using (var b = new SolidBrush(col)) g.FillPath(b, path);
            }
        }
        return bmp;
    }
    // Two tabular Diablo cells fitted into the tray square with the style's proportions (W, H; italic = sheared like the old italic).
    static Bitmap DrawDiablo(int size, string t, Color col, TextStyle ts)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        using (var path = DiabloTabular(t.Trim(), 2))
        {
            float w = DiabloTabularWidth(2), h = Diablo.CAP, slant = (ts.Style & FontStyle.Italic) != 0 ? 0.2f : 0f;
            if (slant > 0) using (var sh = new Matrix(1, 0, -slant, 1, slant * h, 0)) path.Transform(sh);
            w += slant * h;
            float kx = Math.Min(ts.W * size / w, ts.H > 0 ? size / h : float.MaxValue), ky = ts.H > 0 ? ts.H * size / h : kx;
            using (var m = new Matrix()) { m.Translate((size - w * kx) / 2f, (size - h * ky) / 2f); m.Scale(kx, ky); path.Transform(m); }
            using (var b = new SolidBrush(col)) g.FillPath(b, path);
            if (ts.Weight > 0) using (var pen = new Pen(col, ts.Weight * size) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawPath(pen, path);
        }
        return bmp;
    }
    // "No value": a short rounded dash (whitebook placeholder bar).
    static Bitmap DrawDash(int size, Color col)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g) using (var pen = RoundPen(col, Math.Max(1.5f, size / 9f))) g.DrawLine(pen, size * 0.25f, size / 2f, size * 0.75f, size / 2f);
        return bmp;
    }

    // ---- Wi-Fi fan (Render-Wifi, netqd.write_wifi_icon): viewBox "2 3 52 30", apex (28,33), r 30, ±60°, 5 steps ----
    const int FanSteps = 5;
    // lit: -1 = offline (nothing lit), 0..5 = lit steps.
    static Bitmap DrawWifi(int size, int lit) { return DrawWifi(size, lit, Light); }
    // fanColour = the ping's colour (owner, batch 2 A5): light, warning from 100 ms, danger from 500 ms / no reply
    static Bitmap DrawWifi(int size, int lit, Color fanColour)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        {
            using (var m = Fit(2, 3, 52, 30, size)) g.Transform = m;   // source proportions (viewBox 2 3 52 30), no stretch
            float cx = 28, cy = 33, R = 30;
            using (var b = new SolidBrush(FanBase)) g.FillPie(b, cx - R, cy - R, 2 * R, 2 * R, 210, 120);
            if (lit > 0) { float r = R * lit / FanSteps; using (var b = new SolidBrush(fanColour)) g.FillPie(b, cx - r, cy - r, 2 * r, 2 * r, 210, 120); }
            using (var pen = new Pen(Color.Black, 52f / 34f))   // size/34 px, in viewBox units
                for (int k = 1; k < FanSteps; k++) { float r = R * k / FanSteps; g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, 210, 120); }
        }
        return bmp;
    }

    // ---- Ethernet (A2): RJ45 plug shown instead of the Wi-Fi arcs when the default route is wired. Coloured like the
    // Wi-Fi icon (ping colour). Supersampled 3x for a smooth outline at tray size. ----
    static Bitmap DrawEthernet(int size, Color col)
    {
        const int SS = 3; int big = size * SS;
        Graphics g; var bmp = NewIcon(big, out g);
        using (g)
        using (var m = Fit(0, 0, 44, 44, big))
        {
            g.Transform = m;
            // Flow concept 2 (owner 26.10): an RJ45 plug tilted up-right with its cable swinging down in an S, drawn to fill the
            // whole icon (the Flow sketch was too small). Thick strokes, gold contacts; colour = ping state (red = no reply).
            using (var pen = new Pen(col, 2.7f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var ppen = new Pen(col, 2.7f / 1.4f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })   // same width after the x1.4 scale
            using (var pins = new SolidBrush(Warning))
            {
                var st = g.Save();
                g.TranslateTransform(24f, 19f); g.RotateTransform(45f); g.ScaleTransform(1.4f, 1.4f);   // big plug, axis up-right
                using (var head = RoundRect(-6, -14, 12, 13, 1.6f)) g.DrawPath(ppen, head);                     // plug head
                for (int i = 0; i < 4; i++) using (var pin = RoundRect(-3.9f + i * 2.25f, -12f, 1.2f, 4.2f, 0.4f)) g.FillPath(pins, pin);
                g.DrawLine(ppen, -3.5f, -4.6f, 3.5f, -4.6f);                                                   // latch ridge
                using (var boot = RoundRect(-4.2f, -1, 8.4f, 7, 2)) g.DrawPath(ppen, boot);                   // strain-relief boot
                g.Restore(st);
                using (var cable = new GraphicsPath())
                {   // leaves the boot along the plug axis, then an S down to the bottom-left corner
                    cable.AddBezier(18.1f, 24.9f, 14f, 29f, 21f, 34f, 12.5f, 37.5f);
                    cable.AddBezier(12.5f, 37.5f, 8.5f, 40.5f, 6.5f, 41f, 3f, 41f);
                    g.DrawPath(pen, cable);
                }
            }
        }
        var outb = new Bitmap(size, size);
        using (var go = Graphics.FromImage(outb))
        {
            go.Clear(Color.Transparent); go.InterpolationMode = InterpolationMode.HighQualityBicubic; go.PixelOffsetMode = PixelOffsetMode.HighQuality;
            go.DrawImage(bmp, new Rectangle(0, 0, size, size), new Rectangle(0, 0, big, big), GraphicsUnit.Pixel);
        }
        bmp.Dispose();
        return outb;
    }

    // ---- ping (Render-Net, current value only): 100+ = "00" ----
    static Color PingColour(int ms) { return ms < 0 || ms >= 500 ? Danger : ms >= 100 ? Warning : Light; }
    static Bitmap DrawPing(int size, int ms) { return ms < 0 ? DrawDash(size, Danger) : DrawDiablo(size, ms >= 100 ? "00" : ms.ToString(), PingColour(ms), PingText); }

    // ---- CPU / RAM (Render-Sys): 4 bands clipped to the Nerd glyph; cpu horizontal from the bottom, memory vertical from the left ----
    static Bitmap DrawSys(int size, bool memory, int pct)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        float[] b = memory ? Glyphs.MemoryBounds : Glyphs.CpuBounds;
        float gwu = b[2] - b[0], ghu = b[3] - b[1];
        float GW = memory ? 38 : 36, GH = memory ? 24 : 36, k = size / Math.Max(GW, GH);   // sysicons.py GEOMETRY, px of the 44 px panel
        float gw = GW * k, gh = GH * k, kx = gw / gwu, ky = gh / ghu, x0 = (size - gw) / 2f, y0 = (size - gh) / 2f;
        int count = Math.Max(1, Math.Min(4, (int)Math.Ceiling(Math.Max(0, pct) / 25.0)));
        Color[] colours = { Light, Success, Warning, Danger };
        float gap = 1.5f * k;   // the 1.5 px black pinstripe of sysicons.py
        using (g)
        using (var bands = new Bitmap(size, size))
        {
            using (var bg = Graphics.FromImage(bands))
            {
                bg.Clear(Color.Transparent);
                for (int i = 0; i < 4; i++)
                    using (var br = new SolidBrush(i < count ? colours[i] : Muted))
                    {
                        float g2 = i < 3 ? gap : 0;
                        if (!memory) { float band = gh / 4; bg.FillRectangle(br, x0, y0 + gh - (i + 1) * band + g2, gw, band - g2); }
                        else { float band = gw / 4; bg.FillRectangle(br, x0 + i * band, y0, band - g2, gh); }
                    }
            }
            using (var path = Svg(memory ? Glyphs.Memory : Glyphs.Cpu))
            using (var m = new Matrix(kx, 0, 0, -ky, x0 - b[0] * kx, y0 + b[3] * ky))   // font units, y up
            using (var tb = new TextureBrush(bands))
            {
                path.Transform(m); g.FillPath(tb, path);   // texture fill keeps the glyph edge anti-aliased (a clip would not)
            }
        }
        return bmp;
    }

    // ---- weather (Render-Weather): symbol viewBox "4.8 0 46.4 48"; temperature = 2 digits, no sign, colour by sign ----
    // WMO code -> condition name is localised: see WmoName() and strings\<lang>.json keys "weather.wmo.<code>".
    const string Cloud = "M13 29c-5 0-7-3-7-7s3-7 7-7c2-7 8-11 15-10 6 1 10 5 11 11 c6-1 11 3 11 9 0 5-4 8-9 8H13z";
    // The bar shows the same picture set as whitebook's panel-weather (owner 26.10): weather-icons\png, chosen by WMO code
    // and day/night exactly like its icon_file(); the black background is keyed out (alpha = brightness) so the art looks the
    // same on a black taskbar and blends on any other; stale data is drawn at half opacity like whitebook. code < 0 = no data.
    // The drawn symbol below (DrawWeatherSymbolBox) stays as the fallback when the PNG set is missing.
    static Bitmap DrawWeatherSymbol(int size, int code, bool stale, bool day = true)
    {
        var png = WeatherPng(WeatherIconName(code, day));
        if (png == null) return CropToInk(DrawWeatherSymbolBox(size * 4, code, stale), size, 1f);
        var keyed = KeyBlack((Bitmap)png, stale || code < 0 ? 0.5 : 1.0);
        return CropToInk(keyed, size, 1f);
    }
    // black-background art -> alpha: a = max(r,g,b), colour un-premultiplied, so over black it is pixel-identical
    static Bitmap KeyBlack(Bitmap src, double opacity)
    {
        int w = src.Width, h = src.Height; var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        Bitmap s32; lock (src) s32 = src.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
        var ds = s32.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[w * h]; Marshal.Copy(ds.Scan0, px, 0, px.Length); s32.UnlockBits(ds); s32.Dispose();
        for (int i = 0; i < px.Length; i++)
        {
            int r = (px[i] >> 16) & 255, g = (px[i] >> 8) & 255, bl = px[i] & 255, m = Math.Max(r, Math.Max(g, bl));
            if (m == 0) { px[i] = 0; continue; }
            int a = (int)(m * opacity);
            px[i] = (a << 24) | ((r * 255 / m) << 16) | ((g * 255 / m) << 8) | (bl * 255 / m);
        }
        var dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(px, 0, dd.Scan0, px.Length); dst.UnlockBits(dd);
        return dst;
    }
    static Bitmap DrawWeatherSymbolBox(int size, int code, bool stale)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        Color colour = stale || code < 0 ? Muted : Light, sun = colour != Muted ? Warning : Muted;
        using (g)
        using (var m = Fit(4.8f, 0, 46.4f, 48, size))
        {
            g.Transform = m;
            Action drawSun = () =>
            {
                using (var rays = Svg("M28 3v5 M28 34v5 M8 21h5 M43 21h5 M14 7l4 4 M38 31l4 4 M42 7l-4 4 M18 31l-4 4"))
                using (var pen = RoundPen(sun, 2.5f)) g.DrawPath(pen, rays);
                using (var br = new SolidBrush(sun)) g.FillEllipse(br, 18, 11, 20, 20);
            };
            Action drawCloud = () =>
            {
                using (var c = Svg(Cloud)) using (var br = new SolidBrush(Background)) using (var pen = RoundPen(colour, 2.5f)) { g.FillPath(br, c); g.DrawPath(pen, c); }
            };
            if (code == 0) drawSun();
            else if (code == 1 || code == 2) { drawSun(); drawCloud(); }
            else if (code == 45 || code == 48) { drawCloud(); using (var l = Svg("M10 38h36 M17 43h27")) using (var pen = new Pen(colour, 2)) g.DrawPath(pen, l); }
            else if (code == 71 || code == 73 || code == 75 || code == 77 || code == 85 || code == 86)
            {
                drawCloud(); using (var br = new SolidBrush(colour)) foreach (var pt in new[] { new PointF(17, 39), new PointF(29, 42), new PointF(40, 39) }) g.FillEllipse(br, pt.X - 2, pt.Y - 2, 4, 4);
            }
            else if (code == 95 || code == 96 || code == 99) { drawCloud(); using (var bolt = Svg("M29 31 22 43h7l-2 5 11-14h-7l2-3")) using (var br = new SolidBrush(sun)) g.FillPath(br, bolt); }
            else if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) { drawCloud(); using (var r = Svg("m17 37-3 7 m14-7-3 7 m14-7-3 7")) using (var pen = new Pen(colour, 3)) g.DrawPath(pen, r); }
            else drawCloud();
        }
        return bmp;
    }
    static Bitmap DrawWeatherTemp(int size, double? temp, bool stale)
    {
        if (temp == null) return DrawDash(size, Muted);
        double t = temp.Value;
        Color col = stale ? Muted : t > 0 ? Warning : t < 0 ? Light : TextCol;
        string s = Math.Min(99, Math.Abs((int)Math.Round(t))).ToString();
        // Consolas, not Diablo: at tray size the Diablo digits (especially "4") were hard to read; the owner wants the
        // temperature plainly legible. Measured by ink (GraphicsPath) and scaled to fill the icon, left-aligned, with a
        // small degree ring in the top-right corner.
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                // Family() is a shared cache: never dispose it here (that broke every Consolas font in the bar)
                path.AddString(s, Family("Consolas"), (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                var ink = path.GetBounds();
                if (ink.Width > 0 && ink.Height > 0)
                {
                    float bw = size * 0.74f, bh = size * 0.90f;   // digits fill this; the top-right corner is kept for the degree sign
                    float k = Math.Min(bw / ink.Width, bh / ink.Height);
                    using (var m = new Matrix())
                    {
                        m.Translate(size * 0.04f - ink.X * k, (size - ink.Height * k) / 2f - ink.Y * k);
                        m.Scale(k, k); path.Transform(m);
                    }
                    using (var b = new SolidBrush(col)) g.FillPath(b, path);
                }
            }
            float r = size * 0.12f, cx = size - r - size * 0.03f, cy = r + size * 0.03f;
            using (var pen = new Pen(col, Math.Max(1f, size * 0.07f))) g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        }
        return bmp;
    }

    // ---- battery (Render-Battery): case 16x22 + nub, 4 segments, bolt when plugged; unit box x 1.1..23.6 ----
    static readonly Color[] SegCol = { Light, Success, Warning, Danger };
    static readonly float[] SegY = { 7f, 11.5f, 16f, 20.5f };
    static int BatLit(int shown) { return shown > 0 ? Math.Max(1, Math.Min(4, (int)Math.Round(shown / 25.0, MidpointRounding.AwayFromZero))) : 0; }
    static Color BatDigitColour(int shown) { int lit = BatLit(shown); return lit > 0 ? SegCol[4 - lit] : Danger; }
    // Bolt frame: charging below 95 % (owner, batch 3; was 60 in panel-battery-render.py) the bolt fills band by band (0..4) once a second.
    static int BoltFrame(int shown, bool plugged) { return plugged && shown < 95 ? DateTime.Now.Second % 5 : 4; }
    static Bitmap DrawBattery(int size, int shown, bool plugged) { return DrawBattery(size, shown, plugged, BoltFrame(shown, plugged)); }
    static Bitmap DrawBattery(int size, int shown, bool plugged, int boltLit)
    {
        // Supersample 3x then downscale (A7): the thin outline is noticeably smoother than drawing straight at tray size.
        const int SS = 3; int big = size * SS;
        Graphics g; var bmp = NewIcon(big, out g);
        int lit = BatLit(shown);
        using (g)
        using (var m = Fit(1.1f, 0, 22.5f, 30, big))
        {
            g.Transform = m;
            // The owner's whitebook battery 1:1 (panel-battery-render: rect 2,4,16,22 rx 2, stroke 1.8). Its nub was an open
            // path "M7 2h6v2" (top + right leg only) and read off-centre; both legs now (owner 26.10), centred on x=10.
            using (var pen = new Pen(TextCol, 1.8f) { LineJoin = LineJoin.Round })
            {
                using (var body = RoundRect(2, 4, 16, 22, 2)) g.DrawPath(pen, body);
                using (var nub = Svg("M7 4V2h6v2")) g.DrawPath(pen, nub);
            }
            for (int i = 0; i < 4; i++)
                using (var seg = RoundRect(5, SegY[i], 10, 3, 0.7f)) using (var br = new SolidBrush(i >= 4 - lit ? SegCol[i] : BatDead)) g.FillPath(br, seg);
            if (plugged)
                using (var bolt = Svg("M19 2 9 18h6l-2 11 10-16h-6z")) using (var pen = new Pen(Background, 1.1f) { LineJoin = LineJoin.Round })
                {   // dim bolt, then 4 bands (bottom first, 6.75 units each) lit in warning up to boltLit, then the black outline
                    using (var dim = new SolidBrush(SkyDim)) g.FillPath(dim, bolt);
                    for (int i = 0; i < 4; i++)
                    {
                        var st = g.Save(); g.SetClip(new RectangleF(8, 22.25f - 6.75f * i, 16, 6.75f), CombineMode.Intersect);
                        using (var br = new SolidBrush(i < boltLit ? Warning : SkyDim)) g.FillPath(br, bolt);
                        g.Restore(st);
                    }
                    g.DrawPath(pen, bolt);
                }
        }
        var outb = new Bitmap(size, size);
        using (var go = Graphics.FromImage(outb))
        {
            go.Clear(Color.Transparent); go.InterpolationMode = InterpolationMode.HighQualityBicubic; go.PixelOffsetMode = PixelOffsetMode.HighQuality;
            go.DrawImage(bmp, new Rectangle(0, 0, size, size), new Rectangle(0, 0, big, big), GraphicsUnit.Pixel);
        }
        bmp.Dispose();
        return outb;
    }
    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90); p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // ---- audio (Render-Audio): speaker translated (26.5,-12) in viewBox "0 0 85.5 38", cropped to the ink "32 0 53.5 38" ----
    // vol < 0 = no output device.
    static Bitmap DrawAudio(int size, int vol, bool muted)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        using (var m = Fit(32f, 0, 53.5f, 38, size))
        {
            m.Translate(26.5f, -12f); g.Transform = m;
            SpeakerBody(g, vol, muted);
        }
        return bmp;
    }
    // speaker_svg() of audio-render.py: the speaker alone (viewBox 5 11 56 42), for the sound flyout
    static Bitmap DrawSpeaker(int size, int vol, bool muted)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g) using (var m = Fit(5, 11, 56, 42, size)) { g.Transform = m; SpeakerBody(g, vol, muted); }
        return bmp;
    }
    // speaker_body(): horn, three arcs (lit by volume, unlit at 0.14), muted: the red Diablo "O" of bar.ps1 Render-Audio (owner, batch 4:
    // "O like mine instead of the cross"), 22 high, centred on x 45, its middle at y 32; speaker coordinates
    static void SpeakerBody(Graphics g, int vol, bool muted)
    {
        bool off = muted || vol < 0;
        int lit = off ? 0 : vol <= 33 ? 1 : vol <= 66 ? 2 : 3;
        {
            using (var horn = Svg("M7 24h9l12-10v36L16 40H7z")) using (var pen = new Pen(off ? Muted : Foreground, 2.6f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, horn);
            string[] arcs = { "M34 26q6 6 0 12", "M41 20q12 12 0 24", "M48 14q18 18 0 36" }; Color[] ac = { Warning, Success, Light };
            for (int i = 0; i < 3; i++)
                using (var a = Svg(arcs[i])) using (var pen = RoundPen(i < lit ? ac[i] : Alpha(ac[i], 0.14), 4)) g.DrawPath(pen, a);
            if (muted)
            {
                var o = Diablo.Glyphs['O']; float s = 22f / Diablo.CAP, xo = 45 - (o.XMin + o.XMax) / 2 * s, yo = 32 + 22f / 2 + Diablo.BASE * s;
                using (var p = Svg(o.D)) using (var m = new Matrix(s, 0, 0, -s, xo, yo)) using (var b = new SolidBrush(Danger)) { p.Transform(m); g.FillPath(b, p); }
            }
        }
    }

    // ---- gear (not in bar.ps1: drawn in the panel's text colour) ----
    static Bitmap DrawGear(int size)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        using (var m = Fit(0, 0, 22, 22, size))
        using (var p = new GraphicsPath(FillMode.Alternate))
        {
            g.Transform = m;
            var pts = new List<PointF>();
            for (int t = 0; t < 8; t++)
                foreach (var ar in new[] { new[] { -15f, 7.4f }, new[] { -9f, 10.4f }, new[] { 9f, 10.4f }, new[] { 15f, 7.4f } })
                {
                    double a = (t * 45 + ar[0]) * Math.PI / 180; pts.Add(new PointF(11 + ar[1] * (float)Math.Cos(a), 11 + ar[1] * (float)Math.Sin(a)));
                }
            p.AddPolygon(pts.ToArray()); p.AddEllipse(11 - 3.4f, 11 - 3.4f, 6.8f, 6.8f);
            using (var br = new SolidBrush(TextCol)) g.FillPath(br, p);
        }
        return bmp;
    }

    // ---- clock (Render-Clock): two DSEG7 digits over a ghost "88" (foreground, opacity 0.20), lit in light; no colon ----
    static Bitmap DrawSeg(int size, string two)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g)
        using (var ghost = new GraphicsPath(FillMode.Winding))
        using (var lit = new GraphicsPath(FillMode.Winding))
        {
            for (int i = 0; i < 2; i++)
            {
                using (var m = new Matrix(1, 0, 0, -1, i * Glyphs.Dseg7Adv, 0))   // tabular cell i, y up -> y down
                {
                    using (var p8 = Svg(Glyphs.Dseg7[8])) { p8.Transform(m); ghost.AddPath(p8, false); }
                    using (var pd = Svg(Glyphs.Dseg7[two[i] - '0'])) { pd.Transform(m); lit.AddPath(pd, false); }
                }
            }
            var b = ghost.GetBounds();
            using (var fit = FitFill(b.X, b.Y, b.Width, b.Height, size, 1.3f)) { ghost.Transform(fit); lit.Transform(fit); }
            using (var br = new SolidBrush(Alpha(Foreground, 0.20))) g.FillPath(br, ghost);
            using (var br = new SolidBrush(Light)) g.FillPath(br, lit);
        }
        return bmp;
    }

    // ---- date (Render-Date): day = Diablo, season colour of the month; weekday = Diablo (DiabloCyr), rainbow colour Mon..Sun ----
    // Localised (A9): filled from the string tables by LoadLocalizedArrays() after Strings.Load, so switching language
    // re-labels the date area. Weekday is Monday-first (index ((int)DayOfWeek+6)%7); months are index Month-1.
    static string[] UaWeek, UaWeekFull, UaMonthFull, UaMonthName, UaMonthAbbr, UaDayAbbr;
    static string[] LArr(string prefix, int n) { var a = new string[n]; for (int i = 0; i < n; i++) a[i] = Strings.T(prefix + (i + 1)); return a; }
    static void LoadLocalizedArrays()
    {
        UaWeek      = LArr("clock.week_abbr.", 7);   // ПН..НД — uppercase tray weekday
        UaWeekFull  = LArr("date.week.", 7);         // full weekday (lowercase uk/ru, capitalised en)
        UaMonthFull = LArr("date.month.", 12);       // genitive month ("26 <month> 2026")
        UaMonthName = LArr("date.month_nom.", 12);   // nominative month (calendar header)
        UaMonthAbbr = LArr("date.month_abbr.", 12);  // abbreviated month
        UaDayAbbr   = LArr("date.week_abbr.", 7);    // Пн..Нд — calendar weekday header
    }
    // WMO weather-code -> localised name ("—" when unknown). Strings.T returns the key itself if absent.
    static string WmoName(int code) { string k = "weather.wmo." + code, v = Strings.T(k); return v == k ? "—" : v; }

    // Live language switch (A9): re-read the shared language, refill arrays, force every slot to repaint. Runs on the UI thread.
    static void ReloadLang()
    {
        Strings.Load(Strings.ResolveLang(SettingStr("language", "en")));
        LoadLocalizedArrays();
        foreach (var s in new[] { wifi, ping, cpu, ram, wSym, wTemp, bat, batPct, audio, day, week, hours, minutes }) if (s != null) s.Key = "";
        foreach (Action a in new Action[] { PollClock, PollWifi, PollBattery, PollAudio, RenderWeather, PollSys })
            try { a(); } catch (Exception ex) { Log("reloadlang " + a.Method.Name + ": " + ex.Message); }
    }
    static Color SeasonColour(int month) { return month <= 2 || month == 12 ? Light : month <= 5 ? Success : month <= 8 ? Danger : Warning; }
    static readonly Color[] WeekdayColour = { Violet, TerminalBlue, Light, Success, Warning, Orange, Danger };

    // The native clock's own click (posted to TrayClockWClass, the cursor does not move) opens the same calendar as the shell.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    static void OpenCalendar()
    {
        IntPtr tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr clock = FindWindowEx(notify, IntPtr.Zero, "TrayClockWClass", null);
        RECT r;
        if (clock == IntPtr.Zero || !GetClientRect(clock, out r)) { Open("ms-actioncenter:"); return; }
        var lp = (IntPtr)(((r.Bottom / 2) << 16) | (r.Right / 2));
        PostMessage(clock, 0x0201 /*WM_LBUTTONDOWN*/, (IntPtr)1, lp); PostMessage(clock, 0x0202 /*WM_LBUTTONUP*/, IntPtr.Zero, lp);
    }

    static void PollClock()
    {
        var now = DateTime.Now; int wi = ((int)now.DayOfWeek + 6) % 7;
        string tip = UaWeekFull[wi] + ", " + now.Day + " " + UaMonthFull[now.Month - 1] + " " + now.Year;
        string hh = now.ToString("HH"), mm = now.ToString("mm"), dd = now.ToString("dd");
        Color dc = Foreground, wc = WeekdayColour[wi];
        Show(hours, hh, s => DrawSeg(s, hh), tip);
        Show(minutes, mm, s => DrawSeg(s, mm), tip);
        Show(day, dd + now.Month, s => DrawDiablo(s, dd, dc, DayText), tip);
        Show(week, wi.ToString(), s => DrawDiabloWord(s, UaWeek[wi], UaWeek, wc, WeekText), tip);
    }

    // ---- data ----
    static void PollWifi()
    {
        int lit = -1; string tip;
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) tip = Strings.T("tip.wifi.no_connection");
            else if (p.IsWlanConnectionProfile)
            {
                var bars = p.GetSignalBars();
                lit = bars.HasValue ? Math.Max(1, Math.Min(FanSteps, (int)bars.Value)) : FanSteps;
                tip = Strings.T("tip.wifi.signal", p.ProfileName, (bars.HasValue ? bars.Value + "/5" : "?"));
            }
            else { lit = FanSteps; tip = Strings.T("tip.net.wired", p.ProfileName); }
            wifiName = p != null ? p.ProfileName : null; wired = p != null && !p.IsWlanConnectionProfile;
        }
        catch (Exception ex) { tip = Strings.T("tip.wifi.error"); Log("wifi: " + ex.Message); }
        Color fc = pingKnown ? PingColour(lastPing) : Light;
        bool eth = wired;   // snapshot for the icon closure
        Show(wifi, (eth ? "eth" : lit.ToString()) + "/" + fc.ToArgb(), s => eth ? DrawEthernet(s, fc) : DrawWifi(s, lit, fc), tip);
        if (bigWifiLit != lit) { bigWifiLit = lit; BigChanged(); }
    }

    static void PollPing()
    {
        // internet and router in parallel, once a second (Net.cs keeps the windows for the tooltip); the indicator shows "now"
        string gw = gateway;
        var ti = PingOnce(InternetHost, 900); var tr = gw != null ? PingOnce(gw, 500) : null;
        int ms = ti.Result; internetPings.Add(ms);
        if (tr != null) routerPings.Add(tr.Result);
        PollBytes();
        string tip = ms < 0 ? Strings.T("tip.ping.no_reply", InternetHost) : Strings.T("tip.ping.ms", InternetHost, ms);
        Show(ping, ms >= 100 ? (ms >= 500 ? "500" : "100") : ms.ToString(), s => DrawPing(s, ms), tip);
        bool changed = !pingKnown || PingColour(ms) != PingColour(lastPing);
        lastPing = ms; pingKnown = true;
        if (changed) PollWifi();   // the fan takes the ping's colour at once
        BigChanged();
    }
    static volatile int lastPing; static volatile bool pingKnown;
    static System.Threading.Tasks.Task<int> PingOnce(string host, int timeout)
    {
        return System.Threading.Tasks.Task.Run(() =>
        {
            try { using (var p = new Ping()) { var r = p.Send(host, timeout); return r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1; } } catch { return -1; }
        });
    }

    static long lastIdle, lastTotal;
    static void PollSys()
    {
        long idle, kernel, user;
        if (GetSystemTimes(out idle, out kernel, out user))
        {
            long total = kernel + user, dt = total - lastTotal, di = idle - lastIdle;
            if (lastTotal != 0 && dt > 0)
            {
                int pct = (int)Math.Round(100.0 * (dt - di) / dt); int cnt = (int)Math.Ceiling(pct / 25.0);
                Show(cpu, cnt.ToString(), s => DrawSys(s, false, pct), Strings.T("tip.cpu", pct));
            }
            lastIdle = idle; lastTotal = total;
        }
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
        if (GlobalMemoryStatusEx(ref ms))
        {
            int pct = (int)ms.dwMemoryLoad; double used = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0, tot = ms.ullTotalPhys / 1073741824.0;
            Show(ram, ((int)Math.Ceiling(pct / 25.0)).ToString(), s => DrawSys(s, true, pct),
                 Strings.T("tip.ram", pct, used.ToString("0.0", Inv), tot.ToString("0.0", Inv)));
        }
    }

    static void PollBattery()
    {
        var ps = SystemInformation.PowerStatus;
        bool present = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown;
        batteryPresent = present;
        if (!present) { bigBatShown = -1; BigChanged(); ui.Post(_ => { bat.Ni.Visible = false; batPct.Ni.Visible = false; }, null); return; }
        int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
        bool plugged = ps.PowerLineStatus == PowerLineStatus.Online;
        bool full = plugged && pct >= 97;
        int shown = full ? 100 : pct;
        string tip = Strings.T("tip.battery", shown, (plugged ? Strings.T("tip.battery.charging") : Strings.T("tip.battery.discharging")));
        string key = shown + "/" + plugged;
        int frame = BoltFrame(shown, plugged);
        Show(bat, BatLit(shown) + "/" + plugged + "/" + frame, s => DrawBattery(s, shown, plugged, frame), tip);
        Show(batPct, key, s => DrawDiablo(s, shown >= 100 ? "00" : shown.ToString(), BatDigitColour(shown), BatText), tip);
        if (bigBatShown != shown || bigPlugged != plugged) { bigBatShown = shown; bigPlugged = plugged; BigChanged(); }
        if (!bigMode) ui.Post(_ => { bat.Ni.Visible = true; batPct.Ni.Visible = true; }, null);
    }

    static volatile bool batteryPresent = true;
    static void PollAudio()
    {
        int vol = -1; bool muted = false;
        IMMDevice dev = null; object o = null;
        try
        {
            if (NewEnum().GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out dev) == 0 && dev != null)
            {
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o) == 0)
                {
                    var ep = (IAudioEndpointVolume)o; float lv; bool mu;
                    ep.GetMasterVolumeLevelScalar(out lv); ep.GetMute(out mu);
                    vol = (int)Math.Round(lv * 100); muted = mu;
                }
            }
        }
        catch (Exception ex) { Log("audio: " + ex.Message); threadEnum = null; }
        finally { if (o != null) Marshal.ReleaseComObject(o); if (dev != null) Marshal.ReleaseComObject(dev); }
        string tip = vol < 0 ? Strings.T("tip.audio.unavailable") : muted ? Strings.T("tip.audio.muted") : Strings.T("tip.audio.volume", vol);
        int lit = muted || vol < 0 ? 0 : vol <= 33 ? 1 : vol <= 66 ? 2 : 3;
        int v = vol; bool mt = muted;
        Show(audio, lit + "/" + muted + "/" + (vol < 0), s => DrawAudio(s, v, mt), tip);
        if (bigVol != vol || bigMuted != muted) { bigVol = vol; bigMuted = muted; BigChanged(); }
    }

    static int wCode = -1; static bool wDay = true; static double? wTempVal; static DateTime wOk = DateTime.MinValue;
    static System.Threading.Timer weatherTimer;
    static void PollWeather()
    {
        bool ok = false;
        try
        {
            EnsureLocation();
            if (!HasLocation) { weatherTimer.Change(2 * 60000, Timeout.Infinite); RenderWeather(); return; }   // no location yet; retry soon
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 })
            {
                string js = wc.DownloadString("https://api.open-meteo.com/v1/forecast?" + GeoQuery + "&current=temperature_2m,weather_code,is_day&timezone=auto");
                var cur = Regex.Match(js, "\"current\"\\s*:\\s*\\{([^}]*)\\}").Groups[1].Value;
                var t = Regex.Match(cur, "\"temperature_2m\"\\s*:\\s*(-?[\\d.]+)"); var c = Regex.Match(cur, "\"weather_code\"\\s*:\\s*(\\d+)");
                var dn = Regex.Match(cur, "\"is_day\"\\s*:\\s*(\\d)");
                if (t.Success && c.Success) { wTempVal = double.Parse(t.Groups[1].Value, Inv); wCode = int.Parse(c.Groups[1].Value); wDay = !dn.Success || dn.Groups[1].Value != "0"; wOk = DateTime.Now; ok = true; }
            }
        }
        catch (Exception ex) { Log("weather: " + ex.Message); }
        weatherTimer.Change(ok ? 15 * 60000 : 2 * 60000, Timeout.Infinite);   // retry sooner after a failure
        RenderWeather();
    }
    static void RenderWeather()
    {
        bool stale = wOk == DateTime.MinValue || (DateTime.Now - wOk).TotalMinutes > 60;
        int code = wCode; bool day = wDay; double? temp = wTempVal;
        string nm = WmoName(code);
        string place = GeoPlace;   // stored location name (A5), falls back to the localised default label
        string tip = temp == null ? Strings.T("tip.weather.no_data")
            : (stale ? Strings.T("tip.weather.stale", Strings.T("tip.weather", place, nm, Math.Round(temp.Value).ToString(Inv)))
                     : Strings.T("tip.weather", place, nm, Math.Round(temp.Value).ToString(Inv)));
        Show(wSym, code + "/" + day + "/" + stale, s => DrawWeatherSymbol(s, code, stale, day), tip);
        Show(wTemp, (temp == null ? "-" : Math.Round(temp.Value).ToString(Inv)) + "/" + stale, s => DrawWeatherTemp(s, temp, stale), tip);
    }

    // Timer that never overlaps itself; exceptions are logged, not fatal.
    // ---- rates: normal / on battery / idle (session locked or display off: nobody sees the bar) ----
    static System.Threading.Timer tWifi, tPing, tSys, tBat, tAudio;
    static volatile bool sessionLocked, displayOff;
    static DisplayWatch displayWatch;
    static void ApplyRates()
    {
        bool idle = sessionLocked || displayOff, battery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        Rate(tWifi, idle ? -1 : battery ? 6000 : 3000);
        Rate(tPing, idle ? -1 : battery ? 5000 : 1000);   // 1 s only on mains with somebody looking
        Rate(tSys, idle ? -1 : battery ? 4000 : 2000);
        Rate(tAudio, idle ? -1 : battery ? 2000 : 1000);
        Rate(tBat, idle ? 5000 : battery ? 2000 : 1000);   // keeps the automatic power mode current even when idle
        var b = big; if (b != null) ui.Post(_ => { if (!b.IsDisposed) b.SetRate(idle ? 0 : battery ? 500 : 250); }, null);
    }
    static void Rate(System.Threading.Timer t, int ms) { if (t == null) return; if (ms < 0) t.Change(Timeout.Infinite, Timeout.Infinite); else t.Change(0, ms); }
    // GUID_CONSOLE_DISPLAY_STATE: Windows sends the current state at once, then every change (0 off, 1 on, 2 dimmed).
    sealed class DisplayWatch : NativeWindow
    {
        [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
        static Guid ConsoleDisplayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
        public DisplayWatch() { CreateHandle(new CreateParams()); RegisterPowerSettingNotification(Handle, ref ConsoleDisplayState, 0 /*DEVICE_NOTIFY_WINDOW_HANDLE*/); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0218 /*WM_POWERBROADCAST*/ && (int)m.WParam == 0x8013 /*PBT_POWERSETTINGCHANGE*/ && m.LParam != IntPtr.Zero)
            {
                // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[] }
                var g = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                if (g == ConsoleDisplayState) { bool off = Marshal.ReadInt32(m.LParam, 20) == 0; if (off != displayOff) { displayOff = off; ApplyRates(); } }
            }
            base.WndProc(ref m);
        }
    }

    static System.Threading.Timer Every(int ms, Action a)
    {
        int[] busy = { 0 };
        return new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref busy[0], 1) == 1) return;
            try { a(); } catch (Exception ex) { Log(a.Method.Name + ": " + ex.Message); } finally { Interlocked.Exchange(ref busy[0], 0); }
        }, null, 0, ms);
    }

    // ---- preview: every state at tray size, zoomed 6x nearest-neighbour, one row per indicator ----
    static void Shot(string file)
    {
        int size = IconSize(), z = 6, pad = 6, cellW = size * z + pad * 2, cellH = size * z + size + pad * 3;
        var rows = new List<List<Bitmap>>();
        Func<Func<int, Bitmap>[], List<Bitmap>> row = fs => { var l = new List<Bitmap>(); foreach (var f in fs) l.Add(f(size)); return l; };
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawWifi(s, -1), s => DrawWifi(s, 1), s => DrawWifi(s, 2), s => DrawWifi(s, 3), s => DrawWifi(s, 4), s => DrawWifi(s, 5),
                                               s => DrawWifi(s, 4, Warning), s => DrawWifi(s, 4, Danger), s => DrawEthernet(s, Light), s => DrawEthernet(s, Warning), s => DrawEthernet(s, Danger) }));
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawPing(s, 7), s => DrawPing(s, 23), s => DrawPing(s, 99), s => DrawPing(s, 150), s => DrawPing(s, 600), s => DrawPing(s, -1) }));
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawSys(s, false, 5), s => DrawSys(s, false, 40), s => DrawSys(s, false, 70), s => DrawSys(s, false, 95),
                                               s => DrawSys(s, true, 5), s => DrawSys(s, true, 40), s => DrawSys(s, true, 70), s => DrawSys(s, true, 95) }));
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawWeatherSymbol(s, 0, false), s => DrawWeatherSymbol(s, 2, false), s => DrawWeatherSymbol(s, 3, false), s => DrawWeatherSymbol(s, 45, false),
                                               s => DrawWeatherSymbol(s, 61, false), s => DrawWeatherSymbol(s, 71, false), s => DrawWeatherSymbol(s, 95, false), s => DrawWeatherSymbol(s, 2, true), s => DrawWeatherSymbol(s, -1, true) }));
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawWeatherTemp(s, 12, false), s => DrawWeatherTemp(s, -7, false), s => DrawWeatherTemp(s, 0, false), s => DrawWeatherTemp(s, 8, true), s => DrawWeatherTemp(s, null, true) }));
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawBattery(s, 100, true), s => DrawBattery(s, 80, false), s => DrawBattery(s, 55, false), s => DrawBattery(s, 30, true), s => DrawBattery(s, 10, false),
                                               s => DrawDiablo(s, "00", BatDigitColour(100), BatText), s => DrawDiablo(s, "55", BatDigitColour(55), BatText), s => DrawDiablo(s, "30", BatDigitColour(30), BatText), s => DrawDiablo(s, "10", BatDigitColour(10), BatText) }));
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawAudio(s, 20, false), s => DrawAudio(s, 50, false), s => DrawAudio(s, 90, false), s => DrawAudio(s, 50, true), s => DrawAudio(s, -1, false), s => DrawGear(s) }));
        rows.Add(row(new Func<int, Bitmap>[] { s => DrawSeg(s, "21"), s => DrawSeg(s, "47"), s => DrawSeg(s, "09"), s => DrawDiablo(s, "24", Foreground, DayText), s => DrawDiablo(s, "05", Foreground, DayText),
                                               s => DrawDiablo(s, "15", Foreground, DayText), s => DrawDiablo(s, "31", Foreground, DayText) }));
        var wk = new List<Func<int, Bitmap>>(); for (int i = 0; i < 7; i++) { int w = i; wk.Add(s => DrawDiabloWord(s, UaWeek[w], UaWeek, WeekdayColour[w], WeekText)); }
        rows.Add(row(wk.ToArray()));
        int cols = 0; foreach (var r in rows) cols = Math.Max(cols, r.Count);
        using (var outBmp = new Bitmap(cols * cellW, rows.Count * cellH))
        using (var g = Graphics.FromImage(outBmp))
        {
            g.Clear(Color.FromArgb(16, 16, 16));
            for (int y = 0; y < rows.Count; y++)
                for (int x = 0; x < rows[y].Count; x++)
                {
                    var b = rows[y][x];
                    g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(b, new Rectangle(x * cellW + pad, y * cellH + pad, size * z, size * z));
                    g.InterpolationMode = InterpolationMode.Default; g.PixelOffsetMode = PixelOffsetMode.Default;
                    g.DrawImageUnscaled(b, x * cellW + (cellW - size) / 2, y * cellH + size * z + pad * 2);
                    b.Dispose();
                }
            outBmp.Save(file, ImageFormat.Png);
        }
    }

    // "--probe out.txt": run every poll once without icons and write the tooltips (live data check, no UI).
    static void Probe(string file)
    {
        var lines = new List<string>();
        ui = new ProbeContext(lines);
        foreach (var f in new[] { "wifi", "ping", "cpu", "ram", "wSym", "wTemp", "bat", "batPct", "audio", "day", "week", "hours", "minutes" })
            typeof(FanaticBar).GetField(f, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, new Slot { Ni = null });
        PollSys(); Thread.Sleep(1000); PollSys(); PollWifi(); PollPing(); PollBattery(); PollAudio(); PollClock();
        weatherTimer = new System.Threading.Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite); PollWeather();
        File.WriteAllLines(file, lines.ToArray());
    }
    sealed class ProbeContext : SynchronizationContext
    {
        readonly List<string> lines; public ProbeContext(List<string> l) { lines = l; }
        public override void Post(SendOrPostCallback d, object state)
        {
            // Show() posts a closure that captures key/tip; read them back through its fields.
            var t = d.Target; if (t == null) return; string tip = null;
            foreach (var f in t.GetType().GetFields()) if (f.Name == "tip") tip = (string)f.GetValue(t);
            if (tip != null) lines.Add(tip.Replace("\n", " | "));
        }
    }

    // fanatic-bar.json next to the exe, e.g. { "gear": false }; read at start (restart the task after editing).
    static bool Setting(string name, bool def)
    {
        try
        {
            var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanatic-bar.json");
            if (!File.Exists(f)) return def;
            var m = Regex.Match(File.ReadAllText(f), "\"" + name + "\"\\s*:\\s*(true|false)");
            return m.Success ? m.Groups[1].Value == "true" : def;
        }
        catch { return def; }
    }

    // String / number settings in fanatic-bar.json (language, location name, lat, lon, tz…). Same regex-JSON idiom as Setting().
    static string SettingStr(string name, string def)
    {
        try
        {
            var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanatic-bar.json");
            if (!File.Exists(f)) return def;
            var m = Regex.Match(File.ReadAllText(f), "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : def;
        }
        catch { return def; }
    }
    static double SettingNum(string name, double def)
    {
        try
        {
            var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanatic-bar.json");
            if (!File.Exists(f)) return def;
            var m = Regex.Match(File.ReadAllText(f), "\"" + name + "\"\\s*:\\s*(-?[0-9.]+)");
            double v; return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, Inv, out v) ? v : def;
        }
        catch { return def; }
    }

    // On event "fanatic.Bar.Rects": write each slot's screen rectangle to fanatic-bar-rects.txt (for click tests).
    [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    static void DumpRects()
    {
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var lines = new List<string>();
        foreach (var f in typeof(FanaticBar).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
        {
            var s = f.FieldType == typeof(Slot) ? (Slot)f.GetValue(null) : null; if (s == null) continue;
            var win = (NativeWindow)typeof(NotifyIcon).GetField("window", bf).GetValue(s.Ni);
            var nid = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = win.Handle, uID = (uint)(int)typeof(NotifyIcon).GetField("id", bf).GetValue(s.Ni) };
            RECT r; int hr = Shell_NotifyIconGetRect(ref nid, out r);
            lines.Add(f.Name + " " + (hr == 0 ? r.Left + " " + r.Top + " " + r.Right + " " + r.Bottom : "hr=0x" + hr.ToString("X")));
        }
        File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanatic-bar-rects.txt"), lines.ToArray());
    }

    // ---- tray order: the shell places (re-)added icons unpredictably and forgets drags on restart, so the block is
    // arranged directly in the notification-area toolbar (classic ToolbarWindow32 under ExplorerPatcher): each button's
    // TRAYDATA (hwnd, uID) is read from Explorer, ours are moved to the end in whitebook order with TB_MOVEBUTTON.
    // Explorer's own icon positions (Shell_NotifyIconGetRect) and click routing follow the move (checked).
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    // A hung Explorer must never hang the bar: 200 ms at most, and no wait at all for a window that is already not responding.
    // Returns 0 on a timeout (callers treat it like a failed call).
    static IntPtr Msg(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        IntPtr r; return SendMessageTimeout(h, msg, w, l, 0x0002 /*SMTO_ABORTIFHUNG*/, 200, out r) == IntPtr.Zero ? IntPtr.Zero : r;
    }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    const uint TB_GETBUTTON = 0x417, TB_BUTTONCOUNT = 0x418, TB_MOVEBUTTON = 0x452;

    static IntPtr TrayToolbar()
    {
        IntPtr tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        return FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
    }
    // "hwnd:uID" of every toolbar button, in toolbar order (x64 TBBUTTON.dwData -> TRAYDATA { HWND; UINT uID; ... }).
    static List<string> TrayButtons(IntPtr tb)
    {
        var res = new List<string>(); uint pid; GetWindowThreadProcessId(tb, out pid);
        IntPtr hp = OpenProcess(0x0008 | 0x0010 | 0x0020 /*VM_OPERATION|VM_READ|VM_WRITE*/, false, pid);
        if (hp == IntPtr.Zero) return res;
        IntPtr mem = VirtualAllocEx(hp, IntPtr.Zero, (IntPtr)64, 0x3000, 4);
        try
        {
            int count = (int)Msg(tb, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            var b = new byte[32]; var t = new byte[16]; IntPtr rd;
            for (int i = 0; i < count; i++)
            {
                string key = "";
                if (Msg(tb, TB_GETBUTTON, (IntPtr)i, mem) != IntPtr.Zero && ReadProcessMemory(hp, mem, b, (IntPtr)32, out rd)
                    && ReadProcessMemory(hp, (IntPtr)BitConverter.ToInt64(b, 16), t, (IntPtr)16, out rd))
                    key = BitConverter.ToInt64(t, 0) + ":" + BitConverter.ToUInt32(t, 8);
                res.Add(key);
            }
        }
        finally { VirtualFreeEx(hp, mem, IntPtr.Zero, 0x8000); CloseHandle(hp); }
        return res;
    }
    // Key of the first tray button owned by process <name> (by its window), or null.
    static string ForeignButton(List<string> keys, string name)
    {
        foreach (var k in keys)
        {
            if (k == "") continue;
            uint pid; GetWindowThreadProcessId((IntPtr)long.Parse(k.Split(':')[0]), out pid);
            try { using (var p = Process.GetProcessById((int)pid)) if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return k; } catch { }
        }
        return null;
    }
    static string SlotKey(Slot s)
    {
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var win = (NativeWindow)typeof(NotifyIcon).GetField("window", bf).GetValue(s.Ni);
        return (long)win.Handle + ":" + (uint)(int)typeof(NotifyIcon).GetField("id", bf).GetValue(s.Ni);
    }
    // Forget the drawn state of every slot and draw them all again (new HICONs through NIM_MODIFY).
    static void RefreshAllIcons()
    {
        foreach (var s in new[] { wifi, ping, cpu, ram, wSym, wTemp, bat, batPct, audio, day, week, hours, minutes }) if (s != null) s.Key = "";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Action a in new Action[] { PollClock, PollWifi, PollBattery, PollAudio, RenderWeather, PollSys, PollPing })
                try { a(); } catch (Exception ex) { Log("refresh: " + ex.Message); }
        });
    }    static void ArrangeTray()
    {
        try
        {
            IntPtr tb = TrayToolbar(); if (tb == IntPtr.Zero) return;
            var have = TrayButtons(tb);
            // The owner's two other tray tools join the block: CPU temperature (fan-tray) left of CPU, layout (keyboard-switch) left of the date.
            var want = new List<string>();
            foreach (var s in new object[] { wifi, ping, "fan-tray", cpu, ram, wSym, wTemp, bat, batPct, audio, "keyboard-switch", day, week, hours, minutes, gear })
            {
                var name = s as string; var slot = s as Slot;
                if (name != null) { var k = ForeignButton(have, name); if (k != null) want.Add(k); }
                else if (slot != null && slot.Ni.Visible) want.Add(SlotKey(slot));
            }
            foreach (var sp in spacers) want.Add(SlotKey(sp));   // big mode: the room of the big indicators, at the right end
            // an icon in the overflow (no button in this toolbar) is a valid place, not a wrong order: leave it out of the check,
            // or the block would be "re-arranged" and every picture pushed again every 30 s
            want.RemoveAll(k => !have.Contains(k));
            int n = have.Count;
            if (n < want.Count) return;
            bool ok = true;   // already the last want.Count buttons, in order?
            for (int i = 0; i < want.Count && ok; i++) ok = have[n - want.Count + i] == want[i];
            if (ok) return;
            foreach (var k in want)
            {
                int i = TrayButtons(tb).IndexOf(k);
                if (i >= 0) Msg(tb, TB_MOVEBUTTON, (IntPtr)i, (IntPtr)(n - 1));
            }
            Log("tray: arranged " + want.Count + " icons");
            RefreshAllIcons();   // icons re-added by Explorer were seen faded (25.10 02:05, cause unknown): push every picture again
        }
        catch (Exception ex) { Log("tray arrange: " + ex.Message); }
    }

    // Each slot gets a fixed icon id (WinForms numbers them 1, 2, 3... in creation order, and the tray
    // keeps old positions for low ids it has seen before), so a re-added icon is always "new" to that memory.
    static int nextUid = 7101;
    static Slot NewSlot(string text, Bitmap first, MouseEventHandler click)
    {
        var ni = new NotifyIcon { Icon = ToIcon(first), Text = text };
        typeof(NotifyIcon).GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(ni, nextUid++);
        ni.Visible = true;
        var s = new Slot { Ni = ni };
        first.Dispose(); s.Ni.MouseClick += click; s.Ni.MouseMove += (o, e) => HoverSlot(s); return s;
    }

    [STAThread]
    static void Main(string[] args)
    {
        // per-monitor v2: Windows never bitmap-stretches our icons; the sizes follow the taskbar's DPI (TaskbarDpi)
        if (!SetProcessDpiAwarenessContext((IntPtr)(-4))) SetProcessDPIAware();
        // localisation (A9): load the chosen language (shared file wins, so the sky's switcher changes the bar too) and
        // fill the date/weekday/month arrays before any rendering path
        Strings.Load(Strings.ResolveLang(SettingStr("language", "en")));
        LoadLocalizedArrays();
        if (args.Length == 2 && args[0] == "--shot") { Shot(args[1]); return; }
        if (args.Length == 2 && args[0] == "--shot-fly") { ShotFly(args[1]); return; }
        if (args.Length == 2 && args[0] == "--probe") { Probe(args[1]); return; }
        if (args.Length == 1 && args[0] == "--quit") { EventWaitHandle ev; if (EventWaitHandle.TryOpenExisting(QuitEvent, out ev)) using (ev) ev.Set(); return; }
        if (args.Length >= 1 && args[0] == "--geo")   // force a location refresh (IP, or a city name after --geo) and write location.json
        {
            bool ok = args.Length >= 2 ? ResolveByCity(string.Join(" ", args, 1, args.Length - 1)) : ResolveByIp();
            Log("--geo ok=" + ok + " place=" + geoPlace + " lat=" + geoLat.ToString(Inv) + " lon=" + geoLon.ToString(Inv) + " tz=" + geoTz);
            return;
        }
        bool created;
        using (var m = new Mutex(true, "fanatic.Bar", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log("ThreadException: " + e.Exception);
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            int sz = IconSize();
            // Left click = the quick action / flyout; right click = a small labelled menu (BigBar.cs holds the shared actions).
            Func<Action<bool, Rectangle>, MouseEventHandler> on = a => (s, e) => a(e.Button == MouseButtons.Right, IconRect((NotifyIcon)s));
            MouseEventHandler net = on(NetClick), tm = on(TaskMgrClick), weather = on(WeatherClick), power = on(PowerClick),
                sound = on(SoundClick), clock = on(ClockClick), cal = on(CalClick);
            // Created right-to-left: the shell puts each new icon to the left of the previous one.
            // whitebook order: wifi · ping · cpu · ram · weather · battery · audio · (layout = keyboard-switch) · date · clock · gear
            minutes = NewSlot(Strings.T("slot.time"), DrawSeg(sz, "88"), clock);
            hours = NewSlot(Strings.T("slot.time"), DrawSeg(sz, "88"), clock);
            week = NewSlot(Strings.T("slot.date"), DrawDash(sz, Muted), cal);
            day = NewSlot(Strings.T("slot.date"), DrawDash(sz, Muted), cal);
            if (Setting("gear", true)) gear = NewSlot(Strings.T("slot.windows_settings"), DrawGear(sz), (s, e) => Open("ms-settings:"));
            if (gear != null) { gear.Draw = DrawGear; Paint(gear); }
            audio = NewSlot(Strings.T("slot.audio"), DrawAudio(sz, -1, false), sound);
            batPct = NewSlot(Strings.T("slot.battery"), DrawDash(sz, Muted), power);
            bat = NewSlot(Strings.T("slot.battery"), DrawBattery(sz, 0, false), power);
            wTemp = NewSlot(Strings.T("slot.weather"), DrawWeatherTemp(sz, null, true), weather);
            wSym = NewSlot(Strings.T("slot.weather"), DrawWeatherSymbol(sz, -1, true), weather);
            ram = NewSlot(Strings.T("slot.memory"), DrawSys(sz, true, 0), tm);
            cpu = NewSlot(Strings.T("slot.cpu"), DrawSys(sz, false, 0), tm);
            ping = NewSlot(Strings.T("slot.ping"), DrawDash(sz, Danger), net);
            wifi = NewSlot("Wi-Fi", DrawWifi(sz, -1), net);
            bigMode = Setting("bigIndicators", true);
            if (bigMode) StartBigMode();   // the native clock: HideClock policy, set / removed elevated (README)
            tWifi = Every(3000, () => { PollIface(); PollWifi(); }); tPing = Every(1000, PollPing); tSys = Every(2000, PollSys);
            tBat = Every(1000, PollBattery); tAudio = Every(1000, PollAudio);
            var timers = new List<System.Threading.Timer> {
                tWifi, tPing, tSys, tBat, tAudio,
                Every(60000, RenderWeather),   // re-marks stale weather
                Every(1000, PollClock)
            };
            // slower on battery, paused while the session is locked or the display is off (Rates)
            Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) { sessionLocked = true; ApplyRates(); }
                else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) { sessionLocked = false; ApplyRates(); }
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode != Microsoft.Win32.PowerModes.Suspend) ApplyRates(); };
            displayWatch = new DisplayWatch();
            ApplyRates();
            weatherTimer = new System.Threading.Timer(_ => PollWeather(), null, 0, Timeout.Infinite);
            // "fanatic-bar.exe --quit" asks for a clean exit: icons are removed, no ghosts left in the tray.
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
            ThreadPool.RegisterWaitForSingleObject(quit, (st, to) => ui.Post(_ => Application.Exit(), null), null, -1, true);
            var reorder = new EventWaitHandle(false, EventResetMode.AutoReset, "fanatic.Bar.Reorder");
            ThreadPool.RegisterWaitForSingleObject(reorder, (st, to) => ui.Post(_ => ArrangeTray(), null), null, -1, false);
            // keep the block in order: shortly after start, then every 30 s (Explorer restarts, battery slots appearing)
            // every 15 s: re-promote our icons if Explorer filed them as new (pref 0, see TrayPromote.cs), then keep the order
            timers.Add(new System.Threading.Timer(_ => { PromoteOwnTrayIcons(() => ui.Post(__ => ArrangeTray(), null)); ui.Post(__ => ArrangeTray(), null); }, null, 2000, 15000));
            // black taskbar (Taskbar.cs): checked every 2 s, re-applied only when Explorer has reset it
            timers.Add(StartAutoPower());   // power mode "Автоматичний" (default): overlay follows AC/DC
            timers.Add(new System.Threading.Timer(_ => { try { CheckIconSize(); } catch (Exception ex) { Log("icon size: " + ex.Message); } }, null, 2000, 2000));
            if (Setting("taskbarBlack", true)) timers.Add(new System.Threading.Timer(_ => { try { KeepTaskbarBlack(); } catch (Exception ex) { Log("taskbar: " + ex.Message); } }, null, 1000, 2000));
            else ReleaseTaskbar();
            var rects = new EventWaitHandle(false, EventResetMode.AutoReset, "fanatic.Bar.Rects");
            ThreadPool.RegisterWaitForSingleObject(rects, (st, to) => ui.Post(_ => DumpRects(), null), null, -1, false);
            var reloadLang = new EventWaitHandle(false, EventResetMode.AutoReset, "fanatic.Bar.ReloadLang");   // sky settings' language picker fires this
            ThreadPool.RegisterWaitForSingleObject(reloadLang, (st, to) => ui.Post(_ => ReloadLang(), null), null, -1, false);
            var hoverTimer = new System.Windows.Forms.Timer { Interval = 150 }; hoverTimer.Tick += (o, e) => CheckHoverLeft(); hoverTimer.Start();
            Application.Run();
            foreach (var t in timers) t.Dispose(); weatherTimer.Dispose();
            foreach (var s in new[] { wifi, ping, cpu, ram, wSym, wTemp, bat, batPct, audio, gear, day, week, hours, minutes }) if (s != null) s.Ni.Visible = false;
            foreach (var s in spacers) s.Ni.Visible = false;
        }
    }
}

// ---- Core Audio (default render endpoint volume / mute) ----
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
