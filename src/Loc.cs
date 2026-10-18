// Localisation. Reads the very same .strings files the macOS app ships
// (one folder per language, keyed by the English text), so every translation
// the original already has is reused verbatim.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace LineDry
{
    internal static class Loc
    {
        private static Dictionary<string, string> _table;
        private static string _code = "en";

        public static string Code { get { return _code; } }

        /// Language folders the original ships.
        private static readonly string[] Known =
            { "en", "es", "gl", "fr", "pt-BR", "ja", "ko", "zh-Hans", "tr", "az", "hi" };

        public static void Load()
        {
            _table = new Dictionary<string, string>();
            string want = PickLanguage();
            _code = want;
            Parse(want, _table);
            if (want != "en")
            {
                Dictionary<string, string> en = new Dictionary<string, string>();
                Parse("en", en);
                foreach (KeyValuePair<string, string> kv in en)
                    if (!_table.ContainsKey(kv.Key)) _table[kv.Key] = kv.Value;
            }
        }

        private static string PickLanguage()
        {
            string forced = Settings.GetString("language", "");
            if (forced.Length > 0 && Array.IndexOf(Known, forced) >= 0) return forced;

            CultureInfo c = CultureInfo.CurrentUICulture;
            string two = c.TwoLetterISOLanguageName.ToLowerInvariant();
            if (two == "zh")
                return (c.Name == "zh-TW" || c.Name == "zh-HK" || c.Name == "zh-MO") ? "zh-Hans" : "zh-Hans";
            if (two == "pt") return "pt-BR";
            foreach (string k in Known)
            {
                if (k == two) return k;
                if (k.StartsWith(two + "-", StringComparison.OrdinalIgnoreCase)) return k;
            }
            return "en";
        }

        private static void Parse(string code, Dictionary<string, string> into)
        {
            string text = ReadResource(code);
            if (text == null) return;
            using (StringReader r = new StringReader(text))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("/*") || s.StartsWith("//")) continue;
                    int eq = FindSeparator(s);
                    if (eq < 0) continue;
                    string key = Unquote(s.Substring(0, eq));
                    string rest = s.Substring(eq + 1).Trim();
                    int end = LastQuote(rest);
                    if (end < 0) continue;
                    string val = Unquote(rest.Substring(0, end + 1));
                    if (key != null && val != null && !into.ContainsKey(key)) into[key] = val;
                }
            }
        }

        /// The '=' that sits between the two quoted strings, not one inside them.
        private static int FindSeparator(string s)
        {
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') { i++; continue; }
                if (c == '"') inQuote = !inQuote;
                else if (c == '=' && !inQuote) return i;
            }
            return -1;
        }

        private static int LastQuote(string s)
        {
            for (int i = s.Length - 1; i >= 0; i--)
                if (s[i] == '"' && (i == 0 || s[i - 1] != '\\')) return i;
            return -1;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
        }

        private static string ReadResource(string code)
        {
            string name = "LineDry.res." + code + ".strings";
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream(name))
                {
                    if (st == null) return null;
                    using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
            }
            catch { return null; }
        }

        /// Looks up the English text and returns the user's language for it.
        public static string T(string english)
        {
            if (_table == null) Load();
            string v;
            return _table.TryGetValue(english, out v) ? v : english;
        }

        /// Same, with one %d substituted.
        public static string T(string english, int number)
        {
            return string.Format(T(english), number);
        }
    }
}
