// sky-stars for Windows: a black desktop with slowly twinkling stars, as cheap as a wallpaper.
// Port of the whitebook sky-stars.py + skymap.py (the prototype's sky-src). Kept cheap the same way:
//  * the whole twinkle is a precomputed 30 s loop at 8 fps: per frame only the stars whose step changed are
//    blitted (GDI BitBlt, 6 px each) from a sprite sheet built once; no trigonometry in the frame loop;
//  * catalogue stars (real sky over Kyiv, stereographic zenith projection) are glow sprites, re-evaluated every
//    4 frames ("smoulder") and blitted only when their brightness row moved; positions are recomputed every 30 min;
//  * constellation lines (off by default) and Ukrainian names (optional) are drawn once into the back buffer;
//    settings live in sky-stars.json, edited by "--settings" (desktop context menu "Небо…").
// The window is a layered child of Progman under SHELLDLL_DefView: under the desktop icons, clicks go to the desktop.
// Not ported: the Diablo workspace marks (X11/i3 only).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static partial class SkyStars
{
    // ---- constants (sky-stars.py) ----
    const int LOOP_S = 30, FRAME_MS = 125, FRAMES = LOOP_S * 1000 / FRAME_MS, STEPS = 24;
    const int CELL_W = 20, CELL_H = 28, HALO = 6;
    // ---- settings (sky-stars.json next to the exe; "--settings" writes it and signals fanatic.SkyStars.Reload) ----
    // Owner 25.10 ("the previous iteration"): paler (field 0.5 instead of 0.75, catalogue glows x0.7), smaller glows
    // (x0.7), no constellation lines by default; Ukrainian constellation names as an option (off by default).
    // Defaults = the whitebook sky-stars.py 1:1 (owner 25.10: the desktop is the realistic original; faint lines on).
    // The pale / smaller / no-lines look is used only for the static lock-screen frame ("--shot <png> pale").
    static bool showLines = true, showLabels = false;
    // Black plate behind each constellation name: on by default (owner 26.10, readable over the bright Milky Way), hideable.
    static bool labelBg = true;
    static double lineLevel = 0.16, fieldDim = 0.75, glowDim = 1.0;
    // Milky Way (A10): optional backdrop under the stars that turns with the sky (owner 26.10). Re-projected only on the
    // 18 s sky tick, at full screen resolution with bilinear sampling, into the canvas base (BuildCanvas); never touched per
    // frame, so it costs ~0 while the stars twinkle. Image = milkyway.jpg next to the exe (ESO/S. Brunier), kept downscaled.
    static bool showMilkyway = false; static double milkywayDim = 0.35; static Bitmap milkyBmp;
    static string SettingsFile { get { return Path.Combine(Dir, "sky-stars.json"); } }
    static void LoadSettings()
    {
        string js = ""; try { if (File.Exists(SettingsFile)) js = File.ReadAllText(SettingsFile); } catch { }
        Func<string, bool, bool> b = (k, d) => { var m = Regex.Match(js, "\"" + k + "\"\\s*:\\s*(true|false)"); return m.Success ? m.Groups[1].Value == "true" : d; };
        Func<string, double, double, double, double> n = (k, d, lo, hi) =>
        {
            var m = Regex.Match(js, "\"" + k + "\"\\s*:\\s*([0-9.]+)"); double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? Math.Max(lo, Math.Min(hi, v)) : d;
        };
        showLines = b("lines", true); showLabels = b("labels", false); labelBg = b("labelBg", true);
        lineLevel = n("lineLevel", 0.16, 0.04, 0.4); fieldDim = n("fieldDim", 0.75, 0.2, 1.0); glowDim = n("glowDim", 1.0, 0.3, 1.0);
        showMilkyway = b("milkyway", false); milkywayDim = n("milkywayDim", 0.35, 0.05, 1.0);
        Strings.Load(Strings.ResolveLang("en"));   // shared language (set by the settings' language picker), else English
        try
        {
            string lf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fanatic", "location.json");
            if (File.Exists(lf))
            {
                string lj = File.ReadAllText(lf); double la, lo;
                var a = Regex.Match(lj, "\"lat\"\\s*:\\s*(-?[0-9.]+)"); var o = Regex.Match(lj, "\"lon\"\\s*:\\s*(-?[0-9.]+)");
                if (a.Success && o.Success && double.TryParse(a.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out la)
                    && double.TryParse(o.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lo)) { LAT = la; LON = lo; }
            }
        }
        catch { }
    }
    static void SaveSettings()
    {
        var inv = CultureInfo.InvariantCulture;
        File.WriteAllText(SettingsFile, string.Format(inv, "{{ \"lines\": {0}, \"lineLevel\": {1}, \"labels\": {2}, \"fieldDim\": {3}, \"glowDim\": {4}, \"milkyway\": {5}, \"milkywayDim\": {6}, \"labelBg\": {7} }}\r\n",
            showLines ? "true" : "false", lineLevel, showLabels ? "true" : "false", fieldDim, glowDim, showMilkyway ? "true" : "false", milkywayDim, labelBg ? "true" : "false"));
    }
    static double[][][] SHAPES;
    static void BuildShapes()
    {
        double PEAK = 0.75 * fieldDim, MID = 0.5 * fieldDim, GLOWL = 0.25 * fieldDim, BRIGHT = 1.0 * fieldDim;
        SHAPES = new[] {
            new[] { new double[] { 0, 0, 6, 6, GLOWL }, new double[] { 1, 1, 4, 4, PEAK } },
            new[] { new double[] { 2, 0, 1, 5, GLOWL }, new double[] { 0, 2, 5, 1, GLOWL }, new double[] { 2, 1, 1, 3, MID }, new double[] { 1, 2, 3, 1, MID }, new double[] { 2, 2, 1, 1, PEAK } },
            new[] { new double[] { 1, 0, 3, 5, PEAK }, new double[] { 0, 1, 5, 3, PEAK } },
            new[] { new double[] { 1, 1, 3, 3, PEAK }, new double[] { 2, 2, 1, 1, BRIGHT } },
        };
    }
    static readonly int[] SHAPE_WEIGHTS = { 40, 20, 25, 15 }, TINT_WEIGHTS = { 30, 12, 30, 17, 11 };
    static readonly int VARIANTS = 5 * 4;
    static int[] GLOW_SIZES = { 22, 16, 12, 8 };   // whitebook; x0.7 (15/11/8/6) only in the pale lock-screen frame
    const double GLOW_FALLOFF = 2.2; const int GLOW_CELL = 22;
    static readonly double[] GLOW_LEVELS = { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };
    const int SMOULDER_FRAMES = 4; const double SMOULDER_DEPTH = 0.18, LABEL_LEVEL = 0.32;
    const int SKY_PERIOD_S = 18;          // E4: the catalogue sky is re-projected every 18 s (~1 px per step at 1920 px)
    const int CULL_PERIOD_S = 30 * 60;    // candidates (and precession) every 30 min
    static readonly int[][] DOTS = { new[] { 0, 0 }, new[] { 0, 1 }, new[] { 0, 2 }, new[] { 1, 0 }, new[] { 1, 1 }, new[] { 1, 2 }, new[] { 0, 3 }, new[] { 1, 3 } };
    static readonly Color[] TINTS = { Hex("#dfe8ee"), Hex("#d2e0f2"), Hex("#eee8e0"), Hex("#f0e8ce"), Hex("#f2dfc6") };
    // Observer: the shared %LOCALAPPDATA%\fanatic\location.json written by the bar (auto by IP / "Update location"),
    // so the sky over Dublin is Dublin's sky. Kyiv only as the fallback when there is no file yet.
    static double LAT = 50.45, LON = 30.52;

    static Color Hex(string h) { return ColorTranslator.FromHtml(h); }
    static Color Scale(Color c, double v) { v = Math.Max(0, Math.Min(1, v)); return Color.FromArgb((int)Math.Round(c.R * v), (int)Math.Round(c.G * v), (int)Math.Round(c.B * v)); }
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static void Log(string m) { try { File.AppendAllText(Path.Combine(Dir, "sky-stars.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + m + Environment.NewLine); } catch { } }

    // ---- skymap.py ----
    const double J2000 = 2451545.0;
    static double JulianDay(DateTime utc) { return (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds / 86400.0 + 2440587.5; }
    static double Rad(double d) { return d * Math.PI / 180; }
    static double Mod(double a, double m) { a %= m; return a < 0 ? a + m : a; }
    static double Gmst(double jd) { return Rad(Mod(280.46061837 + 360.98564736629 * (jd - J2000), 360.0)); }
    static double Lst(double jd, double lon) { return Mod(Gmst(jd) + Rad(lon), 2 * Math.PI); }
    static void Precess(ref double ra, ref double dec, double jd)
    {
        double t = (jd - J2000) / 36525.0, m = Rad(1.2812323 * t), n = Rad(0.5567530 * t);
        double r2 = ra + m + n * Math.Sin(ra) * Math.Tan(dec), d2 = dec + n * Math.Cos(ra); ra = r2; dec = d2;
    }
    static void AltAz(double ra, double dec, double lstRad, double lat, out double alt, out double az)
    {
        double ha = lstRad - ra;
        double s = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);
        alt = Math.Asin(Math.Max(-1, Math.Min(1, s)));
        az = Mod(Math.Atan2(-Math.Cos(dec) * Math.Sin(ha), Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(ha)), 2 * Math.PI);
    }
    static void Project(double alt, double az, int w, int h, out double x, out double y)
    {
        double r = 0.39 * w * Math.Tan((Math.PI / 2 - alt) / 2);
        x = w / 2.0 - r * Math.Sin(az); y = h / 2.0 - r * Math.Cos(az);
    }

    class CatStar { public int Hip; public double Ra, Dec, Mag, Bv; }
    static List<CatStar> catalog = new List<CatStar>();
    static List<int[]> linePairs = new List<int[]>();
    static void LoadData()
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(Path.Combine(Dir, "sky-catalog.tsv")))
        {
            if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
            var p = line.Split('\t');
            catalog.Add(new CatStar { Hip = int.Parse(p[0]), Ra = double.Parse(p[1], inv), Dec = double.Parse(p[2], inv), Mag = double.Parse(p[3], inv), Bv = double.Parse(p[4], inv) });
        }
        // {"Aql":[[hip,hip],...],...}: the pairs, and per constellation the set of its stars (for the name labels)
        foreach (Match c in Regex.Matches(File.ReadAllText(Path.Combine(Dir, "sky-lines.json")), "\"(\\w+)\"\\s*:\\s*\\[((?:\\s*\\[\\s*\\d+\\s*,\\s*\\d+\\s*\\]\\s*,?)*)\\s*\\]"))
        {
            var hips = new HashSet<int>();
            foreach (Match m in Regex.Matches(c.Groups[2].Value, @"\[\s*(\d+)\s*,\s*(\d+)\s*\]"))
            {
                int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
                linePairs.Add(new[] { a, b }); hips.Add(a); hips.Add(b);
            }
            figures[c.Groups[1].Value] = hips;
        }
    }
    static readonly Dictionary<string, HashSet<int>> figures = new Dictionary<string, HashSet<int>>();
    // IAU abbreviation -> localised constellation name (strings\<lang>.json key "sky.const.<abbr lower>"); falls back to the abbr.
    static string ConstName(string abbr) { string k = "sky.const." + abbr.ToLowerInvariant(), v = Strings.T(k); return v == k ? abbr : v; }

    // ---- the field ----
    struct FStar { public int X, Y, Row; public ulong H; }
    static int Pick(int[] weights, int roll) { for (int i = 0; i < weights.Length; i++) { if (roll < weights[i]) return i; roll -= weights[i]; } return weights.Length - 1; }
    static int VariantOf(ulong h) { return Pick(TINT_WEIGHTS, (int)((h >> 20) % 100)) * 4 + Pick(SHAPE_WEIGHTS, (int)((h >> 30) % 100)); }
    static int StepOf(ulong h, double t)
    {
        double period = (double)LOOP_S / (4 + (int)(h % 5));
        double phase = Mod(t / period + (h % 997) / 997.0, 1.0);
        double value = Math.Pow(Math.Sin(phase * Math.PI), 12) * 0.55;
        if (value < 0.04) return 0;
        return Math.Max(1, Math.Min(STEPS, (int)Math.Round(value / 0.55 * STEPS, MidpointRounding.ToEven)));
    }
    static int CatalogTint(double bv) { return bv < 0.1 ? 0 : bv < 0.3 ? 1 : bv < 0.8 ? 2 : bv < 1.4 ? 3 : 4; }
    static int GlowSize(double mag) { return GLOW_SIZES[mag < 1.6 ? 0 : mag < 2.6 ? 1 : mag < 3.4 ? 2 : 3]; }
    static double Haze(double x, double y, int w, int h)
    {
        double radius = Math.Sqrt((x - w / 2.0) * (x - w / 2.0) + (y - h / 2.0) * (y - h / 2.0)) / (0.39 * w);
        double alt = Math.PI / 2 - 2 * Math.Atan(radius);
        return Math.Max(0.25, Math.Min(1.0, 0.25 + 0.75 * (alt * 180 / Math.PI + 9) / 11));
    }
    static int LevelOf(double f) { int best = 0; for (int i = 1; i < GLOW_LEVELS.Length; i++) if (Math.Abs(GLOW_LEVELS[i] - f) < Math.Abs(GLOW_LEVELS[best] - f)) best = i; return best; }
    static int Smoulder(int i, double factor, double t)
    {
        int period = 8 + (i * 7) % 7;
        double breath = 1 - SMOULDER_DEPTH * (0.5 + 0.5 * Math.Sin(2 * Math.PI * t / period + i * 2.4));
        return LevelOf(factor * breath);
    }
    static bool NearSegment(int px, int py, int[] s, int pad)
    {
        int x1 = s[0], y1 = s[1], x2 = s[2], y2 = s[3];
        if (px < Math.Min(x1, x2) - pad || px > Math.Max(x1, x2) + pad || py < Math.Min(y1, y2) - pad || py > Math.Max(y1, y2) + pad) return false;
        double dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / l2));
        double ex = px - (x1 + t * dx), ey = py - (y1 + t * dy);
        return Math.Sqrt(ex * ex + ey * ey) <= pad;
    }

    // ---- state ----
    class CStar { public double X, Y, Mag, Haze; public int Tint, Size; }
    static int W, H;
    static List<CStar> cstars = new List<CStar>();
    static int[] clevel = new int[0];
    static List<int[]> segments = new List<int[]>();
    class SkyLabel { public Rectangle Rect; public string Text; }
    static List<SkyLabel> labels = new List<SkyLabel>();
    static Font labelFont;
    static Font LabelFont { get { if (labelFont == null) labelFont = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel); return labelFont; } }
    static FStar[] fstars = new FStar[0];
    static byte[][] steps;        // [frame][star]
    static int[][] changes;       // [frame] -> star indexes whose step differs from the previous frame
    static readonly object gdiLock = new object();
    static IntPtr canvasDC, spriteDC, glowDC, canvasBmp, baseDC, baseBmp;   // base = black + lines + names (no stars)
    static Form form; static IntPtr hwnd;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double Mono() { return clock.Elapsed.TotalSeconds; }
    static int FrameIndex() { return (int)((long)(clock.Elapsed.TotalMilliseconds / FRAME_MS) % FRAMES); }

    // ---- E4 (mailbox\E4-ALGO.md): unit vectors of the precessed catalogue are cached; a tick costs a few multiplications per
    // candidate star (no trigonometry), from DateTime.UtcNow (sleep, NTP steps and manual clock changes need no resync).
    // Same stereographic zenith projection as skymap.py: u = E/(1+Z), v = N/(1+Z), x = W/2 - R u, y = H/2 - R v, R = 0.39 W.
    static double[] vx = new double[0], vy = new double[0], vz = new double[0];
    static int[] candidates = new int[0]; static DateTime cullAt = DateTime.MinValue, lastStatsLog = DateTime.MinValue;
    const double MIN_Z = -0.1564;   // sin(-9 deg): the horizon cut of skymap.positions()
    static void LstTrig(DateTime utc, out double c, out double s)
    {
        double d = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;   // days from JD 2451545.0
        double lst = Mod(280.46061837 + 360.98564736629 * d + LON, 360.0) * Math.PI / 180;
        c = Math.Cos(lst); s = Math.Sin(lst);
    }
    static bool ProjectVec(int i, double c, double s, double sl, double cl, out double x, out double y)
    {
        double a = c * vx[i] + s * vy[i], E = c * vy[i] - s * vx[i], N = vz[i] * cl - sl * a, Z = vz[i] * sl + cl * a;
        x = y = 0; if (Z < MIN_Z) return false;
        double R = 0.39 * W; x = W / 2.0 - R * E / (1 + Z); y = H / 2.0 - R * N / (1 + Z);
        return true;
    }
    // Every 30 min: precess to now, cache the vectors, and keep as candidates the stars that are above -9 deg and inside the
    // screen plus a margin at any of 5 moments over the next 30 min (a whole screen of margin: constellation lines may end off-screen).
    static void Cull(DateTime utc)
    {
        double jd = JulianDay(utc); int n = catalog.Count;
        vx = new double[n]; vy = new double[n]; vz = new double[n];
        for (int i = 0; i < n; i++)
        {
            double ra = catalog[i].Ra, dec = catalog[i].Dec; Precess(ref ra, ref dec, jd);
            vx[i] = Math.Cos(dec) * Math.Cos(ra); vy[i] = Math.Cos(dec) * Math.Sin(ra); vz[i] = Math.Sin(dec);
        }
        double sl = Math.Sin(Rad(LAT)), cl = Math.Cos(Rad(LAT)); var list = new List<int>();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < 5; k++)
            {
                double c, s, x, y; LstTrig(utc.AddMinutes(7.5 * k), out c, out s);
                if (ProjectVec(i, c, s, sl, cl, out x, out y) && x >= -W && x < 2 * W && y >= -W && y < H + W) { list.Add(i); break; }
            }
        candidates = list.ToArray(); cullAt = utc;
    }
    static void ComputeSky()
    {
        var utc = DateTime.UtcNow;
        if (candidates.Length == 0 || Math.Abs((utc - cullAt).TotalSeconds) >= CULL_PERIOD_S) Cull(utc);
        double c0, s0, sl = Math.Sin(Rad(LAT)), cl = Math.Cos(Rad(LAT)); LstTrig(utc, out c0, out s0);
        var byHip = new Dictionary<int, double[]>();
        foreach (int i in candidates)
        {
            double x, y; if (!ProjectVec(i, c0, s0, sl, cl, out x, out y)) continue;
            var c = catalog[i];
            if (x >= -W && x < 2 * W && y >= -W && y < H + W) byHip[c.Hip] = new[] { x, y, c.Mag, c.Bv };
        }
        segments = new List<int[]>();
        foreach (var p in linePairs)
        {
            double[] a, b;
            if (byHip.TryGetValue(p[0], out a) && byHip.TryGetValue(p[1], out b)) segments.Add(new[] { (int)a[0], (int)a[1], (int)b[0], (int)b[1] });
        }
        // name labels: centred on the figure's on-screen stars (at least 2 of them), dim, Consolas
        labels = new List<SkyLabel>();
        if (showLabels)
            foreach (var kv in figures)
            {
                double sx = 0, sy = 0; int k = 0;
                foreach (int hip in kv.Value) { double[] p; if (byHip.TryGetValue(hip, out p) && p[0] >= 0 && p[0] < W && p[1] >= 0 && p[1] < H) { sx += p[0]; sy += p[1]; k++; } }
                if (k < 2) continue;
                string name = ConstName(kv.Key);
                var sz = TextRenderer.MeasureText(name, LabelFont, Size.Empty, TextFormatFlags.NoPadding);
                int lx = (int)(sx / k) - sz.Width / 2, ly = (int)(sy / k) - sz.Height / 2;
                if (lx < 0 || ly < 0 || lx + sz.Width > W || ly + sz.Height > H) continue;
                labels.Add(new SkyLabel { Rect = new Rectangle(lx, ly, sz.Width, sz.Height), Text = name });
            }
        // constellation_positions: brightest first, crowded faint ones shrink or drop; painted faintest first
        var on = new List<double[]>();
        foreach (var s in byHip.Values) if (s[0] >= -11 && s[0] < W + 11 && s[1] >= -11 && s[1] < H + 11) on.Add(s);
        on.Sort((a, b) => a[2].CompareTo(b[2]));
        var placed = new List<CStar>();
        foreach (var s in on)
        {
            int size = GlowSize(s[2]); bool skip = false;
            foreach (var b in placed) { double d2 = (s[0] - b.X) * (s[0] - b.X) + (s[1] - b.Y) * (s[1] - b.Y); if (d2 < Math.Pow(Math.Max(size, b.Size), 2)) { size = GLOW_SIZES[3]; break; } }
            foreach (var b in placed) { double d2 = (s[0] - b.X) * (s[0] - b.X) + (s[1] - b.Y) * (s[1] - b.Y); if (d2 < GLOW_SIZES[3] * GLOW_SIZES[3]) { skip = true; break; } }
            if (skip) continue;
            placed.Add(new CStar { X = s[0], Y = s[1], Mag = s[2], Tint = CatalogTint(s[3]), Size = size, Haze = Haze(s[0], s[1], W, H) });
        }
        placed.Sort((a, b) => b.Mag.CompareTo(a.Mag));
        cstars = placed;
        double t = Mono();
        clevel = new int[cstars.Count];
        for (int i = 0; i < cstars.Count; i++) clevel[i] = Smoulder(i, cstars[i].Haze, t);
    }

    static void Tabulate()
    {
        int cols = (W + CELL_W - 1) / CELL_W, rows = (H + CELL_H - 1) / CELL_H;
        var list = new List<FStar>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                ulong h = (ulong)y * 65537UL + (ulong)x;
                unchecked { h = (h ^ (h >> 16)) * 0x45d9f3bUL; h = (h ^ (h >> 16)) * 0x45d9f3bUL; }
                h ^= h >> 16;
                if (h % 5 != 0) continue;
                var d = DOTS[(int)(h / 161 % 8)];
                int px = (int)((x + (d[0] == 0 ? 0.28 : 0.72)) * CELL_W), py = (int)((y + (d[1] + 0.5) / 4) * CELL_H);
                bool clash = false;
                foreach (var c in cstars) if (Math.Abs(px - c.X) <= c.Size / 2.0 + HALO && Math.Abs(py - c.Y) <= c.Size / 2.0 + HALO) { clash = true; break; }
                if (!clash && showLines) foreach (var s in segments) if (NearSegment(px, py, s, HALO)) { clash = true; break; }
                if (!clash) foreach (var l in labels) if (Rectangle.Inflate(l.Rect, HALO, HALO).Contains(px, py)) { clash = true; break; }
                if (clash) continue;
                list.Add(new FStar { X = px, Y = py, H = h, Row = VariantOf(h) });
            }
        fstars = list.ToArray();
        int n = fstars.Length;
        steps = new byte[FRAMES][];
        for (int f = 0; f < FRAMES; f++) { steps[f] = new byte[n]; double t = f * FRAME_MS / 1000.0; for (int i = 0; i < n; i++) steps[f][i] = (byte)StepOf(fstars[i].H, t); }
        changes = new int[FRAMES][]; long total = 0;
        for (int f = 0; f < FRAMES; f++)
        {
            var now = steps[f]; var before = steps[(f + FRAMES - 1) % FRAMES]; var ch = new List<int>();
            for (int i = 0; i < n; i++) if (now[i] != before[i]) ch.Add(i);
            changes[f] = ch.ToArray(); total += ch.Count;
        }
        if ((DateTime.UtcNow - lastStatsLog).TotalSeconds >= CULL_PERIOD_S) { lastStatsLog = DateTime.UtcNow;   // every 18 s would flood the log
            Log(n + " stars on " + cols + "x" + rows + " cells, " + cstars.Count + " catalogue stars of " + candidates.Length + " candidates, " + segments.Count + " lines, " + (total / FRAMES) + " changes/frame"); }
    }

    // ---- sprite sheets (built once with GDI+, then selected into GDI memory DCs) ----
    static Bitmap BuildSprites()
    {
        var bmp = new Bitmap((STEPS + 1) * HALO, VARIANTS * HALO);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            for (int v = 0; v < VARIANTS; v++)
            {
                int tint = v / 4, kind = v % 4;
                for (int s = 1; s <= STEPS; s++)
                    foreach (var r in SHAPES[kind])
                        using (var b = new SolidBrush(Scale(TINTS[tint], Math.Min(1.0, (double)s / STEPS * r[4]))))
                            g.FillRectangle(b, s * HALO + (int)r[0], v * HALO + (int)r[1], (int)r[2], (int)r[3]);
            }
        }
        return bmp;
    }
    static Bitmap BuildGlows()
    {
        var bmp = new Bitmap(GLOW_CELL * GLOW_SIZES.Length, GLOW_CELL * TINTS.Length * GLOW_LEVELS.Length);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Black);
        for (int level = 0; level < GLOW_LEVELS.Length; level++)
            for (int tint = 0; tint < TINTS.Length; tint++)
            {
                int oy = (level * TINTS.Length + tint) * GLOW_CELL;
                for (int kind = 0; kind < GLOW_SIZES.Length; kind++)
                {
                    int size = GLOW_SIZES[kind], ox = kind * GLOW_CELL; double centre = (size - 1) / 2.0, core = size > 12 ? 1.5 : 1.0;
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            double radius = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre)) / (size / 2.0);
                            double strength = Math.Pow(Math.Max(0, 1 - radius), GLOW_FALLOFF) * GLOW_LEVELS[level] * glowDim;
                            if (Math.Abs(x - centre) <= core && Math.Abs(y - centre) <= core) strength = GLOW_LEVELS[level] * glowDim;
                            if (strength > 0) bmp.SetPixel(ox + x, oy + y, Scale(TINTS[tint], strength));
                        }
                }
            }
        return bmp;
    }

    // Full back buffer: black, lines, the field at the current frame, the catalogue stars.
    static void BuildCanvas()
    {
        int f = FrameIndex();
        using (var bmp = new Bitmap(W, H))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                if (showMilkyway && RenderMilkyway()) g.DrawImageUnscaled(milkyBmp, 0, 0);   // backdrop under lines+stars
                if (showLines) using (var pen = new Pen(Scale(TINTS[0], lineLevel), 1)) foreach (var s in segments) g.DrawLine(pen, s[0], s[1], s[2], s[3]);
                foreach (var l in labels)
                    TextRenderer.DrawText(g, l.Text, LabelFont, l.Rect.Location, Scale(TINTS[0], LABEL_LEVEL), labelBg ? Color.Black : Color.Transparent, TextFormatFlags.NoPadding);
            }
            IntPtr hb = bmp.GetHbitmap(), hbase = bmp.GetHbitmap();
            lock (gdiLock)
            {
                IntPtr old = SelectObject(canvasDC, hb);
                if (canvasBmp != IntPtr.Zero) DeleteObject(canvasBmp); else if (old != IntPtr.Zero) { }
                canvasBmp = hb;
                if (baseDC == IntPtr.Zero) baseDC = CreateCompatibleDC(IntPtr.Zero);
                SelectObject(baseDC, hbase); if (baseBmp != IntPtr.Zero) DeleteObject(baseBmp); baseBmp = hbase;
                for (int i = 0; i < fstars.Length; i++) BlitStar(canvasDC, i, steps[f][i]);
                for (int i = 0; i < cstars.Count; i++) BlitGlow(canvasDC, i);
            }
        }
    }
    static void BlitStar(IntPtr dc, int i, int step)
    {
        var s = fstars[i]; int r = HALO / 2;
        // Default: SRCCOPY the sprite cell (its black background is invisible over the black canvas) — the fast path.
        // With the Milky Way backdrop the cell's black would erase it, so restore the base (backdrop+lines) then OR the
        // star, exactly like BlitGlow. Both sprite backgrounds are pure black, so the OR keeps the backdrop intact.
        if (showMilkyway && milkyBmp != null)
        {
            BitBlt(dc, s.X - r, s.Y - r, HALO, HALO, baseDC, s.X - r, s.Y - r, SRCCOPY);
            BitBlt(dc, s.X - r, s.Y - r, HALO, HALO, spriteDC, step * HALO, s.Row * HALO, SRCPAINT);
        }
        else BitBlt(dc, s.X - r, s.Y - r, HALO, HALO, spriteDC, step * HALO, s.Row * HALO, SRCCOPY);
    }
    static void BlitGlow(IntPtr dc, int i)
    {
        // The glow sprite is a black square: copied over the lines and names it rubbed black holes into them (owner, 25.10).
        // Now the square first gets the base (black + lines + names) back, then the glow is OR-ed on top.
        var c = cstars[i]; int kind = Array.IndexOf(GLOW_SIZES, c.Size), x = (int)(c.X - c.Size / 2.0), y = (int)(c.Y - c.Size / 2.0);
        BitBlt(dc, x, y, c.Size, c.Size, baseDC, x, y, SRCCOPY);
        BitBlt(dc, x, y, c.Size, c.Size, glowDC, kind * GLOW_CELL, (clevel[i] * TINTS.Length + c.Tint) * GLOW_CELL, SRCPAINT);
    }

    // ---- frame loop (own thread; GDI only, under gdiLock) ----
    static volatile bool running = true;
    static void Loop()
    {
        int last = FrameIndex(), watch = 0; double lastSky = Mono(); DateTime lastWall = DateTime.UtcNow;
        while (running)
        {
            double ms = clock.Elapsed.TotalMilliseconds;
            int wait = (int)(FRAME_MS - ms % FRAME_MS) + 1;
            Thread.Sleep(wait);
            if (!running) break;
            var wall = DateTime.UtcNow;
            bool jumped = Math.Abs((wall - lastWall).TotalSeconds - wait / 1000.0) > 300; lastWall = wall;   // sleep/resume
            if (reload) { reload = false; LoadSettings(); BuildShapes(); Tabulate(); BuildSheets(); Log("settings reloaded"); jumped = true; }
            if (jumped || Mono() - lastSky >= SKY_PERIOD_S) { Refresh(); lastSky = Mono(); last = FrameIndex(); continue; }
            if (++watch % 16 == 0) Watchdog();   // every 2 s
            int f = FrameIndex(); if (f == last) continue;
            IntPtr wdc = GetDC(hwnd);
            try
            {
                lock (gdiLock)
                {
                    if (((f - last + FRAMES) % FRAMES) == 1) foreach (int i in changes[f]) { BlitStar(canvasDC, i, steps[f][i]); BlitStar(wdc, i, steps[f][i]); }
                    else for (int i = 0; i < fstars.Length; i++) { BlitStar(canvasDC, i, steps[f][i]); BlitStar(wdc, i, steps[f][i]); }
                    if (f % SMOULDER_FRAMES == 0)
                    {
                        double t = Mono();
                        for (int i = 0; i < cstars.Count; i++)
                        {
                            int lv = Smoulder(i, cstars[i].Haze, t);
                            if (lv != clevel[i]) { clevel[i] = lv; BlitGlow(canvasDC, i); BlitGlow(wdc, i); }
                        }
                    }
                }
            }
            finally { ReleaseDC(hwnd, wdc); }
            last = f;
        }
    }
    // Explorer restart: a new Progman appears and our window is left behind (no destroy message reaches us).
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    static void Watchdog()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero || GetParent(hwnd) == progman) return;
        if (!IsWindow(hwnd))
        {   // relaunch ourselves (Task Scheduler does not restart on an exit code): the new copy waits for our mutex and the new desktop
            Log("desktop window gone - relaunching");
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restart") { UseShellExecute = false }); } catch (Exception ex) { Log("relaunch: " + ex.Message); }
            Environment.Exit(1);
        }
        Log("Progman changed - re-attaching");
        Ui(() => { Attach(hwnd); form.Invalidate(); });
    }
    static void Refresh()
    {
        ComputeSky(); Tabulate(); BuildCanvas();
        Ui(() => form.Invalidate());
    }
    // BeginInvoke from a worker thread, but never onto a dead window: the window can die with a killed Explorer
    // (it is a Progman child), and an unguarded BeginInvoke then crashed the process (24.10 22:42, "Unknown Hard Error").
    static void Ui(Action a)
    {
        try { var f = form; if (f != null && !f.IsDisposed && f.IsHandleCreated) f.BeginInvoke(a); }
        catch (InvalidOperationException ex) { Log("ui: " + ex.Message); }
    }
    static volatile bool reload;
    static IntPtr spriteBmp, glowBmp;
    // (Re)build both sprite sheets into their memory DCs; the previous bitmaps are freed.
    static void BuildSheets()
    {
        using (var sp = BuildSprites()) using (var gl = BuildGlows())
        {
            IntPtr s = sp.GetHbitmap(), g = gl.GetHbitmap();
            lock (gdiLock)
            {
                if (spriteDC == IntPtr.Zero) spriteDC = CreateCompatibleDC(IntPtr.Zero);
                if (glowDC == IntPtr.Zero) glowDC = CreateCompatibleDC(IntPtr.Zero);
                SelectObject(spriteDC, s); SelectObject(glowDC, g);
                if (spriteBmp != IntPtr.Zero) DeleteObject(spriteBmp);
                if (glowBmp != IntPtr.Zero) DeleteObject(glowBmp);
                spriteBmp = s; glowBmp = g;
            }
        }
        BuildMilkyway();
    }

    // The panorama (equirectangular, galactic coordinates, centre l=0 b=0, l growing to the LEFT) is decoded once and kept
    // at MW_W x MW_H as BGR bytes: 11.4 px per degree, about the screen's own density at mid altitudes (24 MB instead of the
    // 72 MB full 6000x3000 decode, which is freed at once). Owner 26.10: the half-resolution 2048 px layer looked blocky.
    const int MW_W = 4096, MW_H = 2048;
    static byte[] milkySrc; static byte[] milkyRow;
    static void BuildMilkyway()
    {
        try
        {
            if (milkyBmp != null) { milkyBmp.Dispose(); milkyBmp = null; }
            if (!showMilkyway || W <= 0 || H <= 0) { milkySrc = null; milkyRow = null; return; }
            if (milkySrc != null) return;
            string p = Path.Combine(Dir, "milkyway.jpg");
            if (!File.Exists(p)) { Log("milkyway.jpg missing"); return; }
            using (var small = new Bitmap(MW_W, MW_H, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(small)) using (var src = new Bitmap(p))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, MW_W, MW_H));
                }
                var d = small.LockBits(new Rectangle(0, 0, MW_W, MW_H), System.Drawing.Imaging.ImageLockMode.ReadOnly, small.PixelFormat);
                var buf = new byte[MW_W * MW_H * 3];
                for (int y = 0; y < MW_H; y++) Marshal.Copy(d.Scan0 + y * d.Stride, buf, y * MW_W * 3, MW_W * 3);
                small.UnlockBits(d); milkySrc = buf;
            }
            GC.Collect();   // the 72 MB full-size decode is garbage now; give it back at once
        }
        catch (Exception ex) { Log("milkyway: " + ex.Message); milkySrc = null; }
    }
    // Project the panorama onto the screen for NOW, exactly inverse to ProjectVec (stereographic zenith projection):
    // screen pixel -> horizon vector -> equatorial (rotated by the local sidereal time) -> galactic -> panorama, sampled
    // bilinearly. Once per 18 s sky tick, row by row straight into the bitmap. Returns false when there is nothing to draw.
    static DateTime milkyStatsAt;
    static bool RenderMilkyway()
    {
        if (milkySrc == null) return false;
        try
        {
            var sw = Stopwatch.StartNew();
            if (milkyBmp == null || milkyBmp.Width != W || milkyBmp.Height != H)
            {
                if (milkyBmp != null) milkyBmp.Dispose();
                milkyBmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            if (milkyRow == null || milkyRow.Length != W * 3) milkyRow = new byte[W * 3];
            double c, s; LstTrig(DateTime.UtcNow, out c, out s);
            double sl = Math.Sin(Rad(LAT)), cl = Math.Cos(Rad(LAT)), R = 0.39 * W, fade = 0.15;
            int dim = (int)Math.Round(Math.Max(0, Math.Min(1, milkywayDim)) * 256);
            const double U = MW_W / (2 * Math.PI), V = MW_H / Math.PI;
            var src = milkySrc; var row = milkyRow; const int SR = MW_W * 3;
            var d = milkyBmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.WriteOnly, milkyBmp.PixelFormat);
            try
            {
                for (int y = 0; y < H; y++)
                {
                    double dy = (H / 2.0 - (y + 0.5)) / R;
                    for (int x = 0; x < W; x++)
                    {
                        double dx = (W / 2.0 - (x + 0.5)) / R, q = dx * dx + dy * dy, k = 1 / (1 + q);
                        double Z = (1 - q) * k; int o = x * 3;
                        if (Z < MIN_Z) { row[o] = row[o + 1] = row[o + 2] = 0; continue; }
                        double E = 2 * dx * k, N = 2 * dy * k;
                        double a = cl * Z - sl * N, vz = sl * Z + cl * N, vx = c * a - s * E, vy = s * a + c * E;
                        double gx = -0.0548755604 * vx - 0.8734370902 * vy - 0.4838350155 * vz;
                        double gy = 0.4941094279 * vx - 0.4448296300 * vy + 0.7469822445 * vz;
                        double gz = -0.8676661490 * vx - 0.1980763734 * vy + 0.4559837762 * vz;
                        // texel centres at +0.5: fu/fv are positions in texel space; wrap in longitude, clamp at the poles
                        double fu = (Math.PI - Math.Atan2(gy, gx)) * U - 0.5, fv = (Math.PI / 2 - Math.Asin(Math.Max(-1, Math.Min(1, gz)))) * V - 0.5;
                        int x0 = (int)Math.Floor(fu), y0 = (int)Math.Floor(fv);
                        int wx = (int)((fu - x0) * 256), wy = (int)((fv - y0) * 256);
                        int xa = x0 < 0 ? x0 + MW_W : (x0 >= MW_W ? x0 - MW_W : x0), xb = xa + 1 == MW_W ? 0 : xa + 1;
                        int ya = y0 < 0 ? 0 : (y0 >= MW_H ? MW_H - 1 : y0), yb = y0 + 1 >= MW_H ? MW_H - 1 : (y0 + 1 < 0 ? 0 : y0 + 1);
                        int p00 = ya * SR + xa * 3, p01 = ya * SR + xb * 3, p10 = yb * SR + xa * 3, p11 = yb * SR + xb * 3;
                        int m = Z < MIN_Z + fade ? (int)(dim * (Z - MIN_Z) / fade) : dim;   // fade out towards the horizon cut
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int top = src[p00 + ch] * (256 - wx) + src[p01 + ch] * wx, bot = src[p10 + ch] * (256 - wx) + src[p11 + ch] * wx;
                            int v = ((top >> 8) * (256 - wy) + (bot >> 8) * wy) >> 8;
                            row[o + ch] = (byte)(v * m >> 8);
                        }
                    }
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, W * 3);
                }
            }
            finally { milkyBmp.UnlockBits(d); }
            if ((DateTime.UtcNow - milkyStatsAt).TotalSeconds >= CULL_PERIOD_S) { milkyStatsAt = DateTime.UtcNow; Log("milky way rendered in " + sw.ElapsedMilliseconds + " ms"); }
            return true;
        }
        catch (Exception ex) { Log("milkyway render: " + ex.Message); return false; }
    }

    // ---- the desktop window: WorkerW behind the icons ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    // per-monitor v2: after a live display-scale change Windows virtualized the system-aware window (CPU x4, 25.10)
    static void PerMonitorDpi() { if (!SetProcessDpiAwarenessContext((IntPtr)(-4))) SetProcessDPIAware(); }
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint action, uint param, string pv, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    const uint SRCCOPY = 0x00CC0020, SRCPAINT = 0x00EE0086;

    // Win11 24H2+ (this build): Progman holds SHELLDLL_DefView (icons, on top) and a WorkerW (wallpaper) as children;
    // our window becomes a Progman child placed right under DefView, i.e. above the wallpaper and below the icons.
    // Older shells: a top-level WorkerW behind the one holding DefView.
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    static void Attach(IntPtr me)
    {
        IntPtr progman = FindWindow("Progman", null), r;
        SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)0x1, 0, 1000, out r);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out r);
        IntPtr defview = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        int style = GetWindowLong(me, -16);
        SetWindowLong(me, -16, (style & ~unchecked((int)0x80000000) /*WS_POPUP*/) | 0x40000000 /*WS_CHILD*/);
        if (defview != IntPtr.Zero)
        {
            SetParent(me, progman);
            SetWindowLong(me, -20, GetWindowLong(me, -20) | 0x80000 | 0x20 /*WS_EX_LAYERED + WS_EX_TRANSPARENT: click-through so desktop right-click never hits us*/);
            SetLayeredWindowAttributes(me, 0, 255, 2 /*LWA_ALPHA*/);
            SetWindowPos(me, defview, 0, 0, W, H, 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
            Log("desktop layer: child of Progman under SHELLDLL_DefView, parent=" + GetParent(me) + " progman=" + progman);
            return;
        }
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) found = FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        SetParent(me, found != IntPtr.Zero ? found : progman);
        SetWindowPos(me, IntPtr.Zero, 0, 0, W, H, 0x0010 | 0x0040);
        Log("desktop layer: top-level WorkerW " + found);
    }
    sealed class SkyForm : Form
    {
        public SkyForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(0, 0, W, H);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x20 /*WS_EX_TRANSPARENT*/; return cp; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            IntPtr dc = e.Graphics.GetHdc();
            try { lock (gdiLock) { var r = e.ClipRectangle; BitBlt(dc, r.X, r.Y, r.Width, r.Height, canvasDC, r.X, r.Y, SRCCOPY); } }
            finally { e.Graphics.ReleaseHdc(dc); }
        }
    }

    // "--shot out.png [lines,labels]": the current settings (plus the listed overrides) rendered off-screen at screen size.
    static void Shot(string file, string force)
    {
        PerMonitorDpi(); W = GetSystemMetrics(0); H = GetSystemMetrics(1);
        LoadSettings(); if (force.Contains("lines")) showLines = true; if (force.Contains("labels")) showLabels = true;
        if (force.Contains("pale"))   // the lock-screen frame: paler, smaller glows, no lines, no names (phase-5 look)
        { fieldDim = 0.5; glowDim = 0.7; showLines = false; showLabels = false; GLOW_SIZES = new[] { 15, 11, 8, 6 }; }
        var size = Regex.Match(force, @"(\d+)x(\d+)"); if (size.Success) { W = int.Parse(size.Groups[1].Value); H = int.Parse(size.Groups[2].Value); }
        BuildShapes(); LoadData(); ComputeSky(); Tabulate(); BuildSheets();
        canvasDC = CreateCompatibleDC(IntPtr.Zero); BuildCanvas();
        using (var bmp = new Bitmap(W, H))
        {
            using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); BitBlt(dc, 0, 0, W, H, canvasDC, 0, 0, SRCCOPY); g.ReleaseHdc(dc); }
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--quit") { EventWaitHandle ev; if (EventWaitHandle.TryOpenExisting("fanatic.SkyStars.Quit", out ev)) using (ev) ev.Set(); return; }
        if (args.Length == 1 && args[0] == "--settings") { SettingsWindow.Run(); return; }
        if (args.Length == 2 && args[0] == "--shot-settings") { SettingsWindow.Shot(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--shot") { Shot(args[1], args.Length > 2 ? args[2] : ""); return; }
        bool created;
        using (var m = new Mutex(true, "fanatic.SkyStars", out created))
        {
            if (!created && args.Length == 1 && args[0] == "--restart")
            {   // after an Explorer restart: wait for the old copy to exit, then for the new desktop (Progman with DefView)
                try { created = m.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
                for (int i = 0; i < 60 && FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero; i++) Thread.Sleep(500);
                Thread.Sleep(2000);
            }
            if (!created) return;
            PerMonitorDpi();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log("ThreadException: " + e.Exception);
            W = GetSystemMetrics(0); H = GetSystemMetrics(1);
            var t0 = Mono();
            LoadSettings(); BuildShapes();
            LoadData(); ComputeSky(); Tabulate();
            BuildSheets();
            canvasDC = CreateCompatibleDC(IntPtr.Zero);
            BuildCanvas();
            Log(string.Format(CultureInfo.InvariantCulture, "start {0}x{1}, built in {2:0.00} s", W, H, Mono() - t0));
            form = new SkyForm();
            form.HandleCreated += (s, e) => { hwnd = form.Handle; };
            form.Shown += (s, e) => { Attach(hwnd); new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start(); };
            // "--quit": a named event asks for a clean exit, then the wallpaper is repainted
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "fanatic.SkyStars.Quit");
            ThreadPool.RegisterWaitForSingleObject(quit, (st, to) => Ui(() => form.Close()), null, -1, true);
            // "--settings" saved new settings: the frame loop reloads them (sprites, glows, lines, labels)
            var reloadEv = new EventWaitHandle(false, EventResetMode.AutoReset, ReloadEvent);
            ThreadPool.RegisterWaitForSingleObject(reloadEv, (st, to) => { reload = true; }, null, -1, false);
            // Explorer restart destroys Progman and our child window with it: exit with an error so the task
            // (restart on failure, 1 min) starts us again on the new desktop
            bool quitting = false;
            form.FormClosing += (s, e) => quitting = true;
            form.HandleDestroyed += (s, e) => { if (!quitting) { Log("desktop window destroyed (Explorer restart?) - exit 1"); Environment.Exit(1); } };
            Application.Run(form);
            running = false;
            SystemParametersInfo(0x0014 /*SPI_SETDESKWALLPAPER*/, 0, null, 0);   // repaint the original wallpaper
        }
    }
}
