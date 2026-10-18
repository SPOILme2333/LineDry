// Hangs images copied to the clipboard, like Win+Shift+S with the copy-only
// option, or an image copied in any app. Each one is saved as a PNG in the
// app's own folder, so on the line it behaves like any other capture.
//
// Off until the user turns it on. Windows has no clipboard change
// notification, so the sequence number is checked twice a second; what was
// copied is only read once its formats say it is a picture -- markup or a URL
// riding along inside the same copy does not make it anything else.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace LineDry
{
    internal sealed class ClipWatcher : IDisposable
    {
        private readonly Action<string> _onImage;
        private readonly Action<string> _onText;
        private Timer _timer;
        private uint _last;
        private byte[] _lastBytes;
        private string _lastPath;

        private bool _busy;

        /// Set when this app itself puts something on the clipboard: that
        /// generation was not copied by the user, so it is skipped.
        private static bool _ownCopy;

        public static void SkipOwnCopy() { _ownCopy = true; }

        public ClipWatcher(Action<string> onImage, Action<string> onText)
        {
            _onImage = onImage;
            _onText = onText;
        }

        private const string EnabledKey = "clipboardEnabled";

        public static bool IsEnabled
        {
            get { return Settings.GetBool(EnabledKey, false); }
            set { Settings.SetBool(EnabledKey, value); }
        }

        private const string TextKey = "clipboardTextEnabled";

        /// Copying a passage brings a note out; on unless it is turned off.
        public static bool TextIsEnabled
        {
            get { return Settings.GetBool(TextKey, true); }
            set { Settings.SetBool(TextKey, value); }
        }

        public void Start()
        {
            if (_timer != null) return;
            _last = Native.GetClipboardSequenceNumber();
            _timer = new Timer();
            _timer.Interval = 500;
            _timer.Tick += delegate { Check(); };
            _timer.Start();
        }

        public void Stop()
        {
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
        }

        /// Format names Windows apps use to mark a copy as private or
        /// temporary (the Windows counterpart of org.nspasteboard.ConcealedType).
        private static readonly string[] PrivateFormats =
        {
            "ExcludeClipboardContentFromMonitorProcessing",
            "CanIncludeInClipboardHistory",
            "CanUploadToCloudClipboard",
            "ClipboardViewerIgnore",
        };

        private void Check()
        {
            uint now = Native.GetClipboardSequenceNumber();
            if (now == _last) return;
            _last = now;
            if (_ownCopy) { _ownCopy = false; return; }
            if (_busy) return;
            _busy = true;
            try { Grab(now); }
            catch { }
            finally { _busy = false; }
        }

        private void Grab(uint stamp)
        {
            IDataObject data = null;
            try { data = Clipboard.GetDataObject(); }
            catch { return; }
            if (data == null) return;

            // Anything a password manager marks private is skipped, whatever it is.
            foreach (string f in PrivateFormats)
                if (data.GetDataPresent(f)) return;

            bool image = data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG");
            bool text = data.GetDataPresent(DataFormats.UnicodeText)
                     || data.GetDataPresent(DataFormats.Text);

            // A real bitmap wins over everything riding along with it, including
            // a file list: WeChat copies a picture from a chat as the bitmap
            // *plus* a FileDrop pointing at its own temp file, and bailing out on
            // that file list is why copied chat images never arrived. The bitmap
            // is also the better half to keep -- a chat app's temp file can be
            // swept away at any moment.
            if (image) { GrabImage(data); return; }

            // A file list is the other way a picture arrives: copying a .png in
            // Explorer carries no bitmap at all, only the file. Its pictures are
            // taken -- the very same filter a drag onto the line uses -- so the
            // two ways of handing the app a file behave alike.
            if (data.GetDataPresent(DataFormats.FileDrop)) { GrabFiles(data); return; }

            if (text) GrabText(data);
        }

        private void GrabText(IDataObject data)
        {
            if (_onText == null || !TextIsEnabled) return;
            string words = null;
            try { words = data.GetData(DataFormats.UnicodeText) as string; } catch { }
            if (words == null) { try { words = data.GetData(DataFormats.Text) as string; } catch { } }
            if (words == null) return;

            words = words.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (words.Length == 0) return;
            if (words.Length > 4000) words = words.Substring(0, 4000);
            // No filter of our own here on purpose. The line already refuses
            // words that are up, so all a watcher-level "same passage twice"
            // rule adds is blocking a passage the owner copied again *after*
            // taking it down -- which is exactly when they want it back.
            try { _onText(words); }
            catch { }
        }

        private void GrabImage(IDataObject data)
        {
            // The switch belongs to the user: with "hang copied images" off, a
            // picture on the clipboard is none of our business, however it got
            // there -- and checking it here means no caller can forget to.
            if (!IsEnabled) return;

            Bitmap copy = null;
            try
            {
                object raw = data.GetDataPresent("PNG") ? data.GetData("PNG") : data.GetData(DataFormats.Bitmap);
                if (raw == null) return;
                Image img = raw as Image;
                if (img != null) copy = new Bitmap(img);
                else
                {
                    MemoryStream ms = raw as MemoryStream;
                    if (ms == null) return;
                    using (Image fromStream = Image.FromStream(ms)) copy = new Bitmap(fromStream);
                }
            }
            catch { return; }
            if (copy == null) return;

            try
            {
                if (copy.Width <= 0 || copy.Height <= 0) return;
                if ((long)copy.Width * copy.Height > 80000000L) return;

                byte[] png;
                using (MemoryStream ms = new MemoryStream())
                {
                    copy.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
                if (png.Length > 64 * 1024 * 1024) return;

                // Some apps put the same image on the clipboard again; it
                // hangs once, unless it has been taken down since.
                if (_lastBytes != null && _lastBytes.Length == png.Length && _lastPath != null && File.Exists(_lastPath))
                {
                    bool same = true;
                    for (int i = 0; i < png.Length; i++)
                        if (png[i] != _lastBytes[i]) { same = false; break; }
                    if (same) return;
                }

                Folders.EnsureInbox();
                string name = "Clipboard " + Stamp() + ".png";
                string path = Path.Combine(Folders.Inbox, name);
                int n = 2;
                while (File.Exists(path))
                    path = Path.Combine(Folders.Inbox, "Clipboard " + Stamp() + " " + (n++) + ".png");

                File.WriteAllBytes(path, png);
                _lastBytes = png;
                _lastPath = path;
                _onImage(path);
            }
            catch { }
            finally { copy.Dispose(); }
        }

        /// Pictures copied as files. Each one hangs where it lies, exactly as if
        /// it had been dragged onto the line, so nothing is copied, moved or
        /// deleted -- taking the card down leaves the file untouched. A list
        /// with no picture in it (a document, a folder) is not ours to hang.
        private void GrabFiles(IDataObject data)
        {
            if (_onImage == null || !IsEnabled) return;
            string[] raw = null;
            try { raw = data.GetData(DataFormats.FileDrop) as string[]; } catch { }
            if (raw == null || raw.Length == 0) return;
            foreach (string f in raw)
            {
                if (!File.Exists(f) || !Thumbs.IsImage(f)) continue;
                try { _onImage(f); }
                catch { }
            }
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
