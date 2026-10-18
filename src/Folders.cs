// Where Windows keeps the things the line cares about.
using System;
using System.IO;
using Microsoft.Win32;

namespace LineDry
{
    internal static class Folders
    {
        /// The app's own folder: screenshots taken over and clipboard images.
        public static string Inbox
        {
            get
            {
                string b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LineDry");
                return Path.Combine(b, "Screenshots");
            }
        }

        public static string Pictures
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); }
        }

        public static string Desktop
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }

        /// Where Win+PrtScn, the Snipping Tool and Win+Shift+S(autosave) all
        /// drop their files: the known Shell folder {B7BEDE81-...}, which the
        /// user may have moved, with the conventional path as the fallback.
        public static string Screenshots
        {
            get
            {
                string known = KnownScreenshotsFolder();
                if (known != null && Directory.Exists(known)) return known;
                string guess = Path.Combine(Pictures, "Screenshots");
                if (Directory.Exists(guess)) return guess;
                return known ?? guess;
            }
        }

        private const string ScreenshotGuid = "{B7BEDE81-DF94-4682-A7D8-57A71785D4C3}";
        private const string ShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        private const string ShellFoldersExpanded = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

        private static string KnownScreenshotsFolder()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ShellFoldersExpanded))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(ScreenshotGuid);
                        if (v != null && Directory.Exists(v.ToString())) return v.ToString();
                    }
                }
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ShellFolders))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(ScreenshotGuid);
                        if (v != null)
                        {
                            string p = Environment.ExpandEnvironmentVariables(v.ToString());
                            return p;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// The value Windows ships with, so taking over can be undone exactly.
        public static string RawScreenshotsFolderValue()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ShellFolders))
                {
                    if (k == null) return null;
                    object v = k.GetValue(ScreenshotGuid);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }

        public static bool SetScreenshotsFolder(string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ShellFolders, true))
                {
                    if (k == null) return false;
                    if (value == null) k.DeleteValue(ScreenshotGuid, false);
                    else k.SetValue(ScreenshotGuid, value, RegistryValueKind.ExpandString);
                    return true;
                }
            }
            catch { return false; }
        }

        public static void EnsureInbox()
        {
            try { Directory.CreateDirectory(Inbox); } catch { }
        }
    }
}
