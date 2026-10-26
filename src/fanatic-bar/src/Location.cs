// Location (A5): the weather coordinates are no longer hardcoded to Kyiv. They live in location.json next to the exe
// (a sidecar, like power-mode.txt). First run with no file auto-resolves by IP (ipwho.is, keyless HTTPS) so a machine in
// Ireland shows Ireland, not Kyiv. A manual "update location" action re-resolves by IP, and a city name can be entered
// (Open-Meteo geocoding). Once stored it is reused, so the app does NOT need the Location permission on every start.
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

static partial class FanaticBar
{
    static double geoLat = double.NaN, geoLon = double.NaN;
    static string geoPlace = "", geoTz = "auto";
    static readonly object geoLock = new object();

    static bool HasLocation { get { return !double.IsNaN(geoLat) && !double.IsNaN(geoLon); } }
    // Shared with the sky (it projects the real sky for the same place): %LOCALAPPDATA%\fanatic\location.json
    static string LocationFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fanatic", "location.json"); } }

    // The place name for the tray tooltip: the resolved city, else the (localisable) fallback label.
    static string GeoPlace { get { return string.IsNullOrEmpty(geoPlace) ? Strings.T("weather.city") : geoPlace; } }
    // Open-Meteo query fragment; timezone=auto unless we stored an IANA zone.
    static string GeoQuery { get { return string.Format(Inv, "latitude={0}&longitude={1}", geoLat, geoLon); } }
    static string GeoTz { get { return string.IsNullOrEmpty(geoTz) ? "auto" : geoTz; } }

    static void LoadLocation()
    {
        try
        {
            if (!File.Exists(LocationFile)) return;
            string js = File.ReadAllText(LocationFile);
            double la, lo;
            var mla = Regex.Match(js, "\"lat\"\\s*:\\s*(-?[0-9.]+)"); var mlo = Regex.Match(js, "\"lon\"\\s*:\\s*(-?[0-9.]+)");
            if (mla.Success && mlo.Success && double.TryParse(mla.Groups[1].Value, NumberStyles.Float, Inv, out la)
                && double.TryParse(mlo.Groups[1].Value, NumberStyles.Float, Inv, out lo))
            {
                lock (geoLock)
                {
                    geoLat = la; geoLon = lo;
                    var mp = Regex.Match(js, "\"place\"\\s*:\\s*\"([^\"]*)\""); geoPlace = mp.Success ? mp.Groups[1].Value : "";
                    var mt = Regex.Match(js, "\"tz\"\\s*:\\s*\"([^\"]*)\""); geoTz = mt.Success ? mt.Groups[1].Value : "auto";
                }
            }
        }
        catch (Exception ex) { Log("location load: " + ex.Message); }
    }

    static void SaveLocation()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocationFile));
            string place = (geoPlace ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            string tz = (geoTz ?? "auto").Replace("\\", "\\\\").Replace("\"", "\\\"");
            File.WriteAllText(LocationFile, string.Format(Inv,
                "{{ \"lat\": {0}, \"lon\": {1}, \"place\": \"{2}\", \"tz\": \"{3}\" }}\r\n", geoLat, geoLon, place, tz));
        }
        catch (Exception ex) { Log("location save: " + ex.Message); }
    }

    static WebClient GeoClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 };
        wc.Headers[HttpRequestHeader.UserAgent] = "fanatic-bar";
        return wc;
    }

    // Resolve the approximate location from the IP (no key, no Location permission). Returns true on success.
    static bool ResolveByIp()
    {
        try
        {
            using (var wc = GeoClient())
            {
                string js = wc.DownloadString("https://ipwho.is/");
                if (Regex.Match(js, "\"success\"\\s*:\\s*false").Success) return false;
                var la = Regex.Match(js, "\"latitude\"\\s*:\\s*(-?[0-9.]+)"); var lo = Regex.Match(js, "\"longitude\"\\s*:\\s*(-?[0-9.]+)");
                if (!la.Success || !lo.Success) return false;
                string city = Match1(js, "\"city\"\\s*:\\s*\"([^\"]*)\"");
                string tz = Match1(js, "\"timezone\"\\s*:\\s*\\{[^}]*?\"id\"\\s*:\\s*\"([^\"]*)\"");
                Store(la.Groups[1].Value, lo.Groups[1].Value, city, tz);
                return true;
            }
        }
        catch (Exception ex) { Log("geo ip: " + ex.Message); return false; }
    }

    // Resolve from a typed city name (Open-Meteo geocoding, keyless). Returns true on a match.
    static bool ResolveByCity(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        try
        {
            using (var wc = GeoClient())
            {
                string js = wc.DownloadString("https://geocoding-api.open-meteo.com/v1/search?count=1&language=" +
                    (Strings.Lang == "ru" ? "ru" : "en") + "&format=json&name=" + Uri.EscapeDataString(query.Trim()));
                var la = Regex.Match(js, "\"latitude\"\\s*:\\s*(-?[0-9.]+)"); var lo = Regex.Match(js, "\"longitude\"\\s*:\\s*(-?[0-9.]+)");
                if (!la.Success || !lo.Success) return false;   // no "results" when nothing matches
                string city = Match1(js, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                string tz = Match1(js, "\"timezone\"\\s*:\\s*\"([^\"]*)\"");
                Store(la.Groups[1].Value, lo.Groups[1].Value, city, tz);
                return true;
            }
        }
        catch (Exception ex) { Log("geo city: " + ex.Message); return false; }
    }

    static void Store(string la, string lo, string city, string tz)
    {
        double a, o; double.TryParse(la, NumberStyles.Float, Inv, out a); double.TryParse(lo, NumberStyles.Float, Inv, out o);
        lock (geoLock) { geoLat = a; geoLon = o; geoPlace = city ?? ""; geoTz = string.IsNullOrEmpty(tz) ? "auto" : tz; }
        SaveLocation();
        // the sky re-projects the stars for the new place (its reload forces a full refresh)
        try { System.Threading.EventWaitHandle ev; if (System.Threading.EventWaitHandle.TryOpenExisting("fanatic.SkyStars.Reload", out ev)) using (ev) ev.Set(); } catch { }
    }

    static string Match1(string s, string pat) { var m = Regex.Match(s, pat); return m.Success ? m.Groups[1].Value : ""; }

    // Ensure a location before fetching weather: load the file, else auto-resolve by IP once. Runs on the weather thread.
    static void EnsureLocation()
    {
        if (HasLocation) return;
        LoadLocation();
        if (!HasLocation) ResolveByIp();
    }
}
