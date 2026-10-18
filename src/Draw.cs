// Every pixel on the line is drawn here with GDI+. The original app draws all
// of its art in code too, so this is the same approach, not a shortcut.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LineDry
{
    internal static class Ui
    {
        public static double DpiScale = 1.0;
        public static string Face
        {
            get
            {
                if (_face == null)
                {
                    string[] want = { "Microsoft YaHei UI", "Segoe UI", "Microsoft YaHei", "Arial" };
                    foreach (string f in want)
                    {
                        try
                        {
                            using (FontFamily fam = new FontFamily(f)) { _face = f; break; }
                        }
                        catch { }
                    }
                    if (_face == null) _face = "Arial";
                }
                return _face;
            }
        }
        private static string _face;

        // Cached per (size, style) pair AND invalidated when DpiScale changes:
        // a stale-DPI font is exactly as wrong as a null one.
        private static Font _f11Bold, _f12Regular;
        private static double _fDpi = -1;

        /// Cached and shared: the returned instance is owned by Ui and must NOT
        /// be disposed by the caller. Wrapping it in a using block disposes the
        /// shared font, after which every MeasureString on it throws 'Parameter is
        /// not valid' -- per frame, silently, for as long as anything is drawing.
        public static Font Font(double pts, FontStyle style)
        {
            if (_fDpi != DpiScale)
            {
                if (_f11Bold != null) _f11Bold.Dispose();
                if (_f12Regular != null) _f12Regular.Dispose();
                _f11Bold = null;
                _f12Regular = null;
                _fDpi = DpiScale;
            }
            float px = (float)(pts * DpiScale * 96.0 / 72.0);
            if (pts == 11 && style == FontStyle.Bold)
            {
                if (_f11Bold == null) _f11Bold = SafeFont(px, style);
                return _f11Bold;
            }
            if (pts == 12 && style == FontStyle.Regular)
            {
                if (_f12Regular == null) _f12Regular = SafeFont(px, style);
                return _f12Regular;
            }
            return SafeFont(px, style);
        }

        /// A Font constructor with a dead family throws; MeasureString then
        /// kills the whole render pass. Fall back to a family that always
        /// exists instead.
        private static Font SafeFont(float px, FontStyle style)
        {
            try { return new Font(Face, px, style, GraphicsUnit.Pixel); }
            catch
            {
                try { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
                catch { return SystemFonts.DefaultFont; }
            }
        }
    }

    internal static class Draw
    {
        // ------------------------------------------------------------- primitives

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.05f)
            {
                GraphicsPath sq = new GraphicsPath();
                sq.AddRectangle(r);
                return sq;
            }
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// The system's blurred material, standing in as crisp glass: a
        /// translucent plate, a specular edge lit from above, and a hairline.
        public static void Glass(Graphics g, RectangleF r, float radius)
        {
            using (GraphicsPath plate = Rounded(r, radius))
            {
                using (SolidBrush body = new SolidBrush(Color.FromArgb(206, 247, 248, 250)))
                    g.FillPath(body, plate);

                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new PointF(0, r.Top), new PointF(0, r.Bottom),
                    Color.FromArgb(140, 255, 255, 255), Color.FromArgb(31, 255, 255, 255)))
                using (Pen edge = new Pen(lg, 0.75f))
                    g.DrawPath(edge, plate);

                RectangleF outer = r;
                outer.Inflate(-0.25f, -0.25f);
                using (Pen hair = new Pen(Color.FromArgb(26, 0, 0, 0), 0.5f))
                using (GraphicsPath op = Rounded(outer, Math.Max(0, radius - 0.25f)))
                    g.DrawPath(hair, op);
            }
        }

        /// A soft drop shadow, built from concentric strokes -- no blur filter
        /// needed, and it stays cheap enough to run at 60 fps.
        /// Set by the renderer while an animation is running: the same picture
        /// at a cheaper cost, because nobody can see the difference in motion.
        public static bool Fast;
        public static void Shadow(Graphics g, RectangleF r, float radius, int alpha, float reach, float dy)
        {
            int steps = Fast ? 3 : 7;
            for (int i = steps; i >= 1; i--)
            {
                float t = (float)i / steps;
                float spread = reach * t;
                int a = (int)(alpha * (1f - t) * 0.55f) + 1;
                RectangleF rr = r;
                rr.Inflate(spread, spread);
                rr.Offset(0, dy);
                using (Pen pen = new Pen(Color.FromArgb(a, 0, 0, 0), spread * 1.6f))
                using (GraphicsPath p = Rounded(rr, radius + spread))
                    g.DrawPath(pen, p);
            }
        }

        // ------------------------------------------------------------------ the rope

        /// A thin, neutral line that fades out at both ends, so it seems to
        /// come from beyond the screen.
        public static void RopeCore(Graphics g, double width, double panelHeight)
        {
            double U = Ui.DpiScale;
            double top = Geo.RopeTop;
            double sag = Geo.Sag(width);
            double cx = width / 2, cy = top + 2 * sag;

            // Sampled into short segments so the ends can fade out.
            const int N = 160;
            PointF[] pts = new PointF[N + 1];
            for (int i = 0; i <= N; i++)
            {
                double t = (double)i / N;
                double mt = 1 - t;
                double x = mt * mt * (-20) + 2 * mt * t * cx + t * t * (width + 20);
                double y = mt * mt * top + 2 * mt * t * cy + t * t * top;
                pts[i] = new PointF((float)x, (float)y);
            }

            for (int layer = 0; layer < 3; layer++)
            {
                Color col;
                float w, dy;
                if (layer == 0) { col = Color.FromArgb(56, 0, 0, 0); w = (float)(1.4 * U); dy = (float)(1.2 * U); }
                else if (layer == 1) { col = Color.FromArgb(255, 140, 140, 140); w = (float)(1.2 * U); dy = 0f; }
                else { col = Color.FromArgb(115, 255, 255, 255); w = (float)(0.4 * U); dy = (float)(-0.35 * U); }

                for (int i = 0; i < N; i++)
                {
                    double t = (double)i / N;
                    double fade = Math.Min(1.0, Math.Min(t / 0.08, (1 - t) / 0.08));
                    if (fade <= 0) continue;
                    int a = (int)Math.Round(col.A * fade);
                    if (a <= 0) continue;
                    using (Pen pen = new Pen(Color.FromArgb(a, col.R, col.G, col.B), w))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawLine(pen, pts[i].X, pts[i].Y + dy, pts[i + 1].X, pts[i + 1].Y + dy);
                    }
                }
            }
        }

        // ------------------------------------------------------------- the clothespin

        /// A minimal aluminium clip: a brushed metal pill with a slot where it
        /// grips the line.
        public static void Clothespin(Graphics g, float cx, float top)
        {
            float cwid = (float)Geo.ClipWidth, chei = (float)Geo.ClipHeight;
            RectangleF pin = new RectangleF(cx - cwid / 2f, top, cwid, chei);

            using (GraphicsPath path = Rounded(pin, cwid * 0.39f))
            {
                using (LinearGradientBrush metal = new LinearGradientBrush(
                    new PointF(pin.Left, 0), new PointF(pin.Right, 0),
                    Color.White, Color.White))
                {
                    ColorBlend cb = new ColorBlend();
                    cb.Colors = new Color[]
                    {
                        Color.FromArgb(255, 179, 179, 179), Color.FromArgb(255, 237, 237, 237),
                        Color.FromArgb(255, 209, 209, 209), Color.FromArgb(255, 158, 158, 158)
                    };
                    cb.Positions = new float[] { 0f, 0.35f, 0.65f, 1f };
                    metal.InterpolationColors = cb;
                    g.FillPath(metal, path);
                }

                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new PointF(0, pin.Top), new PointF(0, pin.Bottom),
                    Color.FromArgb(230, 255, 255, 255), Color.FromArgb(46, 0, 0, 0)))
                using (Pen edge = new Pen(lg, 0.6f))
                    g.DrawPath(edge, path);

                // The slot the line passes through.
                using (GraphicsPath slot = Rounded(
                    new RectangleF(cx - cwid * 0.28f, top + chei * 0.33f, cwid * 0.56f, chei * 0.054f), chei * 0.027f))
                using (SolidBrush dark = new SolidBrush(Color.FromArgb(82, 0, 0, 0)))
                    g.FillPath(dark, slot);
            }
        }

        // ------------------------------------------------------------------ one card

        public static SizeF PhotoSize(SizeF source)
        {
            double maxW = Geo.CardWidth - Geo.FrameInset * 2;
            double maxH = Geo.PhotoMaxHeight;
            if (source.Width <= 0 || source.Height <= 0)
                return new SizeF((float)maxW, (float)maxH);
            double k = Math.Min(maxW / source.Width, maxH / source.Height);
            return new SizeF((float)(source.Width * k), (float)(source.Height * k));
        }

        public static SizeF CardSize(SizeF source)
        {
            SizeF p = PhotoSize(source);
            return new SizeF(p.Width + (float)(Geo.FrameInset * 2), p.Height + (float)(Geo.FrameInset * 2));
        }

        /// Draws one photo with its clip, swinging about its top edge.
        /// `cx`/`clipTop` are screen-space; everything else happens in the
        /// rotated frame, exactly as the SwiftUI view nests it.
        /// The unchanging part of a card, drawn once into its own bitmap.
        private static Bitmap BuildFace(Pegged p, float cardW, float cardH, float radius)
        {
            // Only the widest shadow (10 + 5) and the clip above the card
            // (CardOffsetBelowTop) have to fit; anything more is dead pixels in
            // every card bitmap.
            float pad = (float)(16 * Ui.DpiScale);
            p.FacePad = pad;
            Bitmap face = new Bitmap((int)Math.Ceiling(cardW + pad * 2),
                                     (int)Math.Ceiling(cardH + pad * 2),
                                     PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(face))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.TranslateTransform(pad + cardW / 2f, pad);

                RectangleF card = new RectangleF(-cardW / 2f, 0, cardW, cardH);

                // Hairline and shadow first, so they sit under the glass.
                Shadow(g, card, radius, 46, 10, 5);
                Glass(g, card, radius);

                // The photo inside, with concentric corners.
                RectangleF inner = card;
                inner.Inflate(-(float)Geo.FrameInset, -(float)Geo.FrameInset);
                using (GraphicsPath clipPath = Rounded(inner, radius - (float)Geo.FrameInset))
                {
                    GraphicsState ps = g.Save();
                    g.SetClip(clipPath, CombineMode.Replace);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(p.Thumb, inner);
                    g.Restore(ps);

                    using (Pen edge = new Pen(Color.FromArgb(46, 255, 255, 255), 0.5f))
                        g.DrawPath(edge, clipPath);
                }

                // The clip grips the line above the card, over it. It never
                // changes either, so it belongs in here and not in the frame.
                g.ResetTransform();
                g.TranslateTransform(pad + cardW / 2f, pad - (float)Geo.CardOffsetBelowTop);
                Clothespin(g, 0, 0);
            }
            return face;
        }

        public static void Card(Graphics g, Pegged p, double cx, double clipTop,
                                bool dragging, bool isInbox, bool showCopied, bool isRecording)
        {
            if (p.Thumb == null) return;

            SizeF photo = PhotoSize(new SizeF(p.Thumb.Width, p.Thumb.Height));
            float cardW = photo.Width + (float)(Geo.FrameInset * 2);
            float cardH = photo.Height + (float)(Geo.FrameInset * 2);
            float radius = (float)Geo.FrameRadius;

            // Shadow, glass, framed photo and hairline are the same picture on
            // every frame; they are drawn once and blitted from then on. Doing
            // it live each frame cost a whole core and kept the finaliser busy
            // throwing away the pens, paths, brushes and gradients.
            if (p.Face == null || !ReferenceEquals(p.FaceFor, p.Thumb))
            {
                p.Face = BuildFace(p, cardW, cardH, radius);
                p.FaceFor = p.Thumb;
            }
            float pad = (float)p.FacePad;

            GraphicsState st = g.Save();
            g.TranslateTransform((float)cx, (float)clipTop);

            // press holds the photo in; hover lifts it.
            double scale = p.IsPressed ? 0.95 : (p.Hover > 0.01 ? 1.0 + 0.035 * p.Hover : 1.0);
            if (Math.Abs(scale - 1.0) > 0.0005) g.ScaleTransform((float)scale, (float)scale);

            g.RotateTransform((float)(p.Tilt + p.Swing));
            g.TranslateTransform(0, (float)(-p.Drop));

            RectangleF card = new RectangleF(-cardW / 2f, (float)Geo.CardOffsetBelowTop, cardW, cardH);

            g.InterpolationMode = Fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
            g.DrawImage(p.Face, card.X - pad, card.Y - pad);

            // A recording says so with a play button over its first frame.
            if (isRecording)
            {
                float bs = (float)(28 * Ui.DpiScale);
                RectangleF badge = new RectangleF(card.X + cardW / 2f - bs / 2f,
                                                  card.Y + cardH / 2f - bs / 2f, bs, bs);
                using (GraphicsPath circle = new GraphicsPath())
                {
                    circle.AddEllipse(badge);
                    using (SolidBrush body = new SolidBrush(Color.FromArgb(232, 246, 246, 246)))
                        g.FillPath(body, circle);
                    using (Pen edge = new Pen(Color.FromArgb(140, 255, 255, 255), 0.75f))
                        g.DrawPath(edge, circle);
                }
                PointF[] tri = new PointF[]
                {
                    new PointF(badge.X + bs * 0.38f, badge.Y + bs * 0.28f),
                    new PointF(badge.X + bs * 0.72f, badge.Y + bs * 0.5f),
                    new PointF(badge.X + bs * 0.38f, badge.Y + bs * 0.72f)
                };
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(235, 20, 20, 20)))
                    g.FillPolygon(fg, tri);
            }

            // The corner button: a cross inside the app's own folder, an eye
            // with a slash for a file that lives anywhere else.
            if (p.Hover > 0.02 && !dragging)
            {
                float cs = (float)(20 * Ui.DpiScale);
                float cp = (float)(3 * Ui.DpiScale);
                RectangleF corner = new RectangleF(card.X + cp, card.Y + cp, cs, cs);
                double appear = p.Hover;
                int bodyA = (int)Math.Round(232 * appear);
                GraphicsState bs2 = g.Save();
                float k = (float)(0.6 + 0.4 * appear);
                g.TranslateTransform(corner.X + cs / 2f, corner.Y + cs / 2f);
                g.ScaleTransform(k, k);
                g.TranslateTransform(-cs / 2f, -cs / 2f);
                using (GraphicsPath circle = new GraphicsPath())
                {
                    circle.AddEllipse(new RectangleF(0, 0, cs, cs));
                    using (SolidBrush body = new SolidBrush(Color.FromArgb(bodyA, 246, 246, 246)))
                        g.FillPath(body, circle);
                    using (Pen edge = new Pen(Color.FromArgb((int)(140 * appear), 255, 255, 255), 0.75f))
                        g.DrawPath(edge, circle);
                }
                using (Pen ink = new Pen(Color.FromArgb((int)(235 * appear), 20, 20, 20), 1.6f))
                {
                    ink.StartCap = LineCap.Round;
                    ink.EndCap = LineCap.Round;
                    float m = cs * 0.32f;
                    if (isInbox)
                    {
                        g.DrawLine(ink, m, m, cs - m, cs - m);
                        g.DrawLine(ink, cs - m, m, m, cs - m);
                    }
                    else
                    {
                        // An eye with a slash.
                        using (GraphicsPath eye = new GraphicsPath())
                        {
                            eye.AddBezier(m, cs / 2f, cs * 0.32f, cs * 0.24f, cs * 0.68f, cs * 0.24f, cs - m, cs / 2f);
                            eye.AddBezier(cs - m, cs / 2f, cs * 0.68f, cs * 0.76f, cs * 0.32f, cs * 0.76f, m, cs / 2f);
                            g.DrawPath(ink, eye);
                        }
                        g.DrawLine(ink, m - 1, cs - m + 1, cs - m + 1, m - 1);
                    }
                }
                g.Restore(bs2);
            }

            g.Restore(st);

            // "Copied", hanging under the card in screen space.
            if (showCopied)
            {
                double angle = (p.Tilt + p.Swing) * Math.PI / 180.0;
                double halfH = cardH / 2 + Geo.CardOffsetBelowTop;
                double pivotY = clipTop - p.Drop;
                double midY = pivotY + halfH * Math.Cos(angle);
                DrawPill(g, Loc.T("Copied"), (float)cx, (float)(midY + cardH / 2 + 24), true, -1);
            }
        }

        // ---------------------------------------------------------------- furniture

        /// A capsule label: the empty-line hint, and the "Copied" confirmation.
        public static void DrawPill(Graphics g, string text, float cx, float cy, bool withCheck, int forceAlpha)
        {
            {
                Font f = Ui.Font(11, FontStyle.Bold);   // cached: shared, never disposed
                SizeF sz = g.MeasureString(text, f);
                if (withCheck) sz.Width += (float)(18 * Ui.DpiScale);
                float w = sz.Width + (float)(20 * Ui.DpiScale), h = sz.Height + (float)(8 * Ui.DpiScale);
                RectangleF r = new RectangleF(cx - w / 2f, cy - h / 2f, w, h);

                if (forceAlpha < 0)
                {
                    using (GraphicsPath capsule = Rounded(r, h / 2f))
                    using (SolidBrush body = new SolidBrush(Color.FromArgb(238, 246, 246, 246)))
                        g.FillPath(body, capsule);
                    using (GraphicsPath capsule = Rounded(r, h / 2f))
                    using (Pen edge = new Pen(Color.FromArgb(140, 255, 255, 255), 0.75f))
                        g.DrawPath(edge, capsule);
                }

                int a = forceAlpha < 0 ? 235 : forceAlpha;
                float tx = r.X + (float)(10 * Ui.DpiScale) + (withCheck ? (float)(18 * Ui.DpiScale) : 0);
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(a, 20, 20, 20)))
                {
                    if (withCheck)
                    {
                        using (Pen tick = new Pen(Color.FromArgb(a, 20, 20, 20), 1.8f))
                        {
                            tick.StartCap = LineCap.Round;
                            tick.EndCap = LineCap.Round;
                            float x0 = r.X + (float)(10 * Ui.DpiScale), y0 = cy;
                            g.DrawLine(tick, x0, y0, x0 + 3.5f, y0 + 4);
                            g.DrawLine(tick, x0 + 3.5f, y0 + 4, x0 + 9.5f, y0 - 4.5f);
                        }
                    }
                    g.DrawString(text, f, ink, tx, r.Y + (float)(4 * Ui.DpiScale));
                }
            }
        }

        /// The hint shown while nothing hangs on the line.
        public static void Hint(Graphics g, string text, double width, double y)
        {
            {
                Font f = Ui.Font(12, FontStyle.Regular);   // cached: shared, never disposed
                SizeF sz = g.MeasureString(text, f);
                float w = sz.Width + (float)(24 * Ui.DpiScale), h = sz.Height + (float)(12 * Ui.DpiScale);
                RectangleF r = new RectangleF((float)(width / 2 - w / 2), (float)(y - h / 2), w, h);
                using (GraphicsPath capsule = Rounded(r, h / 2f))
                {
                    using (SolidBrush body = new SolidBrush(Color.FromArgb(238, 246, 246, 246)))
                        g.FillPath(body, capsule);
                    using (Pen edge = new Pen(Color.FromArgb(31, 0, 0, 0), 0.75f))
                        g.DrawPath(edge, capsule);
                }
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(150, 40, 40, 40)))
                    g.DrawString(text, f, ink, r.X + (float)(12 * Ui.DpiScale), r.Y + (float)(6 * Ui.DpiScale));
            }
        }

        // -------------------------------------------------------------- tray picture

        /// The app's mark: the shirt from the original's status item, drawn in
        /// code here too. Filled while copied images are being hung.
        public static Icon Tshirt(int size, bool filled)
        {
            using (Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    float u = size / 32f;
                    GraphicsPath shirt = new GraphicsPath();
                    shirt.AddLines(new PointF[]
                    {
                        new PointF(11*u, 5*u), new PointF(5*u, 8*u), new PointF(2*u, 13*u),
                        new PointF(7*u, 16*u), new PointF(8*u, 14*u), new PointF(8*u, 27*u),
                        new PointF(24*u, 27*u), new PointF(24*u, 14*u), new PointF(25*u, 16*u),
                        new PointF(30*u, 13*u), new PointF(27*u, 8*u), new PointF(21*u, 5*u),
                    });
                    shirt.CloseFigure();

                    using (Pen outline = new Pen(Color.FromArgb(255, 245, 245, 245), 2.2f*u))
                    {
                        outline.LineJoin = LineJoin.Round;
                        g.DrawPath(outline, shirt);
                    }
                    // The collar.
                    using (GraphicsPath collar = new GraphicsPath())
                    {
                        collar.AddBezier(11*u, 5*u, 13.5f*u, 9*u, 18.5f*u, 9*u, 21*u, 5*u);
                        using (Pen p = new Pen(Color.FromArgb(255, 245, 245, 245), 2.2f*u))
                        {
                            p.StartCap = LineCap.Round;
                            p.EndCap = LineCap.Round;
                            g.DrawPath(p, collar);
                        }
                    }
                    if (filled)
                    {
                        using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 245, 245, 245)))
                        {
                            GraphicsPath body = new GraphicsPath();
                            body.AddLines(new PointF[]
                            {
                                new PointF(11*u, 5*u), new PointF(8*u, 14*u), new PointF(8*u, 27*u),
                                new PointF(24*u, 27*u), new PointF(24*u, 14*u), new PointF(21*u, 5*u),
                            });
                            body.CloseFigure();
                            g.FillPath(fill, body);
                        }
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }

        public static Icon AppIcon(int size)
        {
            using (Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    RectangleF r = new RectangleF(1, 1, size - 2, size - 2);
                    using (GraphicsPath plate = Rounded(r, size * 0.22f))
                    using (LinearGradientBrush body = new LinearGradientBrush(
                        new PointF(0, 0), new PointF(size, size),
                        Color.FromArgb(255, 250, 250, 250), Color.FromArgb(255, 214, 222, 232)))
                        g.FillPath(body, plate);

                    using (Pen rope = new Pen(Color.FromArgb(255, 120, 120, 120), size * 0.045f))
                    {
                        GraphicsPath line = new GraphicsPath();
                        line.AddBezier(size * 0.12f, size * 0.30f,
                                       size * 0.35f, size * 0.42f,
                                       size * 0.65f, size * 0.42f,
                                       size * 0.88f, size * 0.30f);
                        g.DrawPath(rope, line);
                    }
                    using (Pen pin = new Pen(Color.FromArgb(255, 140, 140, 140), size * 0.075f))
                    {
                        pin.StartCap = LineCap.Round;
                        pin.EndCap = LineCap.Round;
                        g.DrawLine(pin, size * 0.5f, size * 0.36f, size * 0.5f, size * 0.52f);
                    }
                    using (GraphicsPath card = Rounded(
                        new RectangleF(size * 0.28f, size * 0.50f, size * 0.44f, size * 0.34f), size * 0.06f))
                    {
                        using (LinearGradientBrush lg = new LinearGradientBrush(
                            new RectangleF(size * 0.28f, size * 0.50f, size * 0.44f, size * 0.34f),
                            Color.FromArgb(255, 110, 150, 215), Color.FromArgb(255, 205, 115, 155), 45f))
                            g.FillPath(lg, card);
                        using (Pen edge = new Pen(Color.FromArgb(80, 0, 0, 0), 1f))
                            g.DrawPath(edge, card);
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }
    }
}
