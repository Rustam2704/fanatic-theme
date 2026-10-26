// ttf2cs: TrueType outlines -> C# glyph table for the bar (like DiabloGlyphs.cs from glyphs.json).
// Usage: ttf2cs <font.ttf> <out.cs> <class> <chars>   (or: ttf2cs <font.ttf> --info)
// Reads head/maxp/loca/glyf/hmtx/cmap (format 4); simple and composite glyphs (offsets only). Paths are SVG M/L/Q/Z in font units,
// y up, shifted so the ink bottom of 'Н' sits on BASE 64 (as glyphs.json); --info prints the metrics to check the cap height.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

static class Ttf2Cs
{
    static byte[] f; static Dictionary<string, int> tab = new Dictionary<string, int>();
    static int U16(int o) { return (f[o] << 8) | f[o + 1]; }
    static int S16(int o) { return (short)U16(o); }
    static int U32(int o) { return (int)(((uint)f[o] << 24) | ((uint)f[o + 1] << 16) | ((uint)f[o + 2] << 8) | f[o + 3]); }
    static int unitsPerEm, numGlyphs, longLoca, numHMetrics;

    static int GlyphIndex(int cp)
    {
        int cmap = tab["cmap"], n = U16(cmap + 2);
        for (int i = 0; i < n; i++)
        {
            int pid = U16(cmap + 4 + i * 8), eid = U16(cmap + 6 + i * 8), off = cmap + U32(cmap + 8 + i * 8);
            if (pid != 3 || (eid != 1 && eid != 10) || U16(off) != 4) continue;
            int segX2 = U16(off + 6), ends = off + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
            for (int s = 0; s < segX2 / 2; s++)
            {
                int end = U16(ends + 2 * s), start = U16(starts + 2 * s);
                if (cp > end || cp < start) continue;
                int ro = U16(ranges + 2 * s), delta = S16(deltas + 2 * s);
                if (ro == 0) return (cp + delta) & 0xFFFF;
                int gi = U16(ranges + 2 * s + ro + 2 * (cp - start));
                return gi == 0 ? 0 : (gi + delta) & 0xFFFF;
            }
        }
        return 0;
    }
    static int Loca(int gi) { int l = tab["loca"]; return longLoca != 0 ? U32(l + 4 * gi) : U16(l + 2 * gi) * 2; }
    static int Advance(int gi) { int h = tab["hmtx"]; return U16(h + 4 * Math.Min(gi, numHMetrics - 1)); }

    // contours of glyph gi: lists of (x, y, onCurve)
    static List<List<int[]>> Contours(int gi, int dx, int dy)
    {
        var res = new List<List<int[]>>();
        int o = tab["glyf"] + Loca(gi), len = Loca(gi + 1) - Loca(gi);
        if (len <= 0) return res;
        int nc = S16(o);
        if (nc < 0)
        {
            int p = o + 10, flags;
            do
            {
                flags = U16(p); int sub = U16(p + 2); p += 4; int ax, ay;
                if ((flags & 1) != 0) { ax = S16(p); ay = S16(p + 2); p += 4; } else { ax = (sbyte)f[p]; ay = (sbyte)f[p + 1]; p += 2; }
                if ((flags & 2) == 0) throw new Exception("composite with point matching in glyph " + gi);
                if ((flags & 8) != 0) p += 2; else if ((flags & 0x40) != 0) p += 4; else if ((flags & 0x80) != 0) p += 8;
                if ((flags & 0xC8) != 0) Console.Error.WriteLine("warning: scaled component in glyph " + gi + " (scale ignored)");
                res.AddRange(Contours(sub, dx + ax, dy + ay));
            } while ((flags & 0x20) != 0);
            return res;
        }
        var endPts = new int[nc]; for (int i = 0; i < nc; i++) endPts[i] = U16(o + 10 + 2 * i);
        int np = nc == 0 ? 0 : endPts[nc - 1] + 1, q = o + 10 + 2 * nc; q += 2 + U16(q);
        var fl = new byte[np];
        for (int i = 0; i < np; )
        {
            byte b = f[q++]; fl[i++] = b;
            if ((b & 8) != 0) { int r = f[q++]; while (r-- > 0) fl[i++] = b; }
        }
        var xs = new int[np]; var ys = new int[np]; int v = 0;
        for (int i = 0; i < np; i++) { if ((fl[i] & 2) != 0) { int d = f[q++]; v += (fl[i] & 16) != 0 ? d : -d; } else if ((fl[i] & 16) == 0) { v += S16(q); q += 2; } xs[i] = v; }
        v = 0;
        for (int i = 0; i < np; i++) { if ((fl[i] & 4) != 0) { int d = f[q++]; v += (fl[i] & 32) != 0 ? d : -d; } else if ((fl[i] & 32) == 0) { v += S16(q); q += 2; } ys[i] = v; }
        int st = 0;
        foreach (int e in endPts)
        {
            var c = new List<int[]>(); for (int i = st; i <= e; i++) c.Add(new[] { xs[i] + dx, ys[i] + dy, fl[i] & 1 });
            res.Add(c); st = e + 1;
        }
        return res;
    }

