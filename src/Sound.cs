// The little sounds the line makes. macOS plays Tink on hang and Pop on
// take-down; Windows maps those onto its own system sounds.
using System;
using System.Runtime.InteropServices;

namespace LineDry
{
    internal static class Sound
    {
        public enum Kind { Hang, Drop, Trash }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_ALIAS = 0x00010000;

        public static void Play(Kind kind)
        {
            string alias;
            switch (kind)
            {
                case Kind.Hang: alias = "SystemAsterisk"; break;
                case Kind.Drop: alias = "SystemDefault"; break;
                default: alias = "SystemExclamation"; break;
            }
            try { PlaySound(alias, IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT); }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);

        public static void Beep()
        {
            try { MessageBeep(0x00000030); } catch { }
        }
    }
}
