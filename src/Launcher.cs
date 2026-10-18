// The app itself. Every state machine here is a port of AppDelegate.swift:
// when the line comes down, when it tucks away, and what the tray menu offers.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LineDry
{
    internal sealed class Launcher : ApplicationContext
    {
        private LineForm _form;
        private NotifyIcon _tray;
        private ShotWatcher _shot;
        private ShotWatcher _safety;
        private readonly List<ShotWatcher> _extras = new List<ShotWatcher>();
        private ClipWatcher _clip;
        private Timer _loop;
        private readonly Stopwatch _clock = new Stopwatch();
        private double _lastT;
        private double _lastFrame;
        private bool _trimmedAtStart;
        private bool _trimmedEmpty;
        private double _hiddenSince = -1;
        private bool _trimmedIdle;

        private double _fsCheckedAt = -999;
        private bool _fsBlocked;
        private bool _render = true;
        private ContextMenuStrip _openMenu;
        /// The ceiling the app was asked for: three places with writing on them
        /// at once, no more. The line enforces it; this is only for the menu.
        private const int MaxNotes = Line.MaxTexts;
        private bool _menuArmed;
        private bool _menuShouldClose;
        private bool _clickOutside;

        private double _menuOpenedAt;

        private IntPtr _mouseHook;
        private Native.HookProc _mouseHookProc;

        private HotKeyId _hotKey;

        // Panel / reveal state, ported from AppDelegate.
        private bool _pinned;
        private double _peekUntil = -1;
        private double _hotZoneSince = -1;
        private bool _topSuppressed;
        private double _awaySince = -1;
        private bool _keepOpen;
        private bool _wanted;
        private bool _wantedPrev = true;

        private double Now { get { return _clock.Elapsed.TotalSeconds; } }

        /// How long the pointer rests against the top edge before the line
        /// comes down: short enough to feel instant, long enough that a quick
        /// trip to the title bar does not trigger it.
        private const double RevealDelay = 0.25;

        /// The same guard while the left button is held. A drag toward the top
        /// is an explicit "bring it down" -- something is being carried -- so it
        /// must not wait out the full hover delay meant to absorb stray passes
        /// over the edge.
        private const double RevealDragDelay = 0.08;
        /// How long the pointer is away before the line tucks back up.
        private const double RetractDelay = 0.5;

        // ------------------------------------------------------------ preferences

        public static bool OpensFromTopEdge
        {
            get { return !Settings.GetBool("menuBarRevealOff", false); }
            set { Settings.SetBool("menuBarRevealOff", !value); }
        }

        public static bool PeeksAtNew
        {
            get { return !Settings.GetBool("quietHang", false); }
            set { Settings.SetBool("quietHang", !value); }
        }

        public static bool OverFullScreen
        {
            get { return Settings.GetBool("showOverFullScreen", false); }
            set { Settings.SetBool("showOverFullScreen", value); }
        }

        /// How long the line takes to slide away, in milliseconds. 0 is "at
        /// once"; anything outside 0..4000 is treated as an unusable value and
        /// brought back to the default rather than obeyed.
        public static int RetractMs
        {
            get
            {
                int v = Settings.GetInt("retractMs", DefaultRetractMs);
                if (v < 0 || v > 4000) return DefaultRetractMs;
                return v;
            }
            set { Settings.SetInt("retractMs", value < 0 ? 0 : (value > 4000 ? 4000 : value)); }
        }

        private const int DefaultRetractMs = 320;

        public static int MaxExtraFolders { get { return 3; } }

        public static List<string> ExtraFolders
        {
            get
            {
                List<string> r = new List<string>();
                foreach (string p in Settings.GetStrings("extraFolders"))
                    if (Directory.Exists(p)) r.Add(p);
                return r;
            }
            set { Settings.SetStrings("extraFolders", value.ToArray()); }
        }

        // ------------------------------------------------------------------ start

        public Launcher()
        {
            Loc.Load();
            // A switch that is on keeps its registered command pointing at this
            // executable: if the folder was moved, logon would fail silently.
            if (LaunchAtLogin) LaunchAtLogin = true;



            _form = new LineForm();
            _form.RetractMs = RetractMs;
            _form.PlaceOn(Screen.FromPoint(Cursor.Position));
            _form.MenuFactory = BuildItemMenu;
            _form.MenuIsOpen = delegate { return _openMenu != null; };
            _form.DismissMenu = delegate
            {
                if (_openMenu != null) { _openMenu.Close(); _openMenu = null; }
            };
            _form.DropRequested = delegate
            {
                _keepOpen = true;
                _wanted = true;
                _wantedPrev = true;
                Present();
                Reveal(false, 4.0);
            };
            _form.OpenRequested = OpenItem;
            _form.MarkupRequested = MarkupItem;
            _form.PreviewRequested = PreviewItem;
            _form.ShowInExplorer = delegate (Guid id) { ShowInExplorer(id); };
            _form.Model.Changed += delegate { _render = true; };

            SetUpTray();
            InstallMouseHook();
            RegisterShortcut();
            StartWatchers();

            _clip = new ClipWatcher(delegate (string path) { HangCapture(path); }, ShowText);
            if (ClipWatcher.IsEnabled || ClipWatcher.TextIsEnabled) _clip.Start();

            if (!Settings.GetBool("welcomed", false))
            {
                Settings.SetBool("welcomed", true);
                _keepOpen = true;
                _wanted = true;
                SetRevealed(true);
                _peekUntil = Now + 5;
            }

            _clock.Start();
            _lastT = 0;
            _loop = new Timer();
            _loop.Interval = 16;
            _loop.Tick += Tick;
            _loop.Start();

            _form.Show();
            Refresh();
            _form.Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_loop != null) _loop.Stop();
                UnregisterShortcut();
                if (_clip != null) _clip.Dispose();
                if (_shot != null) _shot.Dispose();
                if (_safety != null) _safety.Dispose();
                foreach (ShotWatcher w in _extras) w.Dispose();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- watchers

        private void StartWatchers()
        {
            if (_shot != null) _shot.Dispose();
            if (_safety != null) { _safety.Dispose(); _safety = null; }
            foreach (ShotWatcher w in _extras) w.Dispose();
            _extras.Clear();

            string folder = TakeOverScreenshots ? Folders.Inbox : Folders.Screenshots;
            _shot = new ShotWatcher(folder,
                delegate (string p) { HangCapture(p); },
                delegate { _form.Model.Prune(); });
            _shot.Start();

            // In take-over mode a second watcher sits on the real screenshots
            // folder: if Windows still drops captures there (the Snipping Tool
            // keeps its own setting), they hang all the same.
            if (TakeOverScreenshots &&
                !string.Equals(folder.TrimEnd('\\'), Folders.Screenshots.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _safety = new ShotWatcher(Folders.Screenshots,
                    delegate (string p) { HangCapture(p); },
                    delegate { _form.Model.Prune(); });
                _safety.Start();
            }

            // A folder may also be watched, like the one another screenshot
            // app saves to. An image both report hangs once.
            foreach (string f in ExtraFolders)
            {
                ShotWatcher extra = new ShotWatcher(f,
                    delegate (string p) { HangCapture(p); },
                    delegate { _form.Model.Prune(); });
                extra.Start();
                _extras.Add(extra);
            }
        }

        /// Taking screenshots over: Windows has no equivalent of the macOS
        /// screencapture defaults, but it does honour the Screenshots known
        /// folder, so pointing that at our own folder is the closest thing.
        public static bool TakeOverScreenshots
        {
            get { return Settings.GetBool("inboxEnabled", false); }
        }

        private void SetInbox(bool on)
        {
            Settings.SetBool("inboxEnabled", on);
            if (on)
            {
                Folders.EnsureInbox();
                if (Settings.GetString("inboxSavedFolder", null) == null)
                    Settings.SetString("inboxSavedFolder", Folders.RawScreenshotsFolderValue() ?? "");
                Folders.SetScreenshotsFolder(Folders.Inbox);
            }
            else
            {
                string saved = Settings.GetString("inboxSavedFolder", "");
                Folders.SetScreenshotsFolder(saved.Length == 0 ? null : saved);
            }
            StartWatchers();
            UpdateTrayIcon();
        }

        // ------------------------------------------------------------------- panel

        private bool FullScreenBlocks()
        {
            if (OverFullScreen) return false;
            // This asks the shell, and it is consulted on every frame while the
            // line is tucked away; the answer cannot change faster than this.
            double now = Now;
            if (now - _fsCheckedAt < 0.4) return _fsBlocked;
            _fsCheckedAt = now;
            try
            {
                int state;
                if (Native.SHQueryUserNotificationState(out state) != 0) { _fsBlocked = false; return false; }
                _fsBlocked = state == Native.QUNS_RUNNING_D3D_FULL_SCREEN
                    || state == Native.QUNS_BUSY
                    || state == Native.QUNS_PRESENTATION_MODE;
            }
            catch { _fsBlocked = false; }
            return _fsBlocked;
        }

        /// An empty line goes away, unless it was opened on purpose.
        private void ItemsChanged()
        {
            if (_form.Model.LiveCount != 0 || _keepOpen) return;
            _pendingEmptyAt = Now + 0.7;
        }

        private double _pendingEmptyAt = -1;

        private void Refresh()
        {
            bool blocked = FullScreenBlocks();
            if (_wanted && !blocked) Present(); else Dismiss();
        }

        /// A static line paints nothing: only a state change or an animation
        /// in progress justifies another frame. Rendering every tick is what
        /// kept the process at 130 MB doing nothing.
        private void Present()
        {
            if (!_form.Visible) _form.Show();
            else if (_render || _form.Animating) _form.Render();
        }

        private void Dismiss()
        {
            SetRevealed(false);
        }

        private void SetRevealed(bool on)
        {
            if (_form.Model.Revealed == on) return;
            _form.Model.Revealed = on;
            _render = true;
            // It always comes down showing the newest.
            if (on) _form.Model.Scroll = 0;
            if (!on)
            {
                _pinned = false;
                _peekUntil = -1;
            }
        }

        private void Reveal(bool pinned, double peekFor)
        {
            if (pinned) _pinned = true;
            if (peekFor > 0) _peekUntil = Now + peekFor;
            _awaySince = -1;
            SetRevealed(true);
        }

        private void Toggle()
        {
            if (_form.Model.Revealed)
            {
                SetRevealed(false);
                if (_form.Model.LiveCount == 0)
                {
                    _keepOpen = false;
                    _wanted = false;
                    Refresh();
                }
            }
            else
            {
                _keepOpen = true;
                _wanted = true;
                _form.PlaceOn(Screen.FromPoint(Cursor.Position));
                _form.UpdateCapacity();
                Refresh();
                Reveal(true, 0);
            }
        }

        /// Screen.FromPoint hands back a fresh object each call, so the
        /// reference comparison would rebuild the buffer on every frame.
        private static bool SameScreen(Screen a, Screen b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase);
        }

        /// The top edge as a gesture. Six pixels is enough to rest a bare
        /// pointer on, but it is a cruel target for a hand that is already
        /// holding a button and steering a drag -- and while the line is up
        /// there the layered window is fully transparent, so the drag does not
        /// reach it either. A held button therefore gets a band it can hit.
        private static bool InTopBand(Screen s, Point p, bool dragging)
        {
            if (s == null) return false;
            Rectangle b = s.Bounds;
            int h = dragging
                ? Math.Max(24, (int)Math.Round(48 * Ui.DpiScale))
                : Math.Max(3, (int)Math.Round(4 * Ui.DpiScale));
            return p.Y >= b.Y && p.Y < b.Y + h && p.X >= b.X && p.X < b.Right;
        }

        // ----------------------------------------------------------------- the loop

        private void Tick(object sender, EventArgs e)
        {
            double t = _clock.Elapsed.TotalSeconds;
            double dt = t - _lastT;
            _lastT = t;
            if (dt <= 0) return;
            if (dt > 0.25) dt = 0.25;

            WatchOpenMenu();



            Point mp = Cursor.Position;
            Screen sc = Screen.FromPoint(mp);
            if (!SameScreen(sc, _form.Target))
            {
                _form.PlaceOn(sc);
                _render = true;
            }

            if (_pendingEmptyAt > 0 && t >= _pendingEmptyAt)
            {
                _pendingEmptyAt = -1;
                if (_form.Model.LiveCount == 0 && !_keepOpen)
                {
                    _wanted = false;
                    Refresh();
                }
            }

            _wanted = _form.Model.LiveCount > 0 || _keepOpen;
            // Only the transition into "wanted" needs a Present; while wanted,
            // the render gate in Present() decides frame by frame.
            bool wantedNow = _wanted;
            if (wantedNow != _wantedPrev)
            {
                _wantedPrev = wantedNow;
                Refresh();
            }

            bool held = Down(0x01);                 // a button down means something is being carried
            bool inTop = InTopBand(sc, mp, held);
            if (!inTop) _topSuppressed = false;

            if (!_form.Model.Revealed)
            {
                double wait = held ? RevealDragDelay : RevealDelay;
                if (OpensFromTopEdge && inTop && !_topSuppressed && !FullScreenBlocks())
                {
                    if (_hotZoneSince < 0) _hotZoneSince = t;
                    if (t - _hotZoneSince >= wait)
                    {
                        _hotZoneSince = -1;
                        if (!SameScreen(sc, _form.Target))
                        {
                            _form.PlaceOn(sc);
                            _render = true;
                        }
                        _wanted = true;
                        Present();
                        Reveal(false, 0);
                    }
                }
                else _hotZoneSince = -1;
            }
            else
            {
                _form.UpdatePointer(mp);
                bool inside = _form.Bounds.Contains(mp);
                if (inside && _pinned) _pinned = false;

                bool busy = _pinned
                    || _form.Model.DraggingId != Guid.Empty
                    || _form.Model.PressedId != Guid.Empty
                    || _openMenu != null
                    || (t < _peekUntil);

                if (inside || busy) _awaySince = -1;
                else
                {
                    if (_awaySince < 0) _awaySince = t;
                    if (t - _awaySince >= RetractDelay)
                    {
                        _awaySince = -1;

                        SetRevealed(false);
                    }
                }
            }

            _form.Step(dt);
            // Nothing on screen means nothing to draw: while the line is away the
            // occasional "something changed" repaint only faulted several MB of
            // bitmaps back into memory, which is exactly what the idle trim had
            // just handed back. The frame that ends an animation is still drawn,
            // because Animating stays true until the last step.
            bool onScreen = _form.Model.Revealed || _form.Animating;
            if ((_render || _form.Animating) && onScreen)
            {
                // A state change paints at once; an animation runs at ~30 fps,
                // which is indistinguishable on slow cloth-and-glass motion and
                // halves the cost of every frame. The model raises Changed on
                // every spring step, so "dirty" must not bypass the cap while
                // something is still moving -- that was the whole cost of hover.
                // Nothing needs more than 60 fps here, and a moving line is
                // indistinguishable at 30; the model raises Changed on every
                // spring step, so an uncapped "dirty" path would run flat out.
                double minGap = _form.Animating ? 0.033 : 0.016;
                if (t - _lastFrame >= minGap)
                {
                    _render = false;
                    _lastFrame = t;
                    _form.Render();
                }
                else _render = true;   // never drop the frame that ends an animation
            }

            // Idle moments worth handing memory back: once the start-up settle
            // is over, and whenever the line has nothing on it at all.
            if (!_trimmedAtStart && t > 4.0) { _trimmedAtStart = true; TrimMemory(); }
            if (_form.Model.LiveCount == 0 && !_form.Animating)
            {
                if (!_trimmedEmpty) { _trimmedEmpty = true; TrimMemory(); }
            }
            else if (_form.Model.LiveCount > 0) _trimmedEmpty = false;

            // The line spends most of its life out of sight; a few seconds after
            // it goes away is the cheapest moment to hand the pages back, and
            // the small hitch is invisible because nothing is on screen.
            // Keyed on the line being away, not on it being still: Animating
            // flickers between gusts, and a condition that resets on every
            // flicker would never reach the ten second mark.
            if (!_form.Model.Revealed)
            {
                if (_hiddenSince < 0) _hiddenSince = t;
                else if (!_trimmedIdle && t - _hiddenSince > 10.0)
                {
                    _trimmedIdle = true;
                    TrimMemory();
                }
            }
            else
            {
                _hiddenSince = -1;
                _trimmedIdle = false;
            }

            // A hidden ornament has no use for 62 Hz. The one thing worth
            // noticing is the pointer coming to rest at the top edge, and that
            // deliberately waits a quarter of a second before it counts.
            int want = (onScreen || _form.Animating || _openMenu != null) ? 16 : 50;
            if (_loop.Interval != want) _loop.Interval = want;

            // Tucked away and settled: leave no window behind at all.
            if (!_form.Model.Revealed && !_form.Animating && _form.Visible && _openMenu == null) _form.Hide();
        }

        // --------------------------------------------------------------- capturing

        private void HangCapture(string path)
        {
            Screen sc = Screen.FromPoint(Cursor.Position);
            if (sc != _form.Target) _form.PlaceOn(sc);
            _form.UpdateCapacity();
            _wanted = true;
            _keepOpen = _keepOpen || _form.Model.LiveCount == 0;
            Refresh();

            Guid id = _form.Model.Hang(path, false, false);
            if (id == Guid.Empty)
            {
                _keepOpen = _form.Model.LiveCount > 0 && _keepOpen;
                ItemsChanged();
                _render = true;
                return;
            }
            if (PeeksAtNew) Reveal(false, 2.5);
            _render = true;
        }

        // ----------------------------------------------------------- item actions

        private Pegged Item(Guid id) { return _form.Model.Find(id); }

        private void OpenItem(Guid id)
        {
            Pegged p = Item(id);
            if (p == null) return;
            if (p.IsText)
            {
                // The same gesture as a photo: it opens in the editor for its
                // kind, here a text file written from the words.
                string file = _form.Model.TextFileFor(id);
                if (file == null) return;
                try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
                catch { }
                WatchTextEdit(id, file);
                return;
            }
            try { Process.Start(new ProcessStartInfo(p.Path) { UseShellExecute = true }); }
            catch { }
        }

        /// Notepad writes over the file; when it goes quiet the words are read
        /// back and the sheet painted again -- the round trip a photo gets after
        /// Paint saves.
        private void WatchTextEdit(Guid id, string file)
        {
            FileSystemWatcher w = null;
            Timer settle = new Timer();
            settle.Interval = 400;
            settle.Tick += delegate
            {
                settle.Stop();
                if (_form.Model.ReadBackText(id, file)) _render = true;
                settle.Dispose();
                if (w != null) { w.EnableRaisingEvents = false; w.Dispose(); }
            };
            try
            {
                w = new FileSystemWatcher(Path.GetDirectoryName(file), Path.GetFileName(file));
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                w.Changed += delegate { settle.Stop(); settle.Start(); };
                w.EnableRaisingEvents = true;
            }
            catch { }
            settle.Start();
        }

        /// Press and hold: open the photo in the system image editor's closest
        /// Windows relative, Paint. A recording opens in its own player.
        private void MarkupItem(Guid id)
        {
            Pegged p = Item(id);
            if (p == null) return;
            if (p.IsText)
            {
                string file = _form.Model.TextFileFor(id);
                if (file == null) return;
                try { Process.Start("notepad.exe", "\"" + file + "\""); }
                catch { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } }
                WatchTextEdit(id, file);
                return;
            }
            try
            {
                if (p.IsRecording)
                    Process.Start(new ProcessStartInfo(p.Path) { UseShellExecute = true });
                else
                    Process.Start("mspaint.exe", "\"" + p.Path + "\"");
                WatchForEdit(p.Path);
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo(p.Path) { UseShellExecute = true }); }
                catch { }
            }
        }

        /// Paint writes over the original; the line picks the change up and
        /// redraws the card, the way the original does after Markup saves.
        private void WatchForEdit(string path)
        {
            FileSystemWatcher w = null;
            Timer settle = new Timer();
            settle.Interval = 400;
            settle.Tick += delegate
            {
                settle.Stop();
                _form.Model.ReloadThumbnail(path);
                _render = true;
                settle.Dispose();
                if (w != null) { w.EnableRaisingEvents = false; w.Dispose(); }
            };
            try
            {
                string dir = Path.GetDirectoryName(path);
                w = new FileSystemWatcher(dir, Path.GetFileName(path));
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                w.Changed += delegate { settle.Stop(); settle.Start(); };
                w.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void PreviewItem(Guid id)
        {
            Pegged p = Item(id);
            if (p == null) return;
            if (p.IsText)
            {
                // The preview shows the sheet itself, so what you look at is
                // exactly the card, only bigger.
                string png = _form.Model.SheetImageFor(id);
                if (png == null) return;
                PreviewWindow.Show(new List<string>(new string[] { png }), 0);
                return;
            }
            // GDI+ cannot decode a video frame; the player is the honest preview.
            if (p.IsRecording) { OpenItem(id); return; }
            List<string> all = new List<string>();
            foreach (Pegged e in _form.Model.Items) if (!e.Falling) all.Add(e.Path);
            int index = all.IndexOf(p.Path);
            PreviewWindow.Show(all, index < 0 ? 0 : index);
        }

        private void ShowInExplorer(Guid id)
        {
            Pegged p = Item(id);
            if (p == null) return;
            if (p.IsText)
            {
                string file = _form.Model.TextFileFor(id);
                if (file == null) return;
                try { Process.Start("explorer.exe", "/select,\"" + file + "\""); }
                catch { }
                return;
            }
            try { Process.Start("explorer.exe", "/select,\"" + p.Path + "\""); }
            catch { }
        }

        private ContextMenuStrip BuildItemMenu(Guid id)
        {
            Pegged p = Item(id);
            ContextMenuStrip m = new ContextMenuStrip();
            m.ShowImageMargin = false;
            if (p == null) return m;

            // A sheet answers the same questions a photo does. There is no file
            // behind the words, so one is written the moment an action asks for
            // it; only "Move to Recycle Bin" has no meaning here, because there
            // is nothing on disk to throw away.
            if (p.IsText)
            {
                Add(m, Loc.T("Copy"), delegate { _form.Model.Copy(id); });
                Add(m, Loc.T("Open"), delegate { OpenItem(id); });
                Add(m, Loc.T("Preview"), delegate { PreviewItem(id); });
                Add(m, Loc.T("Edit text"), delegate { MarkupItem(id); });
                Add(m, Loc.T("Show in File Explorer"), delegate { ShowInExplorer(id); });
                Add(m, Loc.T("Save to Desktop"), delegate { _form.Model.SaveTextToDesktop(id); });
                m.Items.Add(new ToolStripSeparator());
                Add(m, Loc.T("Take down"), delegate { _form.Model.Discard(id, false); });
                ToolStripMenuItem tl = Add(m, Loc.T("Take down everything to the left"),
                    delegate { _form.Model.TakeDownSide(id, true); });
                tl.Enabled = _form.Model.Neighbours(id, true).Count > 0;
                ToolStripMenuItem tr = Add(m, Loc.T("Take down everything to the right"),
                    delegate { _form.Model.TakeDownSide(id, false); });
                tr.Enabled = _form.Model.Neighbours(id, false).Count > 0;
                TrackMenu(m);
                return m;
            }

            bool recording = p.IsRecording;
            bool inInbox = _form.Model.IsInInbox(id);

            Add(m, Loc.T("Copy"), delegate { _form.Model.Copy(id); });
            Add(m, Loc.T("Open"), delegate { OpenItem(id); });
            Add(m, Loc.T("Preview"), delegate { PreviewItem(id); });
            if (!recording) Add(m, Loc.T("Edit"), delegate { MarkupItem(id); });
            Add(m, Loc.T("Show in File Explorer"), delegate { ShowInExplorer(id); });
            if (inInbox) Add(m, Loc.T("Save to Desktop"), delegate { _form.Model.SaveToDesktop(id); });

            m.Items.Add(new ToolStripSeparator());

            if (inInbox)
                Add(m, Loc.T("Discard"), delegate { _form.Model.Discard(id, false); });
            else
            {
                Add(m, Loc.T("Take down"), delegate { _form.Model.Discard(id, false); });
                Add(m, Loc.T("Move to Recycle Bin"), delegate { _form.Model.Trash(id, false); });
            }

            ToolStripMenuItem left = Add(m, Loc.T("Take down everything to the left"),
                delegate { _form.Model.TakeDownSide(id, true); });
            left.Enabled = _form.Model.Neighbours(id, true).Count > 0;

            ToolStripMenuItem right = Add(m, Loc.T("Take down everything to the right"),
                delegate { _form.Model.TakeDownSide(id, false); });
            right.Enabled = _form.Model.Neighbours(id, false).Count > 0;

            TrackMenu(m);
            return m;
        }

        // ------------------------------------------------------------------- tray

        private void SetUpTray()
        {
            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = null;
            _tray.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowTrayMenu();
            };
            _tray.MouseDoubleClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Toggle();
            };
            _tray.Visible = true;
            UpdateTrayIcon();
        }

        private void UpdateTrayIcon()
        {
            if (_tray == null) return;
            bool on = ClipWatcher.IsEnabled;
            int size = (int)Math.Round(16 * Ui.DpiScale);
            Icon old = _tray.Icon;
            _tray.Icon = Draw.Tshirt(size, on);
            if (old != null) old.Dispose();
            _tray.Text = on ? Loc.T("LineDry, hanging copied images") : "LineDry";
            if (_tray.Text.Length > 63) _tray.Text = _tray.Text.Substring(0, 63);
        }

        private void ShowTrayMenu()
        {
            // Clicking the icon again puts the menu away.
            if (_openMenu != null)
            {
                _openMenu.Close();
                _openMenu = null;
                return;
            }
            ContextMenuStrip m = BuildTrayMenu();
            TrackMenu(m);
            // Owned by the always-topmost strip, so the popup inherits that.
            m.Show(_form, _form.PointToClient(Cursor.Position));
        }

        /// A menu shown by a process that can never be foreground gets none of
        /// the shell's usual dismissal: no activation change ever reaches us,
        /// so nothing closes it. The 16 ms loop watches instead, exactly the
        /// way it watches the top edge.
        private void TrackMenu(ContextMenuStrip menu)
        {
            _openMenu = menu;
            _menuArmed = false;
            _menuShouldClose = false;
            _clickOutside = false;
            _menuOpenedAt = Now;
            menu.Closed += delegate
            {
                if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
            };
        }

        private static bool Down(int vk)
        {
            return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static bool PointInMenus(ToolStripDropDown menu, Point screen)
        {
            // Visible, not IsWindowVisible: a submenu caught mid-close would
            // otherwise stop counting as "inside" while its pixels are still on
            // screen, and the click that was meant for it would read as outside.
            if (!menu.Visible && !MenuOnScreen(menu)) return false;
            // Only the mapped client rectangle is trusted: Bounds can be stale
            // or empty right after the menu is shown, and a union with an empty
            // rectangle drags the corner to the screen origin -- which would
            // make every click near the top of the screen read as "inside".
            // An unusable rectangle must never swallow the closing click.
            Rectangle box = menu.IsHandleCreated
                ? menu.RectangleToScreen(menu.ClientRectangle)
                : Rectangle.Empty;
            if (box.Width > 0 && box.Height > 0 && box.Contains(screen)) return true;
            foreach (ToolStripItem item in menu.Items)
            {
                ToolStripDropDownItem dd = item as ToolStripDropDownItem;
                if (dd != null && dd.HasDropDownItems
                    && PointInMenus(dd.DropDown, screen)) return true;
            }
            return false;
        }



        /// The OS is the only honest witness of whether the menu is still up:
        /// WinForms' Visible can disagree with the window it owns.
        private static int _ourPid = -1;

        /// True when the window under the point belongs to somebody else. This
        /// needs none of our geometry to be right, so a bad menu rectangle can
        /// never defeat it.
        private static bool ClickIsForeign(Point where)
        {
            try
            {
                if (_ourPid < 0) _ourPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                Native.POINT p = new Native.POINT(where.X, where.Y);
                IntPtr h = Native.WindowFromPoint(p);
                if (h == IntPtr.Zero) return false;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                return pid != 0 && pid != (uint)_ourPid;
            }
            catch { return false; }
        }

        /// Takes the base type on purpose: a submenu is a ToolStripDropDownMenu,
        /// not a ContextMenuStrip, so casting it would yield null and the whole
        /// recursion would quietly do nothing.
        private static bool MenuOnScreen(ToolStripDropDown m)
        {
            try
            {
                if (m == null) return false;
                if (m.IsHandleCreated && Native.IsWindowVisible(m.Handle)) return true;
                // A submenu is its own top-level window: if any of them is still
                // up, the menu is still up. Missing this is how a submenu
                // outlived its parent and sat there forever.
                foreach (ToolStripItem item in m.Items)
                {
                    ToolStripDropDownItem dd = item as ToolStripDropDownItem;
                    if (dd != null && dd.HasDropDownItems && MenuOnScreen(dd.DropDown)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// Close, then hide, then hide the window itself; whatever survives all
        /// three stays tracked so the next tick can try again -- a menu we
        /// "consider closed" while it is still on screen never comes back.
        private static void ForceMenuAway(ToolStripDropDown m)
        {
            if (m == null) return;
            // Close the submenus first, deepest first: with AutoClose off they
            // belong to nobody, and closing only the parent can leave one of
            // them stranded on screen.
            foreach (ToolStripItem item in m.Items)
            {
                ToolStripDropDownItem dd = item as ToolStripDropDownItem;
                if (dd != null && dd.HasDropDownItems) ForceMenuAway(dd.DropDown);
            }
            try { m.Close(); } catch { }
            if (MenuOnScreen(m)) { try { m.Hide(); } catch { } }
            if (MenuOnScreen(m)) { try { Native.ShowWindow(m.Handle, 0); } catch { } }
        }

        /// Gives the pages back to the system. A tray ornament that has finished
        /// drawing has no business holding 50 MB of working set, and the pages
        /// return on demand, so this trades a little latency for a lot of quiet.
        /// Run only at genuinely idle moments -- doing it often would thrash.
        private static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(),
                    new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        /// A low-level mouse hook reports every physical button press no matter
        /// who owns the focus or the capture -- unlike polling, it cannot miss
        /// a click between two frames.
        private void InstallMouseHook()
        {
            try
            {
                _mouseHookProc = OnMouseHook;
                _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseHookProc,
                    Native.GetModuleHandle(null), 0);
            }
            catch { _mouseHook = IntPtr.Zero; }
        }

        private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && _openMenu != null && _menuArmed)
                {
                    int msg = wParam.ToInt32();
                    if (msg == 0x0201 || msg == 0x0204 || msg == 0x0207)
                    {
                        Native.MSLLHOOKSTRUCT hit = (Native.MSLLHOOKSTRUCT)
                            System.Runtime.InteropServices.Marshal.PtrToStructure(
                                lParam, typeof(Native.MSLLHOOKSTRUCT));
                        Point where = new Point(hit.pt.X, hit.pt.Y);
                        if (!PointInMenus(_openMenu, where))
                        {
                            _clickOutside = true;
                        }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void WatchOpenMenu()
        {
            ContextMenuStrip m = _openMenu;
            if (m == null) return;
            if (!MenuOnScreen(m))
            {
                _openMenu = null;
                return;
            }

            bool lmb = Down(0x01), rmb = Down(0x02), esc = Down(0x1B);
            if (!_menuArmed)
            {
                // The click that opened it is still held for a frame or two.
                // A key that reads stuck must not disarm us forever, so a short
                // grace period arms it regardless.
                if (!lmb && !rmb && !esc) _menuArmed = true;
                else if (Now - _menuOpenedAt > 0.6) _menuArmed = true;
                return;
            }



            if (!_menuShouldClose)
            {
                if (esc) _menuShouldClose = true;
                else if (_clickOutside) { _menuShouldClose = true; }
                else if (lmb || rmb)
                {
                    Point where = Cursor.Position;
                    if (ClickIsForeign(where))
                    {
                        _menuShouldClose = true;
                    }
                    else if (!PointInMenus(m, where))
                    {
                        _menuShouldClose = true;
                    }
                }
                if (!_menuShouldClose) return;
                _clickOutside = false;
            }

            ForceMenuAway(m);
            if (!MenuOnScreen(m)) _openMenu = null;
        }


        /// "At once", or the duration in seconds -- a number and a unit, so it
        /// reads the same in every language.
        private static string RetractLabel(int ms)
        {
            if (ms <= 0) return Loc.T("At once");
            return (ms / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                   + " " + Loc.T("sec");
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            // A check mark is not a glyph drawn beside the text: it is the item's
            // IMAGE, so it lives in the image column. With the image margin off
            // there is no column to draw it in, and a checked row came out
            // pixel-identical to an unchecked one. This menu has checkable rows,
            // so it keeps the column.
            m.ShowImageMargin = true;
            // A settings sheet, not a one-shot menu: clicking a row runs it and
            // the menu stays put, so several switches can be flipped in one
            // visit. It still goes away on a click outside itself, on Escape,
            // or when the tray icon is clicked again -- WatchOpenMenu handles
            // all of that, as it did before.
            m.AutoClose = false;

            ToolStripMenuItem toggle = new ToolStripMenuItem(
                _form.Model.Revealed ? Loc.T("Hide line") : Loc.T("Show line"));
            Shortcut sc = Shortcut.Current;
            if (sc != null) toggle.ShortcutKeyDisplayString = sc.Display();
            toggle.Click += delegate { Toggle(); };
            m.Items.Add(toggle);

            ToolStripMenuItem clear = new ToolStripMenuItem(Loc.T("Take everything down"));
            clear.Enabled = _form.Model.LiveCount > 0;
            clear.Click += delegate { _form.Model.Clear(); };
            m.Items.Add(clear);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem inbox = new ToolStripMenuItem(Loc.T("Handle screenshots"));
            inbox.Checked = TakeOverScreenshots;
            inbox.ToolTipText = Loc.T("Screenshots hang instantly and skip the screenshots folder");
            inbox.Click += delegate
            {
                SetInbox(!TakeOverScreenshots);
                inbox.Checked = TakeOverScreenshots;      // the registry is the truth
            };
            m.Items.Add(inbox);

            ToolStripMenuItem clip = new ToolStripMenuItem(Loc.T("Hang copied images"));
            clip.Checked = ClipWatcher.IsEnabled;
            clip.ToolTipText = Loc.T("Images you copy hang on the line too");
            clip.Click += delegate
            {
                ClipWatcher.IsEnabled = !ClipWatcher.IsEnabled;
                if (ClipWatcher.IsEnabled || ClipWatcher.TextIsEnabled) _clip.Start();
                else _clip.Stop();
                UpdateTrayIcon();
                clip.Checked = ClipWatcher.IsEnabled;     // say what actually happened
            };
            m.Items.Add(clip);

            ToolStripMenuItem copiedText = new ToolStripMenuItem(Loc.T("Hang copied text"));
            copiedText.Checked = ClipWatcher.TextIsEnabled;
            copiedText.ToolTipText = Loc.T("Copy a passage and it appears as a note");
            copiedText.Click += delegate
            {
                ClipWatcher.TextIsEnabled = !ClipWatcher.TextIsEnabled;
                if (ClipWatcher.IsEnabled || ClipWatcher.TextIsEnabled) _clip.Start();
                else _clip.Stop();
                copiedText.Checked = ClipWatcher.TextIsEnabled;
            };
            m.Items.Add(copiedText);

            ToolStripMenuItem down = new ToolStripMenuItem(Loc.T("Bring the line down"));
            ToolStripMenuItem hover = new ToolStripMenuItem(Loc.T("When the pointer rests against the top of the screen"));
            hover.Checked = OpensFromTopEdge;
            hover.Click += delegate { OpensFromTopEdge = !OpensFromTopEdge; hover.Checked = OpensFromTopEdge; };
            // stays open like its parent: this is a settings sheet
            ToolStripDropDownMenu downMenu = down.DropDown as ToolStripDropDownMenu;
            if (downMenu != null) downMenu.AutoClose = false;
            down.DropDownItems.Add(hover);
            ToolStripMenuItem peek = new ToolStripMenuItem(Loc.T("When something new hangs"));
            peek.Checked = PeeksAtNew;
            peek.Click += delegate { PeeksAtNew = !PeeksAtNew; peek.Checked = PeeksAtNew; };
            down.DropDownItems.Add(peek);
            ToolStripMenuItem full = new ToolStripMenuItem(Loc.T("Over full screen apps"));
            full.Checked = OverFullScreen;
            full.Click += delegate { OverFullScreen = !OverFullScreen; Refresh(); full.Checked = OverFullScreen; };
            down.DropDownItems.Add(full);
            m.Items.Add(down);

            ToolStripMenuItem anim = new ToolStripMenuItem(Loc.T("Retract animation"));
            ToolStripDropDownMenu animMenu = anim.DropDown as ToolStripDropDownMenu;
            if (animMenu != null)
            {
                animMenu.ShowImageMargin = true;   // the tick needs its column
                animMenu.AutoClose = false;       // and it stays open like its parent
            }
            int[] speeds = new int[] { 0, 150, 320, 600, 1000 };
            foreach (int ms in speeds)
            {
                int captured = ms;
                ToolStripMenuItem it = new ToolStripMenuItem(RetractLabel(ms));
                it.Checked = RetractMs == ms;
                it.Click += delegate
                {
                    RetractMs = captured;
                    _form.RetractMs = captured;
                    _render = true;
                    // the tick moves without rebuilding anything
                    foreach (ToolStripItem sib in anim.DropDownItems)
                    {
                        ToolStripMenuItem s = sib as ToolStripMenuItem;
                        if (s != null) s.Checked = (captured == 0
                            ? s.Text == Loc.T("At once")
                            : s.Text == RetractLabel(captured));
                    }
                };
                anim.DropDownItems.Add(it);
            }
            m.Items.Add(anim);

            ToolStripMenuItem note = new ToolStripMenuItem(Loc.T("New note"));
            note.ToolTipText = Loc.T("Three notes at most");
            note.Click += delegate { NewText(); };
            m.Items.Add(note);

            ToolStripMenuItem folders = new ToolStripMenuItem(Loc.T("Also watch folders"));
            List<string> extra = ExtraFolders;
            foreach (string f in extra)
            {
                ToolStripMenuItem it = new ToolStripMenuItem(Path.GetFileName(f.TrimEnd('\\')));
                it.Checked = true;
                it.ToolTipText = f;
                string captured = f;
                it.Click += delegate { try { Process.Start("explorer.exe", captured); } catch { } };
                folders.DropDownItems.Add(it);
            }
            ToolStripDropDownMenu foldersMenu = folders.DropDown as ToolStripDropDownMenu;
            if (foldersMenu != null) foldersMenu.AutoClose = false;
            if (extra.Count > 0) folders.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem add = new ToolStripMenuItem(Loc.T("Add Folder…"));
            add.Enabled = extra.Count < MaxExtraFolders;
            add.Click += delegate { ChooseExtraFolder(); };
            folders.DropDownItems.Add(add);

            if (extra.Count > 0)
            {
                ToolStripMenuItem stop = new ToolStripMenuItem(Loc.T("Stop Watching"));
                foreach (string f in extra)
                {
                    string captured = f;
                    ToolStripMenuItem it = new ToolStripMenuItem(Path.GetFileName(captured.TrimEnd('\\')));
                    it.ToolTipText = captured;
                    it.Click += delegate
                    {
                        List<string> list = ExtraFolders;
                        list.RemoveAll(delegate (string x)
                        {
                            return string.Equals(x.TrimEnd('\\'), captured.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        });
                        ExtraFolders = list;
                        StartWatchers();
                    };
                    stop.DropDownItems.Add(it);
                }
                folders.DropDownItems.Add(stop);
            }
            m.Items.Add(folders);

            ToolStripMenuItem keep = new ToolStripMenuItem(Loc.T("Keep on line"));
            ToolStripMenuItem fit = new ToolStripMenuItem(Loc.T("As many as fit"));
            fit.Checked = !Line.PinnedLength;
            fit.Click += delegate { SetKeep(0); };
            keep.DropDownItems.Add(fit);
            foreach (int n in Line.KeepChoices)
            {
                int captured = n;
                ToolStripMenuItem it = new ToolStripMenuItem(Loc.T("%d photos", captured));
                it.Checked = Line.PinnedLength && Line.PinnedCount == captured;
                it.Click += delegate { SetKeep(captured); };
                keep.DropDownItems.Add(it);
            }
            m.Items.Add(keep);

            ToolStripMenuItem size = new ToolStripMenuItem(Loc.T("Size"));
            AddSize(size, LineSize.Small, Loc.T("Small"));
            AddSize(size, LineSize.Medium, Loc.T("Medium"));
            AddSize(size, LineSize.Large, Loc.T("Large"));
            m.Items.Add(size);

            ToolStripMenuItem shortcut = new ToolStripMenuItem(Loc.T("Shortcut"));
            ToolStripMenuItem cur = new ToolStripMenuItem(sc == null ? Loc.T("None") : sc.Display());
            cur.Enabled = false;
            shortcut.DropDownItems.Add(cur);
            ToolStripMenuItem change = new ToolStripMenuItem(Loc.T("Change Shortcut…"));
            change.Click += delegate { ChangeShortcut(); };
            shortcut.DropDownItems.Add(change);
            m.Items.Add(shortcut);

            ToolStripMenuItem openFolder = new ToolStripMenuItem(Loc.T("Open screenshots folder"));
            openFolder.Click += delegate
            {
                try { Process.Start("explorer.exe", TakeOverScreenshots ? Folders.Inbox : Folders.Screenshots); }
                catch { }
            };
            m.Items.Add(openFolder);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem sound = new ToolStripMenuItem(Loc.T("Sounds"));
            sound.Checked = _form.Model.SoundOn;
            sound.Click += delegate { _form.Model.SoundOn = !_form.Model.SoundOn; };
            m.Items.Add(sound);

            ToolStripMenuItem login = new ToolStripMenuItem(Loc.T("Start with Windows"));
            login.Checked = LaunchAtLogin;
            login.Click += delegate
            {
                LaunchAtLogin = !LaunchAtLogin;
                // The registry is the truth, so the tick says what actually
                // happened -- never what was merely intended.
                login.Checked = LaunchAtLogin;
            };
            m.Items.Add(login);

            ToolStripMenuItem about = new ToolStripMenuItem(Loc.T("About LineDry"));
            about.Click += delegate { ShowAbout(); };
            m.Items.Add(about);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quit = new ToolStripMenuItem(Loc.T("Exit LineDry"));
            quit.Click += delegate { Quit(); };
            m.Items.Add(quit);

            // One unfolded submenu at a time. With AutoClose off the shell would
            // happily leave several of them lying open at once, which is the
            // mess the owner saw in the corner of the screen.
            foreach (ToolStripItem item in m.Items)
            {
                ToolStripDropDownItem opened = item as ToolStripDropDownItem;
                if (opened == null || !opened.HasDropDownItems) continue;
                ToolStripDropDownItem mine = opened;
                mine.DropDown.Opened += delegate
                {
                    foreach (ToolStripItem other in m.Items)
                    {
                        ToolStripDropDownItem sibling = other as ToolStripDropDownItem;
                        if (sibling == null || ReferenceEquals(sibling, mine) || !sibling.HasDropDownItems) continue;
                        if (sibling.DropDown.Visible) sibling.DropDown.Close();
                    }
                };
            }
            return m;
        }

        private void AddSize(ToolStripMenuItem parent, LineSize size, string title)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(title);
            it.Checked = Geo.CurrentSize == size;
            it.Click += delegate
            {
                if (Geo.CurrentSize == size) return;
                Geo.SetSize(size);
                _form.PlaceOn(_form.Target);
                _form.UpdateCapacity();
                _form.Model.ReloadThumbnails();
                _render = true;
            };
            parent.DropDownItems.Add(it);
        }

        private static ToolStripMenuItem Add(ContextMenuStrip m, string text, EventHandler handler)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Click += handler;
            m.Items.Add(it);
            return it;
        }

        private void SetKeep(int n)
        {
            if ((n == 0 && !Line.PinnedLength) || (n > 0 && Line.PinnedCount == n)) return;
            Line.SetKeep(n);
            _form.UpdateCapacity();
            _form.Model.Trim();
            _render = true;
        }

        private void ChooseExtraFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Loc.T("New images saved to this folder will hang on the line.");
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                List<string> list = ExtraFolders;
                if (list.Count >= MaxExtraFolders) return;
                foreach (string x in list)
                    if (string.Equals(x.TrimEnd('\\'), dlg.SelectedPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                list.Add(dlg.SelectedPath);
                ExtraFolders = list;
                StartWatchers();
            }
        }

        // ------------------------------------------------------------ launch at login

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "LineDry";

        public static bool LaunchAtLogin
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue(RunName) != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (k == null) return;
                        if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                        else k.DeleteValue(RunName, false);
                    }
                }
                catch { Sound.Beep(); }
            }
        }

        // ------------------------------------------------------------------ hot key

        private void RegisterShortcut()
        {
            UnregisterShortcut();
            Shortcut sc = Shortcut.Current;
            if (sc == null) return;
            _hotKey = HotKeyId.Register(_form.Handle, sc.Modifiers | 0x4000, sc.Key, delegate { Toggle(); });
        }

        private void UnregisterShortcut()
        {
            if (_hotKey != null) { _hotKey.Dispose(); _hotKey = null; }
        }

        private void ChangeShortcut()
        {
            UnregisterShortcut();
            Shortcut previous = Shortcut.Current;
            bool cancelled;
            Shortcut result = RecorderWindow.Ask(out cancelled);
            if (cancelled) { RegisterShortcut(); return; }
            Shortcut.Save(result);
            RegisterShortcut();
            if (result != null && _hotKey == null)
            {
                // Taken by another app: keep the one that worked.
                Sound.Beep();
                Shortcut.Save(previous);
                RegisterShortcut();
            }
        }

        private void ShowAbout()
        {
            string text =
                "LineDry 1.0 — 晾衣绳\r\n\r\n" +
                "Windows port of Tendedero (https://github.com/alejandrobujan/tendedero),\r\n" +
                "MIT-licensed Swift/AppKit app by Alejandro Buján.\r\n\r\n" +
                "Screenshots you take hang on a line at the top of the screen.\r\n" +
                "Rest the pointer against the top edge to bring it down.";
            MessageBox.Show(text, "LineDry", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ----------------------------------------------------------------- notes

        /// A passage just copied hangs on the line like anything else: what goes
        /// up is a card whose picture is those words on a sheet of note paper.
        private void ShowText(string words)
        {
            words = (words ?? "").Trim();
            if (words.Length == 0) return;
            Screen sc = Screen.FromPoint(Cursor.Position);
            if (sc != _form.Target) _form.PlaceOn(sc);
            _form.UpdateCapacity();
            _wanted = true;
            _keepOpen = _keepOpen || _form.Model.LiveCount == 0;
            Refresh();

            Guid id = _form.Model.HangText(words, DateTime.Now);
            if (id == Guid.Empty)
            {
                _keepOpen = _form.Model.LiveCount > 0 && _keepOpen;
                ItemsChanged();
                _render = true;
                return;
            }
            if (PeeksAtNew) Reveal(false, 2.5);
            _render = true;
        }

        /// Typing needs a window that can hold the keyboard, so writing happens
        /// in an ordinary dialog, opened deliberately and closed again.
        private void NewText()
        {
            string typed;
            using (NoteEditDialog dlg = new NoteEditDialog(""))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                typed = dlg.Value;
            }
            if (typed.Trim().Length == 0) return;
            ShowText(typed);
        }

        /// The words on a text card can be changed; the sheet is painted again
        /// from them and the card repaints itself.
        private void EditText(Guid id)
        {
            Pegged p = Item(id);
            if (p == null || !p.IsText) return;
            string typed;
            using (NoteEditDialog dlg = new NoteEditDialog(p.Words))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                typed = dlg.Value;
            }
            typed = typed.Trim();
            if (typed.Length == 0) return;

            p.Words = typed;
            p.Stamp = DateTime.Now;
            Bitmap sheet = Notes.Sheet(typed, p.Stamp);
            if (sheet == null) return;
            if (p.Thumb != null) p.Thumb.Dispose();
            p.Thumb = sheet;                 // a new picture, so the card is rebuilt
            _form.Model.Remember();
            _render = true;
        }
        private void Quit()
        {
            if (TakeOverScreenshots)
            {
                string saved = Settings.GetString("inboxSavedFolder", "");
                Folders.SetScreenshotsFolder(saved.Length == 0 ? null : saved);
            }
            if (_loop != null) _loop.Stop();
            UnregisterShortcut();

            if (_form != null) _form.Dispose();
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ shortcut

    internal sealed class Shortcut
    {
        public uint Modifiers;
        public uint Key;
        public string KeyText = "";

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public static readonly Shortcut Standard = Make(MOD_CONTROL | MOD_ALT, (uint)Keys.T, "T");

        public static Shortcut Make(uint mods, uint key, string text)
        {
            Shortcut s = new Shortcut();
            s.Modifiers = mods;
            s.Key = key;
            s.KeyText = text;
            return s;
        }

        private const string StoreKey = "shortcut";

        /// The user's choice. Null means no shortcut at all.
        public static Shortcut Current
        {
            get
            {
                if (!Settings.GetBool("shortcutSet", false)) return Standard;
                uint mods = (uint)Settings.GetInt("shortcutMods", 0);
                uint key = (uint)Settings.GetInt("shortcutKey", 0);
                string text = Settings.GetString("shortcutText", "");
                if (key == 0) return null;
                return Make(mods, key, text);
            }
        }

        public static void Save(Shortcut s)
        {
            if (s == null)
            {
                Settings.SetBool("shortcutSet", true);
                Settings.SetInt("shortcutMods", 0);
                Settings.SetInt("shortcutKey", 0);
                Settings.SetString("shortcutText", "");
                return;
            }
            Settings.SetBool("shortcutSet", true);
            Settings.SetInt("shortcutMods", (int)s.Modifiers);
            Settings.SetInt("shortcutKey", (int)s.Key);
            Settings.SetString("shortcutText", s.KeyText);
        }

        /// As Windows writes it in menus: Ctrl+Alt+T.
        public string Display()
        {
            string t = "";
            if ((Modifiers & MOD_CONTROL) != 0) t += "Ctrl+";
            if ((Modifiers & MOD_ALT) != 0) t += "Alt+";
            if ((Modifiers & MOD_SHIFT) != 0) t += "Shift+";
            if ((Modifiers & MOD_WIN) != 0) t += "Win+";
            return t + (KeyText.Length == 0 ? "?" : KeyText.ToUpperInvariant());
        }
    }

    internal sealed class HotKeyId : IDisposable
    {
        private IntPtr _hwnd;
        private int _id;
        private static int _next = 0xA100;
        private static readonly Dictionary<int, Action> Handlers = new Dictionary<int, Action>();
        private static NativeWindow _sink;

        private HotKeyId(IntPtr hwnd, int id) { _hwnd = hwnd; _id = id; }

        public static HotKeyId Register(IntPtr hwnd, uint modifiers, uint key, Action action)
        {
            EnsureSink(hwnd);
            int id = ++_next;
            if (!Native.RegisterHotKey(hwnd, id, modifiers, key)) return null;
            Handlers[id] = action;
            return new HotKeyId(hwnd, id);
        }

        /// One window procedure for the whole app, however often the shortcut
        /// changes -- the same shape as the original's single Carbon handler.
        private static void EnsureSink(IntPtr hwnd)
        {
            if (_sink != null) return;
            _sink = new Sink();
            _sink.AssignHandle(hwnd);
        }

        private sealed class Sink : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                const int WM_HOTKEY = 0x0312;
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    Action a;
                    if (Handlers.TryGetValue(id, out a) && a != null) a();
                }
                base.WndProc(ref m);
            }
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                Native.UnregisterHotKey(_hwnd, _id);
                Handlers.Remove(_id);
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
