// One current+hourly request and one atomic cache: logon and flyouts share the last successful reading, even offline.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;

static partial class FanaticBar
{
    const int WeatherMinutes = 15;
    static readonly object weatherFetchLock = new object(), weatherStateLock = new object();
    static event Action WeatherChanged;
    sealed class WeatherCache
    {
        public double Lat, Lon; public string Place, Tz; public DateTime At;
        public int Code; public bool Day; public double? Temp; public List<HourForecast> Hours;
    }
    static string WeatherCacheFile { get { return Path.Combine(Path.GetDirectoryName(LocationFile), "weather-cache.json"); } }
    static JavaScriptSerializer Json() { return new JavaScriptSerializer(); }
    static void LoadWeatherCache(string path = null)
    {
        try
        {
            path = path ?? WeatherCacheFile;
            if (!File.Exists(path)) return;
            var c = Json().Deserialize<WeatherCache>(File.ReadAllText(path));
            if (c == null || c.At == DateTime.MinValue || c.Hours == null || c.Hours.Count == 0 || Math.Abs(c.Lat) > 90 || Math.Abs(c.Lon) > 180) return;
            // Never show another city's cache after the shared sky/location file has changed.
            if (HasLocation && (Math.Abs(c.Lat - geoLat) > 0.001 || Math.Abs(c.Lon - geoLon) > 0.001)) return;
            if (!HasLocation) { geoLat = c.Lat; geoLon = c.Lon; geoPlace = c.Place; geoTz = c.Tz; }
            ApplyWeather(c);
        }
        catch (Exception ex) { Log("weather cache load: " + ex.Message); }
    }
    static void ApplyWeather(WeatherCache c)
    {
        lock (weatherStateLock)
        {
            wCode = c.Code; wDay = c.Day; wTempVal = c.Temp; wOk = c.At.ToLocalTime();
            forecast = c.Hours; forecastAt = wOk; forecastError = null;
        }
    }
    static void SaveWeatherCache(WeatherCache c, string path = null)
    {
        path = path ?? WeatherCacheFile; string tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tmp, Json().Serialize(c));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch (Exception ex) { Log("weather cache save: " + ex.Message); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
    static void NotifyWeather()
    {
        if (ui == null) return;
        ui.Post(_ => { RenderWeather(); var changed = WeatherChanged; if (changed != null) changed(); }, null);
    }
    static void RefreshWeatherSoon() { ThreadPool.QueueUserWorkItem(_ => PollWeather()); }
    static WeatherCache ParseWeather(string js)
    {
        var root = Json().Deserialize<Dictionary<string, object>>(js);
        var cur = (Dictionary<string, object>)root["current"];
        var hourly = (Dictionary<string, object>)root["hourly"];
        var times = (IList)hourly["time"]; var codes = (IList)hourly["weather_code"];
        var temps = (IList)hourly["temperature_2m"]; var days = (IList)hourly["is_day"];
        var hours = new List<HourForecast>();
        for (int i = 0; i < times.Count && i < codes.Count && i < temps.Count && i < 24; i++)
        {
            if (temps[i] == null || codes[i] == null) continue;
            hours.Add(new HourForecast { Time = DateTime.ParseExact((string)times[i], "yyyy-MM-dd'T'HH:mm", Inv),
                Code = Convert.ToInt32(codes[i]), Temp = Convert.ToDouble(temps[i], Inv), Day = i >= days.Count || Convert.ToInt32(days[i]) != 0 });
        }
        if (hours.Count == 0 || cur["temperature_2m"] == null || cur["weather_code"] == null) throw new Exception(Strings.T("weather.no_data"));
        string tz = (string)root["timezone"];
        return new WeatherCache { Lat = geoLat, Lon = geoLon, Place = geoPlace, Tz = tz, At = DateTime.UtcNow,
            Temp = Convert.ToDouble(cur["temperature_2m"], Inv), Code = Convert.ToInt32(cur["weather_code"]), Day = Convert.ToInt32(cur["is_day"]) != 0, Hours = hours };
    }
    static void PollWeather()
    {
        if (!Monitor.TryEnter(weatherFetchLock)) return;
        bool ok = false;
        try
        {
            EnsureLocation();
            if (!HasLocation) throw new Exception(Strings.T("geo.unknown"));
            using (var wc = GeoClient())
            {
                string js = wc.DownloadString("https://api.open-meteo.com/v1/forecast?" + GeoQuery + "&current=temperature_2m,weather_code,is_day&hourly=weather_code,temperature_2m,is_day&forecast_hours=24&timezone=auto");
                var c = ParseWeather(js);
                ApplyWeather(c); SaveWeatherCache(c); ok = true;
                if (geoTz != c.Tz) { geoTz = c.Tz; SaveLocation(); }
            }
        }
        catch (Exception ex) { forecastError = ex.Message; Log("weather: " + ex.Message); }
        finally
        {
            if (weatherTimer != null) try { weatherTimer.Change((ok ? WeatherMinutes : 2) * 60000, Timeout.Infinite); } catch (ObjectDisposedException) { }
            Monitor.Exit(weatherFetchLock); NotifyWeather();
        }
    }
}
