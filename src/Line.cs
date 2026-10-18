// The line itself: what hangs on it and what you can do with each item.
// A direct port of Sources/Tendedero/Line.swift -- the files never move,
// the line is only a view onto them.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LineDry
{
    internal sealed class Pegged
    {
        public Guid Id = Guid.NewGuid();
        public string Path;
        public Bitmap Thumb;

        /// Words instead of a picture: a card whose "photo" is a sheet of note
        /// paper. Tilt, swing, clip, frame, hover and take-down all treat it
        /// like any other card -- because that is exactly what it is.
        public string Words;
        public DateTime Stamp;
        public bool IsText { get { return !string.IsNullOrEmpty(Words); } }

        /// The card drawn once -- shadow, glass, framed photo, hairline --
        /// reused on every frame. FaceFor is the Thumb it was built from, so a
        /// replaced picture rebuilds it without touching any call site.
        public Bitmap Face;
        public object FaceFor;
        public double FacePad;
        /// Every photo hangs a little crooked, like on a real line.
        public double Tilt;

        /// Where the card is drawn across the line, eased: when one is taken
        /// down the rest slide into its place instead of teleporting sideways.
        public double SlotX;
        public bool SlotKnown;
        public bool Falling;
        /// Still flying in from where it was captured; the card waits hidden.
        public bool Flying;
        public bool IsRecording;

        // ---- animation state, advanced by Line.Tick(dt) ----

        /// Degrees away from the resting tilt: the swing on the line.
        public double Swing;
        public double SwingTarget;
        public double SwingVelocity;
        /// Wall-clock second at which the current nudge lets go.
        public double SwingReleaseAt;
        /// How far the card still floats above the rope, in pixels.
        public double Drop = 46;
        public double DropVelocity;
        public double BornAt;
        /// Eased 0..1 values the renderer reads.
        public double Hover;
        public double Press;
        public bool Hovering;
        public bool IsPressed;
        public double CopiedAt = -99;
        public double FallingAt;

        public bool Live { get { return !Falling; } }
    }

    internal sealed class Line
    {
        public readonly List<Pegged> Items = new List<Pegged>();
        public event Action Changed;

        /// Seconds since the app started; the clock every animation runs on.
        public double Now;

        public Guid CopiedId = Guid.Empty;
        public Guid DraggingId = Guid.Empty;
        public Guid PressedId = Guid.Empty;
        public bool Revealed;
        public int Gust;
        /// Bumped every time a gust fires, so the cards can sway.
        public double NextGustAt;

        public int MaxItems = 8;
        public int VisibleCount = 8;
        public double Scroll;

        public static readonly int[] KeepChoices = new int[] { 25, 50, 100 };

        // ------------------------------------------------------------ capacity

        public static bool PinnedLength
        {
            get
            {
                int n = Settings.GetInt("keepOnLine", 0);
                return Array.IndexOf(KeepChoices, n) >= 0;
            }
        }

        public static int PinnedCount { get { return Settings.GetInt("keepOnLine", 0); } }

        public static void SetKeep(int n)
        {
            Settings.SetInt("keepOnLine", n);
        }

        public bool SoundOn
        {
            get { return !Settings.GetBool("soundOff", false); }
            set { Settings.SetBool("soundOff", !value); }
        }

        public int LiveCount
        {
            get
            {
                int n = 0;
                foreach (Pegged p in Items) if (!p.Falling) n++;
                return n;
            }
        }

        public Line()
        {
            Restore();
            ScheduleGust();
        }

        private void Fire()
        {
            Action h = Changed;
            if (h != null) h();
        }

        // -------------------------------------------------- hanging and dropping

        public Guid Hang(string path, bool quietly, bool flying)
        {
            foreach (Pegged p in Items)
                if (!p.Falling && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) return Guid.Empty;

            Bitmap thumb = Thumbs.Make(path, Thumbs.DefaultMaxPixels);
            if (thumb == null) return Guid.Empty;

            Pegged item = new Pegged();
            item.Path = path;
            item.Thumb = thumb;
            item.IsRecording = Thumbs.IsRecording(path);
            item.Tilt = (Rng.NextDouble() * 5.0) - 2.5;
            item.Flying = flying;
            item.BornAt = Now;
            Items.Add(item);
            Scroll = 0;

            // A full line lets the oldest photo fall off the far end. Only one.
            if (LiveCount > MaxItems)
            {
                foreach (Pegged p in Items)
                    if (!p.Falling) { LetGo(p.Id); break; }
            }
            Save();
            if (!quietly) Sound.Play(Sound.Kind.Hang);
            Fire();
            return item.Id;
        }

        /// Three places for writing on the line at once.
        public const int MaxTexts = 3;

        /// Words hang on the line like anything else: the card's picture is the
        /// words on a sheet of note paper. A fourth passage lets the oldest one
        /// go, and a full line still lets the oldest photo fall off the end.
        public Guid HangText(string words, DateTime stamp)
        {
            words = (words ?? "").Trim();
            if (words.Length == 0) return Guid.Empty;
            foreach (Pegged p in Items)
                if (!p.Falling && p.Words == words) return Guid.Empty;   // already up

            Bitmap sheet = Notes.Sheet(words, stamp);
            if (sheet == null) return Guid.Empty;

            Pegged item = new Pegged();
            item.Path = "";                  // it is not a file; nothing may assume one
            item.Words = words;
            item.Stamp = stamp;
            item.Thumb = sheet;
            item.Tilt = (Rng.NextDouble() * 5.0) - 2.5;
            item.BornAt = Now;
            Items.Add(item);
            Scroll = 0;

            int texts = 0;
            foreach (Pegged p in Items) if (!p.Falling && p.IsText) texts++;
            if (texts > MaxTexts)
                foreach (Pegged p in Items)
                    if (!p.Falling && p.IsText) { LetGo(p.Id); break; }

            if (LiveCount > MaxItems)
                foreach (Pegged p in Items)
                    if (!p.Falling && !p.IsText) { LetGo(p.Id); break; }

            Save();
            Sound.Play(Sound.Kind.Hang);
            Fire();
            return item.Id;
        }

        /// The capture has reached the line: the real card takes over.
        public void Land(Guid id)
        {
            Pegged p = Find(id);
            if (p == null) return;
            p.Flying = false;
            p.Drop = 0;
            p.DropVelocity = 0;
            Nudge(p, 2.2);
        }

        public Pegged Find(Guid id)
        {
            foreach (Pegged p in Items) if (p.Id == id) return p;
            return null;
        }

        public void Drop(Guid id, bool quietly)
        {
            Pegged p = Find(id);
            if (p == null || p.Falling) return;
            p.Falling = true;
            p.FallingAt = Now;
            if (!quietly) Sound.Play(Sound.Kind.Drop);
            Save();
            Fire();
        }

        public void Clear()
        {
            TakeDownAll(Filter(Items, false));
        }

        /// The photos hung before (left) or after (right) this one.
        public List<Pegged> Neighbours(Guid id, bool toTheLeft)
        {
            List<Pegged> live = Filter(Items, false);
            int i = IndexOf(live, id);
            if (i < 0) return new List<Pegged>();
            return toTheLeft ? live.GetRange(0, i) : live.GetRange(i + 1, live.Count - i - 1);
        }

        public void TakeDownSide(Guid id, bool toTheLeft)
        {
            TakeDownAll(Neighbours(id, toTheLeft));
        }

        /// One after another, and only the first makes a sound.
        private void TakeDownAll(List<Pegged> live)
        {
            for (int n = 0; n < live.Count; n++)
            {
                Pegged p = live[n];
                if (n == 0) Discard(p.Id, false);
                else Pending.Add(new PendingDrop(p.Id, Now + 0.06 * n));
            }
        }

        private struct PendingDrop { public Guid Id; public double At; public PendingDrop(Guid id, double at) { Id = id; At = at; } }
        private readonly List<PendingDrop> Pending = new List<PendingDrop>();

        /// Photos whose file was deleted or moved away fall off by themselves.
        public void Prune()
        {
            bool any = false;
            foreach (Pegged p in Items)
            {
                if (p.Falling || p.IsText) continue;
                if (!File.Exists(p.Path)) { Drop(p.Id, true); any = true; }
            }
            if (any) Fire();
        }

        // ------------------------------------------------------ one photo actions

        public void Copy(Guid id)
        {
            Pegged p = Find(id);
            if (p == null) return;
            if (p.IsText)
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(p.Words);
                    ClipWatcher.SkipOwnCopy();
                    CopiedId = id;
                    p.CopiedAt = Now;
                }
                catch { }
                Fire();
                return;
            }
            try
            {
                System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
                files.Add(p.Path);
                System.Windows.Forms.DataObject data = new System.Windows.Forms.DataObject();
                data.SetFileDropList(files);
                if (!p.IsRecording)
                {
                    using (Bitmap b = Thumbs.LoadPlain(p.Path))
                        if (b != null) data.SetImage(b);
                }
                System.Windows.Forms.Clipboard.SetDataObject(data, true);
            ClipWatcher.SkipOwnCopy();   // our own copy is not something the user copied
                CopiedId = id;
                p.CopiedAt = Now;
            }
            catch { }
            Fire();
        }

        public bool Trash(Guid id, bool quietly)
        {
            Pegged p = Find(id);
            if (p == null) return false;
            if (p.IsText) { Drop(id, quietly); return true; }   // no file to recycle
            if (Recycle.Send(p.Path, quietly))
            {
                if (SoundOn && !quietly) Sound.Play(Sound.Kind.Trash);
                Drop(id, true);
                return true;
            }
            if (!quietly) Sound.Beep();
            return false;
        }

        /// Whether the file lives in the app's own folder. Those are discarded
        /// to the Recycle Bin, or the folder would fill up with forgotten
        /// screenshots. Files anywhere else stay where they are.
        public bool IsInInbox(Guid id)
        {
            Pegged p = Find(id);
            if (p == null || string.IsNullOrEmpty(p.Path)) return false;
            string inbox = Folders.Inbox.TrimEnd('\\') + "\\";
            return p.Path.StartsWith(inbox, StringComparison.OrdinalIgnoreCase);
        }

        /// The corner cross and "Take everything down" both end up here.
        public void Discard(Guid id, bool quietly)
        {
            if (IsInInbox(id)) Trash(id, quietly); else Drop(id, quietly);
        }

        private void LetGo(Guid id)
        {
            if (IsInInbox(id) && Trash(id, true)) return;
            Drop(id, true);
        }

        /// In inbox mode, keep a screenshot by moving it to the Desktop.
        public void SaveToDesktop(Guid id)
        {
            Pegged p = Find(id);
            if (p == null) return;
            try
            {
                string target = UniquePath(Folders.Desktop, System.IO.Path.GetFileName(p.Path));
                File.Move(p.Path, target);
                Drop(id, true);
            }
            catch { Sound.Beep(); }
        }

        /// Words have no file behind them, so one is written the moment an action
        /// asks for it -- opening, revealing, editing. Nothing is written until
        /// then, and it goes to the app's own corner of %TEMP%.
        public string TextFileFor(Guid id)
        {
            Pegged p = Find(id);
            if (p == null || !p.IsText) return null;
            try
            {
                Directory.CreateDirectory(TempFolder());
                string path = Path.Combine(TempFolder(), SheetName(p) + ".txt");
                File.WriteAllText(path, p.Words, new System.Text.UTF8Encoding(true));   // BOM: Notepad reads Chinese right
                return path;
            }
            catch { return null; }
        }

        /// The sheet itself as an image, for the preview window.
        public string SheetImageFor(Guid id)
        {
            Pegged p = Find(id);
            if (p == null || !p.IsText || p.Thumb == null) return null;
            try
            {
                Directory.CreateDirectory(TempFolder());
                string path = Path.Combine(TempFolder(), SheetName(p) + ".png");
                p.Thumb.Save(path, ImageFormat.Png);
                return path;
            }
            catch { return null; }
        }

        /// The words beside the Desktop as a text file; the sheet then leaves.
        public bool SaveTextToDesktop(Guid id)
        {
            Pegged p = Find(id);
            if (p == null || !p.IsText) return false;
            try
            {
                string target = UniquePath(Folders.Desktop, SheetName(p) + ".txt");
                File.WriteAllText(target, p.Words, new System.Text.UTF8Encoding(true));
                Drop(id, true);
                return true;
            }
            catch { return false; }
        }

        /// What the editor left in the file: the sheet is painted again from it,
        /// the way a photo's card is rebuilt after Paint saves.
        public bool ReadBackText(Guid id, string file)
        {
            Pegged p = Find(id);
            if (p == null || !p.IsText) return false;
            try
            {
                string words = File.ReadAllText(file).Trim();
                if (words.Length == 0 || words == p.Words) return false;
                Bitmap sheet = Notes.Sheet(words, DateTime.Now);
                if (sheet == null) return false;
                p.Words = words;
                p.Stamp = DateTime.Now;
                if (p.Thumb != null) p.Thumb.Dispose();
                p.Thumb = sheet;
                Save();
                Fire();
                return true;
            }
            catch { return false; }
        }

        private static string TempFolder()
        {
            return Path.Combine(Path.GetTempPath(), "LineDry");
        }

        /// A file name taken from the first line of the words.
        private static string SheetName(Pegged p)
        {
            string first = p.Words.Replace("\r", "").Split('\n')[0].Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) first = first.Replace(c, ' ');
            if (first.Length > 24) first = first.Substring(0, 24).Trim();
            if (first.Length == 0) first = "note";
            return first + " " + p.Stamp.ToString("yyyy-MM-dd HH.mm.ss");
        }

        private static string UniquePath(string folder, string name)
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(name);
            string ext = System.IO.Path.GetExtension(name);
            string candidate = System.IO.Path.Combine(folder, name);
            int n = 2;
            while (File.Exists(candidate))
                candidate = System.IO.Path.Combine(folder, baseName + " " + n++ + ext);
            return candidate;
        }

        public void ReloadThumbnail(string path)
        {
            foreach (Pegged p in Items)
            {
                if (p.Falling || !string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                Bitmap t = Thumbs.Make(path, Thumbs.DefaultMaxPixels);
                if (t != null) { p.Thumb = t; p.IsRecording = Thumbs.IsRecording(path); }
            }
            Fire();
        }

        public void ReloadThumbnails()
        {
            foreach (Pegged p in Items)
            {
                if (p.Falling) continue;
                Bitmap t = Thumbs.Make(p.Path, Thumbs.DefaultMaxPixels);
                if (t != null) p.Thumb = t;
            }
            Fire();
        }

        /// Moves along a line that holds more than fits, from a scroll gesture.
        public void ScrollBy(double delta)
        {
            double most = Math.Max(0, Items.Count - VisibleCount) * Geo.Spacing;
            double next = Math.Min(most, Math.Max(0, Scroll + delta));
            if (next != Scroll) { Scroll = next; Fire(); }
        }

        /// After "Keep on line" is lowered, the oldest photos past it go.
        public void Trim()
        {
            while (LiveCount > MaxItems)
            {
                bool done = false;
                foreach (Pegged p in Items)
                    if (!p.Falling) { LetGo(p.Id); done = true; break; }
                if (!done) break;
            }
            Scroll = 0;
        }

        // ------------------------------------------------------------------ breeze

        /// Every so often a little wind moves the line. It is the detail that
        /// makes it feel like an object and not a widget.
        private void ScheduleGust()
        {
            NextGustAt = Now + 7 + Rng.NextDouble() * 9;
        }

        /// A fresh capture comes down tens of degrees and settles.
        public void Arrive(Pegged p)
        {
            p.Swing = 16;
            p.SwingTarget = 0;
            p.SwingVelocity = 0;
            p.SwingReleaseAt = 0;
            p.Drop = (p.Drop > 0) ? p.Drop : 46;
            p.DropVelocity = 0;
        }

        public void Nudge(Pegged p, double degrees)
        {
            p.SwingTarget = degrees;
            p.SwingVelocity = 0;
            p.SwingReleaseAt = Now + 0.3;
        }

        // ------------------------------------------------------------------ ticks

        private readonly List<Guid> dead = new List<Guid>();

        public void Tick(double dt)
        {
            Now += dt;
            dead.Clear();
            bool animated = false;

            if (Now >= NextGustAt)
            {
                ScheduleGust();
                bool dragging = DraggingId != Guid.Empty;
                if (Items.Count > 0 && !dragging)
                {
                    Gust++;
                    foreach (Pegged p in Items)
                        if (!p.Falling && !p.Flying)
                            Nudge(p, 1.6 + Rng.NextDouble() * 1.8);
                }
            }

            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (Now >= Pending[i].At)
                {
                    Discard(Pending[i].Id, true);
                    Pending.RemoveAt(i);
                }
            }

            foreach (Pegged p in Items)
            {
                if (p.SwingReleaseAt > 0 && Now >= p.SwingReleaseAt)
                {
                    p.SwingTarget = 0;
                    p.SwingReleaseAt = 0;
                    p.SwingVelocity = 0;
                }

                // Under-damped spring, so a nudge rocks a couple of times
                // before it settles, the way a pegged photo does.
                double k = 34, c = 2.4;
                double a = -k * (p.Swing - p.SwingTarget) - c * p.SwingVelocity;
                p.SwingVelocity += a * dt * 3.0;
                p.Swing += p.SwingVelocity * dt * 3.0;
                if (Math.Abs(p.Swing - p.SwingTarget) > 0.01 || Math.Abs(p.SwingVelocity) > 0.01) animated = true;
                else { p.Swing = p.SwingTarget; p.SwingVelocity = 0; }

                double da = -k * (p.Drop - 0) - c * p.DropVelocity;
                p.DropVelocity += da * dt;
                p.Drop += p.DropVelocity * dt;
                if (Math.Abs(p.Drop) > 0.05) animated = true;
                else { p.Drop = 0; p.DropVelocity = 0; }

                double targetHover = p.Hovering ? 1 : 0;
                if (Math.Abs(p.Hover - targetHover) > 0.001)
                {
                    p.Hover += (targetHover - p.Hover) * Math.Min(1, dt * 11);
                    animated = true;
                }

                double targetPress = p.IsPressed ? 1 : 0;
                double rate = p.IsPressed ? 1.8 : 9;
                if (Math.Abs(p.Press - targetPress) > 0.001)
                {
                    p.Press += (targetPress - p.Press) * Math.Min(1, dt * rate);
                    animated = true;
                }

                if (p.CopiedAt > 0 && Now - p.CopiedAt > 1.2) { p.CopiedAt = -99; animated = true; }
                if (p.CopiedAt > 0) animated = true;

                if (p.Falling && Now - p.FallingAt > 0.62) dead.Add(p.Id);
            }

            foreach (Guid id in dead)
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                    if (Items[i].Id == id) Items.RemoveAt(i);
                animated = true;
            }
            if (dead.Count > 0) Save();

            if (animated) Fire();
        }

        public bool NeedsFrames
        {
            get
            {
                foreach (Pegged p in Items)
                {
                    if (p.Falling || p.Flying) return true;
                    if (Math.Abs(p.Swing - p.SwingTarget) > 0.01 || Math.Abs(p.SwingVelocity) > 0.01) return true;
                    if (Math.Abs(p.Drop) > 0.05) return true;
                }
                return false;
            }
        }

        // -------------------------------------------------------------- persistence

        private const string StoreKey = "pegged";
        private const string TextStoreKey = "peggedText";

        /// Keeping the line as it stands is asked for explicitly (editing a
        /// card's words); hanging and taking down already save.
        public void Remember() { Save(); Fire(); }

        private void Save()
        {
            List<string> paths = new List<string>();
            List<string> texts = new List<string>();
            foreach (Pegged p in Items)
            {
                if (p.Falling) continue;
                if (p.IsText) texts.Add(p.Words.Replace("\t", "    ") + "\t" + p.Stamp.ToBinary());
                else if (!string.IsNullOrEmpty(p.Path)) paths.Add(p.Path);
            }
            Settings.SetStrings(StoreKey, paths.ToArray());
            Settings.SetStrings(TextStoreKey, texts.ToArray());
        }

        /// Everything comes back as it was.
        private void Restore()
        {
            string[] paths = Settings.GetStrings(StoreKey);
            foreach (string path in paths)
            {
                if (!File.Exists(path)) continue;
                bool dup = false;
                foreach (Pegged e in Items)
                    if (string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (dup) continue;
                Bitmap thumb = Thumbs.Make(path, Thumbs.DefaultMaxPixels);
                if (thumb == null) continue;
                Pegged p = new Pegged();
                p.Path = path;
                p.Thumb = thumb;
                p.IsRecording = Thumbs.IsRecording(path);
                p.Tilt = (Rng.NextDouble() * 5.0) - 2.5;
                p.Drop = 0;
                Items.Add(p);
            }

            string[] texts = Settings.GetStrings(TextStoreKey);
            foreach (string entry in texts)
            {
                string[] parts = entry.Split('\t');
                if (parts.Length < 2) continue;
                long ticks;
                if (!long.TryParse(parts[1], out ticks)) ticks = DateTime.Now.Ticks;
                DateTime stamp = DateTime.FromBinary(ticks);
                Bitmap sheet = Notes.Sheet(parts[0], stamp);
                if (sheet == null) continue;
                Pegged t = new Pegged();
                t.Path = "";
                t.Words = parts[0];
                t.Stamp = stamp;
                t.Thumb = sheet;
                t.Tilt = (Rng.NextDouble() * 5.0) - 2.5;
                t.Drop = 0;
                Items.Add(t);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static List<Pegged> Filter(List<Pegged> src, bool falling)
        {
            List<Pegged> r = new List<Pegged>();
            foreach (Pegged p in src) if (p.Falling == falling) r.Add(p);
            return r;
        }

        private static int IndexOf(List<Pegged> list, Guid id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
            return -1;
        }
    }

    internal static class Rng
    {
        private static readonly Random R = new Random();
        public static double NextDouble() { lock (R) return R.NextDouble(); }
    }
}