    static string N(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    static string PathOf(List<List<int[]>> cs, int shift, out bool curves)
    {
        var sb = new StringBuilder(); curves = false;
        foreach (var c in cs)
        {
            if (c.Count == 0) continue;
            // start on an on-curve point (or the midpoint of two off-curve ones)
            int k = c.FindIndex(p => p[2] == 1); double sx, sy;
            var pts = new List<int[]>();
            if (k < 0) { sx = (c[0][0] + c[c.Count - 1][0]) / 2.0; sy = (c[0][1] + c[c.Count - 1][1]) / 2.0; pts = c; }
            else { for (int i = 0; i < c.Count; i++) pts.Add(c[(k + i) % c.Count]); sx = pts[0][0]; sy = pts[0][1]; pts = pts.Skip(1).ToList(); }
            sb.Append("M" + N(sx) + " " + N(sy + shift));
            pts.Add(new[] { (int)sx, (int)sy, 1 }); // close back (sx/sy are integers unless all points are off-curve)
            int[] ctrl = null;
            foreach (var p in pts)
            {
                if (p[2] == 1)
                {
                    if (ctrl != null) { sb.Append("Q" + N(ctrl[0]) + " " + N(ctrl[1] + shift) + " " + N(p[0]) + " " + N(p[1] + shift)); ctrl = null; curves = true; }
                    else sb.Append("L" + N(p[0]) + " " + N(p[1] + shift));
                }
                else
                {
                    if (ctrl != null)
                    {
                        double mx = (ctrl[0] + p[0]) / 2.0, my = (ctrl[1] + p[1]) / 2.0;
                        sb.Append("Q" + N(ctrl[0]) + " " + N(ctrl[1] + shift) + " " + N(mx) + " " + N(my + shift)); curves = true;
                    }
                    ctrl = p;
                }
            }
            sb.Append("Z");
        }
        return sb.ToString();
    }
    static void Bounds(List<List<int[]>> cs, out int x0, out int x1, out int y0, out int y1)
    {
        x0 = y0 = int.MaxValue; x1 = y1 = int.MinValue;
        foreach (var c in cs) foreach (var p in c) { x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]); y0 = Math.Min(y0, p[1]); y1 = Math.Max(y1, p[1]); }
    }

    static int Main(string[] a)
    {
        if (a.Length < 2 || (a[1] != "--info" && a.Length < 4)) { Console.Error.WriteLine("usage: ttf2cs <font.ttf> --info | ttf2cs <font.ttf> <out.cs> <class> <chars>"); return 2; }
        f = File.ReadAllBytes(a[0]);
        int n = U16(4); for (int i = 0; i < n; i++) tab[Encoding.ASCII.GetString(f, 12 + 16 * i, 4)] = U32(12 + 16 * i + 8);
        unitsPerEm = U16(tab["head"] + 18); longLoca = S16(tab["head"] + 50); numGlyphs = U16(tab["maxp"] + 4); numHMetrics = U16(tab["hhea"] + 34);
        int hn = GlyphIndex('Н'); int bx0, bx1, by0, by1; Bounds(Contours(hn, 0, 0), out bx0, out bx1, out by0, out by1);
        int shift = 64 - by0;
        if (a.Length > 1 && a[1] == "--info")
        {
            Console.WriteLine("unitsPerEm " + unitsPerEm + ", glyphs " + numGlyphs + ", tables " + string.Join(" ", tab.Keys));
            Console.WriteLine("Н ink y " + by0 + ".." + by1 + " (cap " + (by1 - by0) + "), x " + bx0 + ".." + bx1 + ", shift to BASE 64: " + shift);
            var miss = new List<string>(); var have = new List<string>();
            for (int cp = 0x20; cp < 0x500; cp++) { if (cp >= 0x7F && cp < 0x400) continue; if (GlyphIndex(cp) != 0) have.Add(((char)cp).ToString()); }
            Console.WriteLine("mapped: " + string.Join("", have));
            return 0;
        }
        string cls = a[2], chars = a[3];
        var o = new StringBuilder();
        o.AppendLine("// " + cls + " outlines (generated by tools\\ttf2cs.cs from " + Path.GetFileName(a[0]) + ", unitsPerEm " + unitsPerEm + "): font units, y up,");
        o.AppendLine("// shifted " + shift + " so the ink bottom sits on Diablo.BASE like glyphs.json. Regenerate with tools\\build-cyr.ps1, don't edit.");
        o.AppendLine("static class " + cls);
        o.AppendLine("{");
        o.AppendLine("    public static readonly System.Collections.Generic.Dictionary<char, Diablo.G> Glyphs = new System.Collections.Generic.Dictionary<char, Diablo.G> {");
        int bad = 0;
        foreach (char ch in chars)
        {
            int gi = GlyphIndex(ch);
            if (gi == 0) { Console.Error.WriteLine("missing: " + ch + " U+" + ((int)ch).ToString("X4")); bad++; continue; }
            var cs = Contours(gi, 0, 0); int x0, x1, y0, y1; Bounds(cs, out x0, out x1, out y0, out y1); bool curves;
            string d = PathOf(cs, shift, out curves);
            if (cs.Count == 0 && ch != ' ') { Console.Error.WriteLine("empty: " + ch); bad++; continue; }
            if (cs.Count == 0) { x0 = x1 = 0; }
            Console.WriteLine(ch + " adv " + Advance(gi) + " x " + x0 + ".." + x1 + " y " + (y0 + shift) + ".." + (y1 + shift) + (curves ? " curves" : ""));
            o.AppendLine("        { '" + (ch == '\'' || ch == '\\' ? "\\" : "") + ch + "', new Diablo.G(\"" + d + "\", " + Advance(gi) + "f, " + x0 + "f, " + x1 + "f) },");
        }
        o.AppendLine("    };");
        o.AppendLine("}");
        File.WriteAllText(a[1], o.ToString(), new UTF8Encoding(true));
        return bad;
    }
}
