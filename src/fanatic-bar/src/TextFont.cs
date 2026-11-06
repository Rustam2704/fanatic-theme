// Shared by fanatic-bar and keyboard-switch. Outlines are compiled in; no installed font is used.
using System;
using System.Drawing.Drawing2D;

static class TextFont
{
    public const float CAP = Mainframe.CAP;
    // Keep the old digit gap in proportion to the visible cap, rather than the new font's em.
    const float DigitGap = Diablo.DIGIT_GAP * Diablo.EM / 36f * CAP / Diablo.CAP;
    static readonly float DigitCell = WidestDigit();

    struct Glyph
    {
        public Diablo.G Data;
        public float Scale, Baseline;
        public float XMin { get { return Data.XMin * Scale; } }
        public float XMax { get { return Data.XMax * Scale; } }
        public float Advance { get { return Data.Adv * Scale; } }
    }
    static Glyph Resolve(char c)
    {
        Diablo.G data;
        if (c == 'ʼ') c = '\'';
        if (Mainframe.Glyphs.TryGetValue(c, out data))
            return new Glyph { Data = data, Scale = 1, Baseline = Mainframe.BASE };
        if (Diablo.Glyphs.TryGetValue(c, out data) || DiabloCyr.Glyphs.TryGetValue(c, out data))
            return new Glyph { Data = data, Scale = CAP / Diablo.CAP, Baseline = Diablo.BASE };
        char upper = char.ToUpperInvariant(c);
        return upper != c ? Resolve(upper) : Resolve(' ');
    }
    static void Add(GraphicsPath all, Glyph glyph, float x, Func<string, GraphicsPath> svg)
    {
        if (glyph.Data.D.Length == 0) return;
        using (var p = svg(glyph.Data.D))
        using (var m = new Matrix(glyph.Scale, 0, 0, -glyph.Scale, x, CAP + glyph.Baseline * glyph.Scale))
        {
            p.Transform(m); all.AddPath(p, false);
        }
    }
    // Use actual path bounds, including overhangs, so badges are centred by ink in both programs.
    public static GraphicsPath Text(string text, Func<string, GraphicsPath> svg, out float width)
    {
        var all = new GraphicsPath(FillMode.Winding); float pen = 0;
        foreach (char c in text)
        {
            Glyph glyph = Resolve(c); Add(all, glyph, pen, svg); pen += glyph.Advance;
        }
        width = pen;
        if (all.PointCount > 0)
        {
            var bounds = all.GetBounds(); width = bounds.Width;
            using (var m = new Matrix()) { m.Translate(-bounds.X, 0); all.Transform(m); }
        }
        return all;
    }
    static float WidestDigit()
    {
        float cell = 0;
        foreach (char c in "0123456789") { Glyph glyph = Resolve(c); cell = Math.Max(cell, glyph.XMax - glyph.XMin); }
        return cell;
    }
    public static float TabularWidth(int cells) { return cells * DigitCell + Math.Max(0, cells - 1) * DigitGap; }
    public static GraphicsPath Tabular(string text, int cells, Func<string, GraphicsPath> svg)
    {
        var all = new GraphicsPath(FillMode.Winding); int first = cells - text.Length;
        for (int i = 0; i < text.Length; i++)
        {
            Glyph glyph = Resolve(text[i]);
            float left = (first + i) * (DigitCell + DigitGap) + (DigitCell - (glyph.XMax - glyph.XMin)) / 2;
            Add(all, glyph, left - glyph.XMin, svg);
        }
        return all;
    }
}
