// A text card is not a new kind of thing on the line: it is a card whose
// picture happens to be words. The sheet is painted here (in the manner of
// Apple's Notes -- warm paper, a hairline edge, the date in grey) and handed to
// the same Pegged a photo uses, so tilt, swing, the clip, the frame, the shadow,
// hover, take-down and the layout all keep working untouched.
//
// The fonts come from Ui.Font, which caches them: they are shared for the life
// of the process and must never be disposed here. Wrapping one in `using`
// disposed the shared instance, and every later MeasureString on it threw
// "Parameter is not valid" -- one frame at a time, silently, for as long as
// anything was drawing.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LineDry
{
    internal static class Notes
    {
        /// The sheet a text card shows, rendered at twice the size it will be
        /// drawn at so the letters stay crisp once the card scales it.
        public static Bitmap Sheet(string words, DateTime stamp)
        {
            words = (words ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

            float inner = (float)(Geo.CardWidth - Geo.FrameInset * 2);
            float maxH = (float)Geo.PhotoMaxHeight;
            float pad = (float)(12 * Ui.DpiScale);
            float wrap = inner - pad * 2;

            Font dateFont = Ui.Font(9, FontStyle.Regular);      // cached: never disposed
            Font titleFont = Ui.Font(12, FontStyle.Bold);
            Font bodyFont = Ui.Font(11, FontStyle.Regular);

            string title = "";
            string rest = "";
            string[] lines = words.Split('\n');
            if (lines.Length > 0) title = lines[0];
            if (lines.Length > 1) rest = string.Join("\n", lines, 1, lines.Length - 1);

            float dateH, titleH, bodyH;
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(probe))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                StringFormat fmt = StringFormat.GenericTypographic;
                fmt.FormatFlags |= StringFormatFlags.LineLimit;
                dateH = g.MeasureString("Ag", dateFont).Height;
                titleH = title.Length == 0 ? 0
                    : g.MeasureString(title, titleFont, new SizeF(wrap, 4000f), fmt).Height;
                bodyH = rest.Length == 0 ? 0
                    : g.MeasureString(rest, bodyFont, new SizeF(wrap, 4000f), fmt).Height;
            }

            float need = pad + dateH + 3 * (float)Ui.DpiScale + titleH + bodyH + pad;
            float h = Math.Min(maxH, Math.Max(inner * 0.62f, need));

            int rw = (int)Math.Round(inner * 2);
            int rh = (int)Math.Round(h * 2);
            if (rw < 8 || rh < 8) return null;

            Bitmap sheet = new Bitmap(rw, rh, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.ScaleTransform(2f, 2f);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (LinearGradientBrush paper = new LinearGradientBrush(
                           new RectangleF(0, 0, inner, h),
                           Color.FromArgb(255, 255, 252), Color.FromArgb(253, 249, 235),
                           LinearGradientMode.Vertical))
                    g.FillRectangle(paper, 0, 0, inner, h);
                using (Pen edge = new Pen(Color.FromArgb(228, 222, 205), 1f))
                    g.DrawRectangle(edge, 0.5f, 0.5f, inner - 1f, h - 1f);

                float y = pad;
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(158, 150, 132)))
                    g.DrawString(stamp.ToString("g"), dateFont, ink, pad, y);
                y += dateH + 2 * (float)Ui.DpiScale;
                using (Pen rule = new Pen(Color.FromArgb(238, 233, 217), 1f))
                    g.DrawLine(rule, pad, y, inner - pad, y);
                y += 4 * (float)Ui.DpiScale;

                GraphicsState clip = g.Save();
                g.SetClip(new RectangleF(pad, y, wrap, h - pad - y));
                StringFormat fmt = StringFormat.GenericTypographic;
                fmt.FormatFlags |= StringFormatFlags.LineLimit;
                fmt.Trimming = StringTrimming.EllipsisWord;
                using (SolidBrush inkTitle = new SolidBrush(Color.FromArgb(28, 28, 30)))
                using (SolidBrush inkBody = new SolidBrush(Color.FromArgb(62, 62, 66)))
                {
                    if (title.Length > 0)
                    {
                        g.DrawString(title, titleFont, inkTitle, new RectangleF(pad, y, wrap, h), fmt);
                        y += titleH;
                    }
                    if (rest.Length > 0 && y < h - pad)
                    {
                        g.DrawString(rest, bodyFont, inkBody,
                            new RectangleF(pad, y, wrap, h - pad - y), fmt);
                    }
                }
                g.Restore(clip);
            }
            return sheet;
        }
    }
}
