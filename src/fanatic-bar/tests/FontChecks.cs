// Font regression checks and multilingual off-screen previews; no runtime services are started.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;

class FontChecks
{
    [STAThread] static int Main(string[] args)
    {
        try { FanaticBar.CheckFont(Path.GetFullPath(args[0])); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
static partial class FanaticBar
{
    static void FontAssert(bool yes, string why) { if (!yes) throw new Exception(why); }
    static void SameOutline(GraphicsPath a, GraphicsPath b)
    {
        FontAssert(a.PointCount == b.PointCount, "Fallback point count");
        var ap = a.PathPoints; var bp = b.PathPoints;
        for (int i = 0; i < ap.Length; i++)
            FontAssert(Math.Abs(ap[i].X - bp[i].X) < 0.002f && Math.Abs(ap[i].Y - bp[i].Y) < 0.002f, "Fallback normalization");
    }
    public static void CheckFont(string root)
    {
        FontAssert(Mainframe.Glyphs.Count == 206 && TextFont.CAP == 720, "Mainframe cmap/cap metrics");
        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -.,:'°")
            FontAssert(Mainframe.Glyphs.ContainsKey(c), "Required glyph: " + c);
        float width;
        using (var p = DiabloText("H", out width))
            FontAssert(Math.Abs(p.GetBounds().Top) < 0.001f && Math.Abs(p.GetBounds().Height - TextFont.CAP) < 0.001f, "Cap normalization");
        using (var lower = DiabloText("a", out width)) using (var upper = DiabloText("A", out width))
            FontAssert(lower.GetBounds().Height < upper.GetBounds().Height, "Latin lowercase must stay lowercase");
        foreach (var item in DiabloCyr.Glyphs)
        {
            if (item.Key < '\u0400') continue;
            float scale = TextFont.CAP / Diablo.CAP;
            using (var expected = Svg(item.Value.D))
            using (var m = new Matrix(scale, 0, 0, -scale, 0, TextFont.CAP + Diablo.BASE * scale))
            using (var actual = DiabloText(item.Key.ToString(), out width))
            using (var lower = DiabloText(char.ToLowerInvariant(item.Key).ToString(), out width))
            {
                expected.Transform(m);
                using (var shift = new Matrix()) { shift.Translate(-expected.GetBounds().X, 0); expected.Transform(shift); }
                SameOutline(expected, actual); SameOutline(actual, lower);
            }
        }
        using (var alias = DiabloText("ʼ", out width)) using (var apostrophe = DiabloText("'", out width)) SameOutline(alias, apostrophe);
        float tabular = DiabloTabularWidth(2);
        for (int value = 0; value < 100; value++) using (var p = DiabloTabular(value.ToString("D2"), 2))
        {
            var bounds = p.GetBounds();
            FontAssert(bounds.Left >= -0.001f && bounds.Right <= tabular + 0.001f, "Digit cells overflow: " + value);
            using (var bmp = new Bitmap(200, 66)) using (var g = Graphics.FromImage(bmp))
                FontAssert(Math.Abs(BigBarDigitWidth(g, value) - tabular * 22 / TextFont.CAP) < 0.001f, "Changing digit advance");
        }
        // Check the ink's horizontal centre in the actual OSD renderer at fractional and integer scales.
        foreach (float unit in new[] { 1f, 1.25f, 1.5f, 3f, 4.5f }) foreach (string code in LangCodes)
        using (var bmp = new Bitmap(BadgeGraphics.Size(unit).Width, BadgeGraphics.Size(unit).Height))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black); BadgeGraphics.Draw(g, code, Color.Lime, unit);
            int left = bmp.Width, right = -1;
            for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++)
            {
                Color p = bmp.GetPixel(x, y);
                if (p.G > 100 && p.G > p.R * 2 && p.G > p.B * 2) { left = Math.Min(left, x); right = Math.Max(right, x); }
            }
            FontAssert(right >= left && Math.Abs((left + right + 1) / 2f - BadgeGraphics.Width(unit) / 2) <= 1, "Badge ink centering: " + code);
        }
        Console.WriteLine("PASS: 206 glyphs, lowercase, cap metrics, every Cyrillic fallback outline, 100 digit values, OSD ink centres at five scales");

        ui = new SynchronizationContext(); bigBatShown = 95; bigVol = 60; pingKnown = true; lastPing = 21;
        foreach (string lang in new[] { "en", "ru", "ua" })
        {
            Strings.Load(lang); LoadLocalizedArrays();
            // Cover every translated word that reaches the vector font; no unsupported letters may disappear.
            foreach (var words in new[] { UaMonthAbbr, UaMonthFull, UaMonthName, UaWeek, UaWeekFull }) foreach (string word in words)
                foreach (char c in word)
                {
                    char upper = char.ToUpperInvariant(c);
                    FontAssert(char.IsWhiteSpace(c) || c == 'ʼ' || Mainframe.Glyphs.ContainsKey(c) || Diablo.Glyphs.ContainsKey(upper) || DiabloCyr.Glyphs.ContainsKey(upper), "Missing localized glyph: " + lang + " " + c);
                }
            int row = 0;
            using (var sheet = new Bitmap(1800, 264)) using (var canvas = Graphics.FromImage(sheet))
            using (var bar = new BigBar(true))
            {
                canvas.Clear(Color.Black);
                foreach (int height in new[] { 48, 60, 66, 90 })
                {
                    bar.Size = new Size(1800, height);
                    using (var bmp = new Bitmap(1800, height)) bar.DrawToBitmap(bmp, bar.ClientRectangle);
                    int need = (int)typeof(BigBar).GetField("needWidth", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bar);
                    FontAssert(need <= MaxSpacers * 48, "Spacer limit exceeded: " + lang + " " + height + " " + need);
                    bar.Width = (need + 47) / 48 * 48;
                    using (var bmp = new Bitmap(bar.Width, height))
                    {
                        bar.DrawToBitmap(bmp, bar.ClientRectangle); canvas.DrawImageUnscaled(bmp, 0, row); row += height;
                    }
                    var zones = (List<KeyValuePair<Rectangle, Action<bool, Rectangle>>>)typeof(BigBar).GetField("zones", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bar);
                    FontAssert(zones.Count == 8 && zones[0].Key.Left == 0 && zones[7].Key.Right == bar.Width, "Multilingual strip edges");
                    for (int i = 1; i < zones.Count; i++) FontAssert(zones[i].Key.Left >= zones[i - 1].Key.Right, "Multilingual zones overlap");
                    Console.WriteLine("layout {0} height={1} needWidth={2} reserved={3}", lang, height, need, bar.Width);
                }
                sheet.Save(Path.Combine(root, "font-layout-" + lang + ".png"), ImageFormat.Png);
            }
        }
        Console.WriteLine("PASS: all localized vector strings, EN/RU/UA layout at 48/60/66/90px within 30 spacers, no overlapping zones");
    }
    static float BigBarDigitWidth(Graphics g, int value)
    {
        return (float)typeof(BigBar).GetMethod("Digits", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { g, value.ToString("D2"), 0f, 33f, 22f, Foreground });
    }
}
