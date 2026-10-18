// Two small windows the tray menu opens: the shortcut recorder, and a
// Windows stand-in for Quick Look.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace LineDry
{
    /// Asks for a new shortcut in a small window: the next key press with
    /// Ctrl, Alt or Win becomes it. Escape cancels and Delete clears it.
    internal sealed class RecorderWindow : Form
    {
        private Shortcut _result;
        private bool _cancelled = true;

        private RecorderWindow()
        {
            Text = Loc.T("Change Shortcut");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            KeyPreview = true;
            ClientSize = new Size((int)Math.Round(360 * Ui.DpiScale), (int)Math.Round(110 * Ui.DpiScale));
            ShowInTaskbar = true;

            Label title = new Label();
            title.Text = Loc.T("Press the new shortcut");
            title.Font = new Font(Ui.Face, 11f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(24, 22);
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = Loc.T("Escape cancels. Delete removes the shortcut.");
            hint.Font = new Font(Ui.Face, 9f, FontStyle.Regular);
            hint.ForeColor = SystemColors.GrayText;
            hint.AutoSize = true;
            hint.Location = new Point(24, 52);
            Controls.Add(hint);
        }

        public static Shortcut Ask(out bool cancelled)
        {
            using (RecorderWindow w = new RecorderWindow())
            {
                w.ShowDialog();
                cancelled = w._cancelled;
                return w._result;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            bool plain = (e.Modifiers & (Keys.Control | Keys.Alt | Keys.Shift)) == Keys.None;

            if (e.KeyCode == Keys.Escape && plain) { _cancelled = true; Close(); return; }
            if ((e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) && plain)
            {
                _cancelled = false;
                _result = null;
                Close();
                return;
            }

            uint mods = 0;
            if ((e.Modifiers & Keys.Control) != 0) mods |= Shortcut.MOD_CONTROL;
            if ((e.Modifiers & Keys.Alt) != 0) mods |= Shortcut.MOD_ALT;
            if ((e.Modifiers & Keys.Shift) != 0) mods |= Shortcut.MOD_SHIFT;

            // Typing alone never becomes a global shortcut.
            if (mods == 0 || (e.KeyCode & Keys.KeyCode) == Keys.None) { Sound.Beep(); return; }
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey) return;

            string text = KeyName(e.KeyCode);
            _cancelled = false;
            _result = Shortcut.Make(mods, (uint)e.KeyCode, text);
            Close();
        }

        private static string KeyName(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            if (code >= Keys.D0 && code <= Keys.D9) return ((char)('0' + (code - Keys.D0))).ToString();
            if (code >= Keys.NumPad0 && code <= Keys.NumPad9) return "Num" + (code - Keys.NumPad0);
            if (code >= Keys.A && code <= Keys.Z) return ((char)('A' + (code - Keys.A))).ToString();
            if (code == Keys.Space) return Loc.T("Space");
            return code.ToString();
        }
    }

    /// Replaces macOS Quick Look: the photo full screen, the rest of the line
    /// a key press away.
    internal sealed class PreviewWindow : Form
    {
        private readonly List<string> _paths;
        private int _index;
        private Bitmap _image;

        private PreviewWindow(List<string> paths, int index)
        {
            _paths = paths;
            _index = index;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.FromPoint(Cursor.Position).Bounds;
            BackColor = Color.FromArgb(24, 24, 24);
            Opacity = 0.96;
            KeyPreview = true;
            DoubleBuffered = true;
            TopMost = true;
            ShowInTaskbar = false;
            LoadImage();
        }

        public static void Show(List<string> paths, int index)
        {
            if (paths == null || paths.Count == 0) return;
            PreviewWindow w = new PreviewWindow(paths, Math.Max(0, Math.Min(index, paths.Count - 1)));
            w.Show();
            w.Activate();
            w.BringToFront();
        }

        private void LoadImage()
        {
            if (_image != null) { _image.Dispose(); _image = null; }
            try
            {
                string p = _paths[_index];
                Bitmap b = Thumbs.LoadPlain(p);
                _image = b;
            }
            catch { }
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Space) { Close(); return; }
            if (e.KeyCode == Keys.Right && _index < _paths.Count - 1) { _index++; LoadImage(); return; }
            if (e.KeyCode == Keys.Left && _index > 0) { _index--; LoadImage(); return; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.FromArgb(24, 24, 24));

            if (_image != null)
            {
                int margin = (int)Math.Round(60 * Ui.DpiScale);
                int top = margin, bottom = margin + (int)Math.Round(40 * Ui.DpiScale);
                double availW = ClientSize.Width - margin * 2;
                double availH = ClientSize.Height - top - bottom;
                double k = Math.Min(availW / _image.Width, availH / _image.Height);
                int w = (int)Math.Round(_image.Width * k);
                int h = (int)Math.Round(_image.Height * k);
                int x = (ClientSize.Width - w) / 2;
                int y = top + (int)((availH - h) / 2);
                g.DrawImage(_image, new Rectangle(x, y, w, h));
            }

            string caption = (_index + 1) + " / " + _paths.Count + "   " +
                             Path.GetFileName(_paths[_index]) +
                             "   —   \u2190 \u2192   Esc";
            using (Font f = new Font(Ui.Face, 10f, FontStyle.Regular))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 240, 240, 240)))
            {
                SizeF sz = g.MeasureString(caption, f);
                g.DrawString(caption, f, b, (ClientSize.Width - sz.Width) / 2f,
                             ClientSize.Height - sz.Height - 14);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _image != null) _image.Dispose();
            base.Dispose(disposing);
        }
    }
}
