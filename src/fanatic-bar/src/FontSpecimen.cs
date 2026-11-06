using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

static partial class FanaticBar
{
    // Off-screen diagnostic: exercises exactly the paths and cap metrics used by the bar/flyouts.
    static void FontSpecimen(string file)
    {
        const string sample = "SecretBaseStump 0123456789 NOV FRI 21 95 -5°";
        float width = DiabloLine(null, sample, 0, 0, 32, Foreground);
        using (var bmp = new Bitmap((int)Math.Ceiling(width) + 48, 330))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Background); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DiabloLine(g, sample, 24, 44, 32, Foreground);
            DiabloLine(g, "UA RU EN  NOV FRI  SecretBaseStump", 24, 100, 20.4f, Light);
            DiabloLine(g, "АБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ", 24, 156, 20.4f, Warning);
            DiabloLine(g, "Листопад П’ятниця Ноябрь Пятница", 24, 205, 20.4f, Foreground);
            float x = 24, scale = 25.5f / TextFont.CAP, cellWidth = DiabloTabularWidth(2) * scale;
            foreach (string value in new[] { "00", "11", "21", "55", "95", "99" })
            {
                using (var pen = new Pen(Muted)) g.DrawRectangle(pen, x, 251, cellWidth, 38);
                using (var p = DiabloTabular(value, 2))
                using (var m = new Matrix(scale, 0, 0, scale, x, 258))
                using (var brush = new SolidBrush(Success)) { p.Transform(m); g.FillPath(brush, p); }
                x += cellWidth + 20;
            }
            bmp.Save(file, ImageFormat.Png);
        }
    }
}
