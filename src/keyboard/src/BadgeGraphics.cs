// SVG parser shared in behaviour with fanatic-bar/FanaticBar.cs; text layout comes from TextFont.
// Mainframe and the Diablo fallback glyph data are compiled in, without font installs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

static class BadgeGraphics
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
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

    static GraphicsPath DiabloText(string text, out float width) { return TextFont.Text(text, Svg, out width); }
    public static float Width(float unit)
    {
        float widest = 0, width;
        foreach (string code in new[] { "UA", "RU", "EN" })
            using (DiabloText(code, out width)) widest = Math.Max(widest, width * 20.4f / TextFont.CAP);
        return (widest + 28) * unit;
    }
    public static Size Size(float unit) { return new Size((int)Math.Ceiling(Width(unit)), (int)Math.Ceiling(36 * unit)); }
    public static void Draw(Graphics g, string code, Color colour, float unit)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float w = Width(unit), h = 36 * unit, cap = 20.4f * unit, scale = cap / TextFont.CAP, width;
        using (var frame = Rounded(unit, unit, w - 2 * unit, h - 2 * unit, 3 * unit))
        using (var pen = new Pen(Color.FromArgb(0x5b, 0x6b, 0x76), 2 * unit)) g.DrawPath(pen, frame);
        using (var p = DiabloText(code, out width))
        using (var m = new Matrix(scale, 0, 0, scale, (w - width * scale) / 2, (h - cap) / 2))
        using (var b = new SolidBrush(colour)) { p.Transform(m); g.FillPath(b, p); }
    }
    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}
