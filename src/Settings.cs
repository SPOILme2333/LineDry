// Settings store. The original uses UserDefaults; the Windows port keeps the
// same key names in HKCU\Software\LineDry so behaviour carries over unchanged.
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace LineDry
{
    internal static class Settings
    {
        private const string Root = @"Software\LineDry";

        private static RegistryKey Open(bool writable)
        {
            return Registry.CurrentUser.CreateSubKey(Root);
        }

        public static string GetString(string key, string fallback)
        {
            using (RegistryKey k = Open(false))
            {
                object v = k == null ? null : k.GetValue(key);
                if (v == null) return fallback;
                return v.ToString();
            }
        }

        public static void SetString(string key, string value)
        {
            using (RegistryKey k = Open(true))
            {
                if (k == null) return;
                k.SetValue(key, value, RegistryValueKind.String);
            }
        }

        public static bool GetBool(string key, bool fallback)
        {
            using (RegistryKey k = Open(false))
            {
                object v = k == null ? null : k.GetValue(key);
                if (v == null) return fallback;
                if (v is int) return ((int)v) != 0;
                return v.ToString() == "1" || v.ToString().ToLowerInvariant() == "true";
            }
        }

        public static void SetBool(string key, bool value)
        {
            using (RegistryKey k = Open(true))
            {
                if (k == null) return;
                k.SetValue(key, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        public static int GetInt(string key, int fallback)
        {
            using (RegistryKey k = Open(false))
            {
                object v = k == null ? null : k.GetValue(key);
                if (v == null) return fallback;
                int n;
                if (v is int) return (int)v;
                return int.TryParse(v.ToString(), out n) ? n : fallback;
            }
        }

        public static void SetInt(string key, int value)
        {
            using (RegistryKey k = Open(true))
            {
                if (k == null) return;
                k.SetValue(key, value, RegistryValueKind.DWord);
            }
        }

        public static string[] GetStrings(string key)
        {
            using (RegistryKey k = Open(false))
            {
                if (k == null) return new string[0];
                string[] v = k.GetValue(key) as string[];
                return v ?? new string[0];
            }
        }

        public static void SetStrings(string key, string[] value)
        {
            using (RegistryKey k = Open(true))
            {
                if (k == null) return;
                if (value == null || value.Length == 0) k.DeleteValue(key, false);
                else k.SetValue(key, value, RegistryValueKind.MultiString);
            }
        }

        public static void Remove(string key)
        {
            using (RegistryKey k = Open(true))
            {
                if (k != null) k.DeleteValue(key, false);
            }
        }
    }
}
