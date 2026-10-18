// The transparent strip along the top of the screen. The Windows counterpart
// of LinePanel.swift: it floats over every app, never takes focus, and lets
// clicks pass through everywhere except over the photos.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace LineDry
{
    internal sealed class LineForm : Form
    {
        public readonly Line Model = new Line();
        public Screen Target;

        // Hooks the launcher fills in.
        public Action<Guid> OpenRequested = delegate { };
        public Action<Guid> MarkupRequested = delegate { };
        public Action<Guid> PreviewRequested = delegate { };
        public Action<Guid> ShowInExplorer = delegate { };
        public Func<Guid, ContextMenuStrip> MenuFactory;

        /// Fired while a shell drag hovers the strip and the line is tucked
        /// away; the launcher brings it down, mirroring the top-edge gesture.
        public Action DropRequested;

        /// True while one of our menus is up: a click then only puts the menu
        /// away, the way a native menu would have swallowed it.
        public Func<bool> MenuIsOpen;
        public Action DismissMenu;

        private Bitmap _buffer;
        private Graphics _g;
        private double _revealY;
        private int _panelPixels;

        private readonly Dictionary<Guid, RectangleF> _hits = new Dictionary<Guid, RectangleF>();
        private Guid _pressId = Guid.Empty;
        private Point _pressAt;
        private bool _didLongPress;
        private bool _startedDrag;
        private readonly Timer _hold = new Timer();
        private bool _retractRuns;
        private double _retractFrom;
        private double _retractAt;


        private Guid _holdId = Guid.Empty;

        public LineForm()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            // Dropping an image file onto the strip hangs it on the line; the
            // file itself never moves, exactly like the original's rule.
            AllowDrop = true;
            _hold.Interval = 450;
            _hold.Tick += HoldElapsed;
        }

        // ---------------------------------------------------------- drag and drop in

        private static string[] DroppedImageFiles(DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            string[] raw = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (raw == null || raw.Length == 0) return null;
            List<string> images = new List<string>();
            foreach (string f in raw)
                if (File.Exists(f) && Thumbs.IsImage(f)) images.Add(f);
            return images.Count > 0 ? images.ToArray() : null;
        }

        /// The dropped list, read once when the drag arrives. Asking the source
        /// for its FileDrop and stat-ing every path on each DragOver is work that
        /// ran on every mouse move and stole time from the line's own descent.
        private string[] _dragFiles;
        private bool _dragKnown;
        private bool _dropAsked;

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            _dragFiles = DroppedImageFiles(e);
            _dragKnown = true;
            _dropAsked = false;
            e.Effect = _dragFiles != null ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _dragFiles = null;
            _dragKnown = false;
            _dropAsked = false;
        }

        /// While a shell drag is in flight the pointer never "rests" in the
        /// top band -- the system owns the mouse -- so the line is brought
        /// down here instead, the drag-enabled twin of the top-edge gesture.
        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (!_dragKnown)                       // a drag that began elsewhere
            {
                _dragFiles = DroppedImageFiles(e);
                _dragKnown = true;
            }
            e.Effect = _dragFiles != null ? DragDropEffects.Copy : DragDropEffects.None;
            // Asked once per drag: repeating it would restart the descent that is
            // already under way, which reads as the line refusing to settle.
            if (!Model.Revealed && !_dropAsked && DropRequested != null)
            {
                _dropAsked = true;
                DropRequested();
            }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] images = _dragFiles;
            if (images == null && !_dragKnown) images = DroppedImageFiles(e);
            _dragFiles = null;
            _dragKnown = false;
            _dropAsked = false;
            if (images == null) return;
            // Freshest first, so several files land in the order the shell
            // listed them and the newest ends up nearest the newest slot.
            foreach (string f in images) Model.Hang(f, false, false);
        }

        /// Model clock at the last paint, so the eased layout is frame-rate
        /// independent instead of a fixed fraction per frame.
        private double _placedAt;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: no taskbar, no Alt-Tab
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: never steals focus
                return cp;
            }
        }

        // ------------------------------------------------------------------ layout

        /// Moves onto the screen the pointer is on: that is where you just
        /// took the screenshot.
        public void PlaceOn(Screen screen)
        {
            Target = screen ?? Screen.PrimaryScreen;
            Rectangle b = Target.Bounds;
            _panelPixels = (int)Math.Ceiling(Geo.PanelHeight) + 6;
            Bounds = new Rectangle(b.X, b.Y, b.Width, _panelPixels);
            UpdateCapacity();

            if (_buffer != null) { _g.Dispose(); _buffer.Dispose(); }

            // Premultiplied: GDI+ draws into it natively, and GetHbitmap then
            // hands UpdateLayeredWindow the pixels as they already are instead
            // of converting 822k of them on every single frame.
            _buffer = new Bitmap(b.Width, _panelPixels, PixelFormat.Format32bppPArgb);
            _g = Graphics.FromImage(_buffer);
            _g.SmoothingMode = SmoothingMode.AntiAlias;
            _g.TextRenderingHint = TextRenderingHint.AntiAlias;
            _g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            _g.CompositingQuality = CompositingQuality.HighQuality;
            _g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public void UpdateCapacity()
        {
            int fits = Geo.VisibleCount(Bounds.Width);
            Model.VisibleCount = fits;
            Model.MaxItems = Math.Max(fits, Line.PinnedLength ? Line.PinnedCount : fits);
        }

        public bool OnTarget(Rectangle screenRect)
        {
            if (Target == null) return false;
            return Target.Bounds.Contains(screenRect.X + screenRect.Width / 2, screenRect.Y + 1);
        }

        // ------------------------------------------------------------------ frames

        /// How long the line takes to slide away once it decides to, in
        /// milliseconds; 0 sends it away at once, with no frames at all. The
        /// reveal keeps its own feel -- this is the setting the tray menu
        /// offers.
        public int RetractMs = 320;

        /// How far the strip must move for none of it to be left on screen.
        ///
        /// This is the distance from the top of the window to the lowest thing
        /// drawn in it -- the bottom edge of the lowest card, or the hint when
        /// the line is empty. The panel itself is taller than that (it is sized
        /// for the hint and for the tallest allowed photo), and retracting by
        /// the panel meant the last third of every retract slid along below the
        /// top edge, where nobody could see it.
        public double ContentBottom()
        {
            double w = Bounds.Width;
            double lowest = Geo.RopeY(w / 2, w) + 56 * Ui.DpiScale;   // the empty-line hint
            int count = Model.Items.Count;
            for (int i = 0; i < count; i++)
            {
                Pegged p = Model.Items[i];
                if (p.Flying || p.Thumb == null) continue;
                double x = Geo.X(i, count, w, Model.VisibleCount, Model.Scroll, Line.PinnedLength);
                double ropeY = Geo.RopeY(x, w);
                SizeF cs = Draw.CardSize(new SizeF(p.Thumb.Width, p.Thumb.Height));
                double bottom = ropeY - Geo.PinAbove - p.Drop
                                + Geo.CardOffsetBelowTop + cs.Height;
                if (bottom > lowest) lowest = bottom;
            }
            return lowest + 10 * Ui.DpiScale;      // room for the shadow
        }

        public void Step(double dt)
        {
            double target = Model.Revealed ? 0 : -ContentBottom();
            Model.Tick(dt);   // advances the clock the retract is timed against

            if (!Model.Revealed && RetractMs >= 0)
            {
                // A retract only starts when there is something to move, so
                // sitting at the bottom costs nothing.
                if (!_retractRuns && Math.Abs(_revealY - target) > 0.01)
                {
                    _retractRuns = true;
                    _retractFrom = _revealY;
                    _retractAt = Model.Now;

                }
                if (!_retractRuns)
                {
                    _revealY = target;
                }
                else
                {
                    double p = RetractMs <= 0 ? 1.0 : (Model.Now - _retractAt) * 1000.0 / RetractMs;
                    if (p >= 1.0)
                    {
                        _revealY = target;
                        _retractRuns = false;

                    }
                    else
                    {
                        // Even, not front-loaded. The old curve 1-(1-p)^3 put
                        // 87.5% of the travel into the first half of the time,
                        // so a one-second retract looked finished in a quarter
                        // of a second. Now the setting is the time you watch.
                        double e = p * p * (3.0 - 2.0 * p);
                        _revealY = _retractFrom + (target - _retractFrom) * e;
                    }
                }
            }
            else
            {
                _retractRuns = false;
                // 24, not 13: at 13 the descent took 320 ms to look settled and
                // 432 ms to land pixel-exact, and that crawling tail is what made
                // the line feel slow to answer a drag. Interrupt-safe either way.
                _revealY += (target - _revealY) * Math.Min(1.0, dt * 24.0);
                if (Math.Abs(target - _revealY) < 0.5) _revealY = target;
            }
        }

        public bool Animating
        {
            get
            {
                double target = Model.Revealed ? 0 : -ContentBottom();
                return Math.Abs(_revealY - target) > 0.5 || Model.NeedsFrames;
            }
        }

        public void Render()
        {
            if (_g == null) return;

            // Full-quality compositing and pixel offset are worth it on a still
            // picture and far too expensive at 30-60 frames a second.
            bool fast = Animating;
            Draw.Fast = fast;
            _g.CompositingQuality = fast ? CompositingQuality.HighSpeed : CompositingQuality.HighQuality;
            _g.PixelOffsetMode = fast ? PixelOffsetMode.HighSpeed : PixelOffsetMode.HighQuality;
            _g.Clear(Color.Transparent);
            _hits.Clear();

            double w = Bounds.Width;
            double off = _revealY;

            if (off > -(Geo.PanelHeight - 2))
            {
                GraphicsState s = _g.Save();
                _g.TranslateTransform(0, (float)off);



                int count = Model.Items.Count;
                if (count == 0)
                    Draw.Hint(_g, Loc.T("Take a screenshot and it will hang here"), w,
                              Geo.RopeY(w / 2, w) + 34);

                // How many cards are actually on the line: one on its way out no
                // longer holds a place, so the rest slide over as it leaves
                // instead of jumping sideways once it is gone.
                int live = 0;
                for (int k = 0; k < count; k++) if (!Model.Items[k].Falling) live++;
                double frameDt = Model.Now - _placedAt;
                if (frameDt <= 0 || frameDt > 0.12) frameDt = 0.016;
                _placedAt = Model.Now;

                int slot = 0;
                for (int i = 0; i < count; i++)
                {
                    Pegged p = Model.Items[i];
                    if (p.Flying) continue;

                    double want = Geo.X(slot, live, w, Model.VisibleCount, 0, Line.PinnedLength);
                    double wantScroll = Geo.X(slot, live, w, Model.VisibleCount, Model.Scroll, Line.PinnedLength) - want;
                    if (!p.Falling)
                    {
                        if (!p.SlotKnown) { p.SlotX = want; p.SlotKnown = true; }
                        else
                        {
                            p.SlotX += (want - p.SlotX) * (1 - Math.Exp(-frameDt * 14.0));
                            if (Math.Abs(want - p.SlotX) < 0.4) p.SlotX = want;
                        }
                        slot++;
                    }
                    double x = p.SlotX + wantScroll;
                    double ropeY = Geo.RopeY(x, w);
                    double clipTop = ropeY - Geo.PinAbove;

                    bool dragging = Model.DraggingId == p.Id;
                    double edge = Math.Min(1, Math.Max(0, Math.Min(x, w - x) / 90));
                    bool showCopied = p.CopiedAt > 0;

                    if (p.Falling)
                    {
                        DrawCardFalling(p, x, clipTop);
                        continue;
                    }

                    if (edge > 0.995)
                    {
                        Draw.Card(_g, p, x, clipTop, dragging, Model.IsInInbox(p.Id), showCopied, p.IsRecording);
                    }
                    else if (edge > 0.004)
                    {
                        DrawFaded(p, x, clipTop, dragging, showCopied, edge);
                    }

                    if (edge > 0.004)
                    {
                        SizeF cs = Draw.CardSize(new SizeF(p.Thumb.Width, p.Thumb.Height));
                        RectangleF hit = new RectangleF(
                            (float)(x - cs.Width / 2),
                            (float)(clipTop - p.Drop + Geo.CardOffsetBelowTop),
                            cs.Width, cs.Height);
                        _hits[p.Id] = hit;
                    }
                }
                _g.Restore(s);
            }

            PushToScreen();
        }

        /// A discarded card falls the way the original does: 520 px down,
        /// tilting further, fading, in 0.55 s with an ease-in curve.
        private void DrawCardFalling(Pegged p, double x, double clipTop)
        {
            const double Slip = 0.13;      // the pin lets go
            const double Rest = 0.50;      // and then it is on its way

            double t = Model.Now - p.FallingAt;
            if (t < 0) t = 0;

            double dy, turn, drift, alpha;
            if (t < Slip)
            {
                double s = t / Slip;
                s = s * s * (3 - 2 * s);                 // smoothstep: a gentle release
                dy = 8 * s;
                turn = 7 * s;
                drift = 0;
                alpha = 1.0;
            }
            else
            {
                double u = Math.Min(1.0, (t - Slip) / Rest);
                double fall = u * u;                     // gravity: distance goes as time squared
                dy = 8 + (_panelPixels + 220 * Ui.DpiScale) * fall;
                turn = 7 + 26 * fall + 5.5 * Math.Sin(u * 6.0) * u;      // turning, with a flutter
                double side = p.Tilt >= 0 ? 1 : -1;      // it slips off the way it leans
                drift = side * (9 * fall + 3.5 * Math.Sin(u * 5.0) * u);
                double f = (u - 0.55) / 0.45;            // opaque while it leaves, then fades
                if (f < 0) f = 0;
                alpha = 1.0 - f * f * (3 - 2 * f);
            }

            int a = (int)Math.Round(255 * alpha);
            if (a <= 2) return;

            SizeF photo = Draw.PhotoSize(new SizeF(p.Thumb.Width, p.Thumb.Height));
            float bw = photo.Width + 8, bh = photo.Height + 8;
            using (Bitmap scratch = new Bitmap((int)Math.Ceiling(bw) + 8, (int)Math.Ceiling(bh) + 8, PixelFormat.Format32bppArgb))
            {
                using (Graphics sg = Graphics.FromImage(scratch))
                {
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.TranslateTransform(4, 4);
                    Draw.Card(sg, p, 0, -Geo.CardOffsetBelowTop, false, Model.IsInInbox(p.Id), false, p.IsRecording);
                }

                // Placed by the transform alone. The absolute offset used to be
                // passed here as well as translated, so it counted twice -- the
                // card shot off sideways -- and since this call cannot rotate,
                // the card never turned at all on its way down.
                GraphicsState s = _g.Save();
                using (Matrix m = new Matrix())
                {
                    m.Translate((float)(x + drift - bw / 2 - 4),
                                (float)(clipTop - p.Drop + dy - 4), MatrixOrder.Append);
                    // Turn about the pin it has just slipped off, not about the
                    // middle of the card.
                    m.RotateAt((float)(p.Tilt + turn), new PointF(bw / 2 + 4, 8f), MatrixOrder.Append);
                    _g.MultiplyTransform(m);
                }
                DrawWithAlpha(scratch, a, 0, 0);
                _g.Restore(s);
            }
        }

        /// Cards scrolled towards an edge fade out before they reach it.
        private void DrawFaded(Pegged p, double x, double clipTop, bool dragging, bool showCopied, double edge)
        {
            SizeF cs = Draw.CardSize(new SizeF(p.Thumb.Width, p.Thumb.Height));
            int pad = 60;
            int w = (int)Math.Ceiling(cs.Width) + pad * 2;
            int h = (int)Math.Ceiling(cs.Height) + pad * 2;
            if (w <= 0 || h <= 0 || w > 4000 || h > 4000) return;

            using (Bitmap scratch = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics sg = Graphics.FromImage(scratch))
                {
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    sg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.TranslateTransform(pad, pad);
                    Draw.Card(sg, p, 0, 0, dragging, Model.IsInInbox(p.Id), showCopied, p.IsRecording);
                }
                DrawWithAlpha(scratch, (int)Math.Round(255 * edge),
                              (float)(x - cs.Width / 2 - pad), (float)(clipTop - p.Drop - pad));
            }
        }

        private void DrawWithAlpha(Bitmap src, int alpha, float dx, float dy)
        {
            if (alpha <= 2) return;
            ColorMatrix cm = new ColorMatrix();
            cm.Matrix33 = alpha / 255f;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                _g.DrawImage(src, new Rectangle((int)dx, (int)dy, src.Width, src.Height),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // -------------------------------------------------------------- hit testing

        /// Which photo, if any, is under this client point. A line longer than
        /// the screen also takes the whole band the photos hang in, so a swipe
        /// anywhere along it moves it.
        public Guid Hit(Point client, bool includeBand)
        {
            foreach (KeyValuePair<Guid, RectangleF> kv in _hits)
            {
                RectangleF r = kv.Value;
                r.Inflate(4, 4);
                if (r.Contains(client.X, client.Y)) return kv.Key;
            }
            if (includeBand && Model.Items.Count > Model.VisibleCount && Line.PinnedLength)
            {
                bool any = false;
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (RectangleF r in _hits.Values)
                {
                    any = true;
                    if (r.Top < minY) minY = r.Top;
                    if (r.Bottom > maxY) maxY = r.Bottom;
                }
                if (any && client.Y >= minY - 4 && client.Y <= maxY + 4) return Special.Band;
            }
            return Guid.Empty;
        }

        public static class Special
        {
            public static readonly Guid Band = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        }

        /// Keeps the pointer state fresh; the strip is click-through, so a
        /// transparent window never sees a mouse-move of its own.
        public void UpdatePointer(Point screenPoint)
        {
            Point client = PointToClient(screenPoint);
            Guid over = Hit(client, false);
            foreach (Pegged p in Model.Items)
            {
                bool hover = over == p.Id;
                if (hover != p.Hovering) p.Hovering = hover;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            const int HTTRANSPARENT = -1;

            if (m.Msg == WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                Point client = PointToClient(new Point(sx, sy));
                Guid hit = Hit(client, true);
                m.Result = (IntPtr)(hit == Guid.Empty ? HTTRANSPARENT : HTCLIENT);
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------- pointer work

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // One of our menus is showing: this click dismisses it and does
            // nothing else -- unless it is a right-click aimed at a photo,
            // which reopens the menu where the user pointed.
            if (MenuIsOpen != null && MenuIsOpen())
            {
                if (DismissMenu != null) DismissMenu();
                if (e.Button != MouseButtons.Right) return;
            }
            Guid id = Hit(e.Location, false);
            if (id == Guid.Empty) return;

            if (e.Button == MouseButtons.Right)
            {
                Guid hid = id;
                ContextMenuStrip menu = MenuFactory == null ? null : MenuFactory(hid);
                if (menu != null) menu.Show(this, e.Location);
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            // The corner button discards straight away, before any press logic.
            if (InCorner(id, e.Location))
            {
                Model.Discard(id, false);
                return;
            }

            if (e.Clicks == 2)
            {
                _pressId = Guid.Empty;
                OpenRequested(id);
                return;
            }

            _pressId = id;
            _pressAt = e.Location;
            _didLongPress = false;
            _startedDrag = false;
            Model.PressedId = id;
            Pegged p = Model.Find(id);
            if (p != null) p.IsPressed = true;
            _holdId = id;
            _hold.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressId == Guid.Empty || _startedDrag || _didLongPress) return;
            if (e.Button != MouseButtons.Left) return;
            double dx = e.X - _pressAt.X, dy = e.Y - _pressAt.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > 4) StartDrag(_pressId, e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _hold.Stop();
            Guid id = _pressId;
            _pressId = Guid.Empty;
            EndPress();
            if (id == Guid.Empty || _startedDrag || _didLongPress) return;
            if (e.Button == MouseButtons.Left) Model.Copy(id);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            foreach (Pegged p in Model.Items) p.Hovering = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Model.ScrollBy(-e.Delta / 120.0 * Geo.Spacing * 0.5);
        }

        private void HoldElapsed(object sender, EventArgs e)
        {
            _hold.Stop();
            if (_pressId == Guid.Empty || _startedDrag) return;
            _didLongPress = true;
            Guid id = _pressId;
            EndPress();
            MarkupRequested(id);
        }

        private void EndPress()
        {
            _hold.Stop();
            if (Model.PressedId != Guid.Empty)
            {
                Pegged p = Model.Find(Model.PressedId);
                if (p != null) p.IsPressed = false;
                Model.PressedId = Guid.Empty;
            }
            foreach (Pegged p in Model.Items) p.IsPressed = false;
        }

        private bool InCorner(Guid id, Point client)
        {
            RectangleF r;
            if (!_hits.TryGetValue(id, out r)) return false;
            float cs = (float)(26 * Ui.DpiScale);
            return new RectangleF(r.X, r.Y, cs, cs).Contains(client.X, client.Y);
        }

        private void StartDrag(Guid id, Point client)
        {
            Pegged p = Model.Find(id);
            if (p == null) return;
            _startedDrag = true;
            EndPress();
            Model.DraggingId = id;

            if (p.IsText)
            {
                // Words are dragged as words: any editor takes them, and dropping
                // them on a folder makes Explorer save them as a text file. The
                // sheet stays on the line -- there is no file to hand over.
                DataObject words = new DataObject();
                words.SetData(DataFormats.UnicodeText, p.Words);
                words.SetData(DataFormats.Text, p.Words);
                try { DoDragDrop(words, DragDropEffects.Copy); }
                catch { }
                finally
                {
                    Model.DraggingId = Guid.Empty;
                    _startedDrag = false;
                    _pressId = Guid.Empty;
                }
                return;
            }

            string[] files = new string[] { p.Path };
            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, files);
            if (!p.IsRecording)
            {
                using (Bitmap img = Thumbs.LoadPlain(p.Path))
                    if (img != null) data.SetImage(img);
            }

            try
            {
                DragDropEffects effect = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                // Dropped on a folder: it is saved there and leaves the line.
                if ((effect & DragDropEffects.Move) == DragDropEffects.Move)
                {
                    System.Threading.Thread.Sleep(400);
                    Model.Prune();
                }
            }
            catch { }
            finally
            {
                Model.DraggingId = Guid.Empty;
                _startedDrag = false;
                _pressId = Guid.Empty;
                Model.Prune();
            }
        }

        // --------------------------------------------------------------- to screen

        public void PushToScreen()
        {
            if (_buffer == null) return;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = _buffer.GetHbitmap(Color.FromArgb(0));
                oldBitmap = Native.SelectObject(memDc, hBitmap);

                Native.SIZE size = new Native.SIZE(_buffer.Width, _buffer.Height);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(Left, Top);
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                if (oldBitmap != IntPtr.Zero) Native.SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
                Native.DeleteDC(memDc);
            }
        }



        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hold.Dispose();
                if (_g != null) _g.Dispose();
                if (_buffer != null) _buffer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
