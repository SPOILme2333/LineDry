// Watches the folder Windows saves screenshots to and reports new ones.
// The line never takes screenshots itself: you keep your usual shortcut
// (Win+PrtScn, the Snipping Tool, or anything else) and the line picks them up.
//
// Two Windows-specific lessons are baked in here:
//  1. FileSystemWatcher raises its events on a thread-pool thread, so the
//     work is marshalled back to the UI thread before touching any Timer.
//  2. A capture app may still be writing the file when we first see it, so a
//     candidate has to look settled before it is read. The periodic sweep
//     also covers the moments the watcher misses or drops.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace LineDry
{
    internal sealed class ShotWatcher : IDisposable
    {
        public string Folder;

        private FileSystemWatcher _fsw;
        private Timer _poll;
        private volatile bool _dirty;
        private int _ticks;
        private HashSet<string> _known;
        private readonly DateTime _launch = DateTime.Now;
        private readonly Action<string> _onNew;
        private readonly Action _onChange;
        private readonly bool _acceptRecordings;

        /// How long a file must have existed before it counts as finished.
        private const double SettleSeconds = 0.6;
        /// The sweep interval, and how many of them pass before a check
        /// happens even with no event from the watcher.
        private const int PollMs = 400;
        private const int SweepEvery = 8;

        public ShotWatcher(string folder, Action<string> onNew, Action onChange)
        {
            Folder = folder;
            _onNew = onNew;
            _onChange = onChange;
            _acceptRecordings = string.Equals(
                folder.TrimEnd('\\'), Folders.Inbox.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public void Start()
        {
            Stop();
            try
            {
                Directory.CreateDirectory(Folder);
                _fsw = new FileSystemWatcher(Folder);
                _fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                                  | NotifyFilters.CreationTime | NotifyFilters.Size;
                FileSystemEventHandler touched = delegate { _dirty = true; };
                _fsw.Created += touched;
                _fsw.Changed += touched;
                _fsw.Deleted += touched;
                _fsw.Renamed += delegate { _dirty = true; };
                _fsw.Error += delegate { _dirty = true; };
                _fsw.IncludeSubdirectories = false;
                _fsw.EnableRaisingEvents = true;
            }
            catch { }

            _poll = new Timer();
            _poll.Interval = PollMs;
            _poll.Tick += delegate
            {
                _ticks++;
                if (_dirty || _ticks % SweepEvery == 0)
                {
                    _dirty = false;
                    Scan();
                }
            };
            _poll.Start();
            Scan();
        }

        public void Stop()
        {
            if (_fsw != null)
            {
                try { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } catch { }
                _fsw = null;
            }
            if (_poll != null) { _poll.Stop(); _poll.Dispose(); _poll = null; }
        }

        private static readonly string[] Ext =
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
        };

        private static readonly string[] VideoExt = { ".mov", ".mp4", ".m4v" };

        private bool IsCandidate(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(VideoExt, e) >= 0) return _acceptRecordings;
            return Array.IndexOf(Ext, e) >= 0;
        }

        private static DateTime Created(string path)
        {
            try { return File.GetCreationTime(path); }
            catch { return DateTime.MinValue; }
        }

        private void Scan()
        {
            List<string> files = new List<string>();
            DateTime now = DateTime.Now;
            try
            {
                foreach (string f in Directory.GetFiles(Folder))
                {
                    if (Path.GetFileName(f).StartsWith(".")) continue;
                    files.Add(f);
                }
            }
            catch { return; }
            files.Sort(delegate (string a, string b) { return Created(a).CompareTo(Created(b)); });

            bool first = _known == null;
            HashSet<string> baseSet = _known;
            if (baseSet == null)
            {
                // Baseline is "everything that was already there when we
                // launched", except captures from the last few minutes: a
                // screenshot taken right before a restart should still hang.
                baseSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                    if (Created(f) < _launch.AddMinutes(-5)) baseSet.Add(f);
            }

            List<string> fresh = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                seen.Add(f);
                if (baseSet.Contains(f) || !IsCandidate(f)) continue;
                // Still being written: leave it for the next sweep rather than
                // marking it known and losing it for good -- so it must NOT
                // land in `seen` either, which is what _known becomes.
                if ((now - Created(f)).TotalSeconds < SettleSeconds)
                {
                    seen.Remove(f);
                    continue;
                }
                fresh.Add(f);
            }
            _known = seen;

            foreach (string f in fresh) _onNew(f);
            if (!first) _onChange();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
