// Localisation (A9): flat key -> text tables in strings\<lang>.json next to the exe (en/uk/ru).
// Dependency-free, matches the bar's existing regex-JSON idiom (no external JSON library on .NET FW 4.8).
// Language id comes from fanatic-bar.json "language" (default "en"). English is always the fallback per key,
// so a missing/untranslated key never shows blank — it falls back to English, then to the key itself.
//
// File format (translator-friendly, flat, grouped by comment keys that start with "_"):
//   {
//     "_group": "Weather conditions (WMO codes)",
//     "weather.wmo.51": "мряка",
//     "battery.remaining": "ще ~{0} год {1} хв"
//   }
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

static class Strings
{
    static Dictionary<string, string> cur = new Dictionary<string, string>();
    static Dictionary<string, string> en = new Dictionary<string, string>();
    static string lang = "en";
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;

    public static string Lang { get { return lang; } }

    // Shared language file so the bar and the sky (separate exes/folders) switch together: %LOCALAPPDATA%\fanatic\lang.txt.
    public static string SharedLangPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fanatic", "lang.txt"); }
    }
    // The language to use: the shared file if present, else the app's own default (e.g. its config), else English.
    public static string ResolveLang(string localDefault)
    {
        try { if (File.Exists(SharedLangPath)) { string s = File.ReadAllText(SharedLangPath).Trim(); if (s.Length > 0) return s; } }
        catch { }
        return string.IsNullOrEmpty(localDefault) ? "en" : localDefault;
    }
    public static void SaveSharedLang(string language)
    {
        try { string p = SharedLangPath; Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, (language ?? "en").Trim()); }
        catch { }
    }

    // Call once at startup with the language from settings. Safe to call again to switch.
    public static void Load(string language)
    {
        lang = string.IsNullOrEmpty(language) ? "en" : language.Trim().ToLowerInvariant();
        en = Parse("en");
        cur = lang == "en" ? en : Parse(lang);
    }

    static Dictionary<string, string> Parse(string code)
    {
        var d = new Dictionary<string, string>();
        try
        {
            string p = Path.Combine(Dir, "strings", code + ".json");
            if (!File.Exists(p)) return d;
            string js = File.ReadAllText(p, Encoding.UTF8);
            // flat "key": "value" pairs; both sides may contain escaped quotes/backslashes.
            foreach (Match m in Regex.Matches(js, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string k = Unescape(m.Groups[1].Value);
                if (k.Length == 0 || k[0] == '_') continue;   // "_group"/"_meta" comment keys are ignored
                d[k] = Unescape(m.Groups[2].Value);
            }
        }
        catch { }
        return d;
    }

    static string Unescape(string s)
    {
        return s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    // T("id") -> current language, else English, else the id itself (so a gap is visible, never blank).
    public static string T(string id)
    {
        string v;
        if (cur.TryGetValue(id, out v)) return v;
        if (en.TryGetValue(id, out v)) return v;
        return id;
    }

    // T("id", a, b) -> string.Format with the resolved template; falls back to the template on a format error.
    public static string T(string id, params object[] args)
    {
        try { return string.Format(T(id), args); }
        catch { return T(id); }
    }
}
