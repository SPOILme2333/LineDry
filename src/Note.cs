// The one window in this app that is allowed to take the foreground, and only
// because it is opened on purpose: words have to be typed into somewhere, and a
// window that never activates cannot receive keys. Everything the user sees
// afterwards is a card on the line, not a window.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace LineDry
{
    internal sealed class NoteEditDialog : Form
    {
        public string Value { get { return _box.Text; } }

        private readonly TextBox _box;

        public NoteEditDialog(string text)
        {
            Text = Loc.T("Words");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            TopMost = true;                 // it was asked for, so it comes to the front
            ClientSize = new Size(400, 260);
            BackColor = Color.FromArgb(252, 250, 243);
            Font = Ui.Font(11, FontStyle.Regular);

            _box = new TextBox();
            _box.Multiline = true;
            _box.AcceptsReturn = true;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Color.FromArgb(255, 255, 252);
            _box.ForeColor = Color.FromArgb(28, 28, 30);
            _box.Font = Ui.Font(11, FontStyle.Regular);
            _box.Bounds = new Rectangle(18, 18, 364, 182);
            _box.Text = text;
            Controls.Add(_box);

            Button save = new Button();
            save.Text = Loc.T("Save");
            save.DialogResult = DialogResult.OK;
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderColor = Color.FromArgb(210, 203, 184);
            save.BackColor = Color.FromArgb(255, 255, 252);
            save.ForeColor = Color.FromArgb(40, 40, 44);
            save.Bounds = new Rectangle(286, 212, 96, 30);
            Controls.Add(save);

            Button cancel = new Button();
            cancel.Text = Loc.T("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderColor = Color.FromArgb(210, 203, 184);
            cancel.BackColor = Color.FromArgb(252, 250, 243);
            cancel.ForeColor = Color.FromArgb(90, 86, 74);
            cancel.Bounds = new Rectangle(182, 212, 96, 30);
            Controls.Add(cancel);

            AcceptButton = save;
            CancelButton = cancel;
        }

        /// A hairline around the sheet, so the typing area reads as paper.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen edge = new Pen(Color.FromArgb(228, 221, 203), 1f))
                e.Graphics.DrawRectangle(edge, _box.Left - 1, _box.Top - 1,
                    _box.Width + 1, _box.Height + 1);
        }
    }
}