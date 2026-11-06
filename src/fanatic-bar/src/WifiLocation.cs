// Native WLAN observations only; no Windows location service. Apple WLOC wire schema/framing:
// https://github.com/darkosancanin/apple_bssid_locator (int64 coordinates, NOT zig-zag sint64).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static partial class FanaticBar
{
    sealed class AccessPoint { public string Mac; public int Rssi; }
    sealed class WifiFix { public string Mac, Source, Place; public double Lat, Lon, Accuracy; }
    [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr handle, ref Guid id, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid id, IntPtr ssid, int type, bool security, IntPtr reserved, out IntPtr list);
    static List<AccessPoint> ScanWifi(out bool denied)
    {
        denied = false; var points = new Dictionary<string, AccessPoint>(); IntPtr h, interfaces = IntPtr.Zero; uint v;
        int err = WlanOpenHandle(2, IntPtr.Zero, out v, out h);
        if (err != 0) { denied = err == 5; if (denied) return new List<AccessPoint>(); throw new Exception(Strings.T("geo.wlan_error", err)); }
        try
        {
            err = WlanEnumInterfaces(h, IntPtr.Zero, out interfaces);
            if (err != 0) { denied = err == 5; if (denied) return new List<AccessPoint>(); throw new Exception(Strings.T("geo.wlan_error", err)); }
            int count = Marshal.ReadInt32(interfaces); var ids = new List<Guid>();
            for (int i = 0; i < count; i++)
            {
                var id = (Guid)Marshal.PtrToStructure(IntPtr.Add(interfaces, 8 + i * 532), typeof(Guid));
                ids.Add(id); err = WlanScan(h, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); if (err == 5) denied = true;
            }
            // Scan is asynchronous. One bounded wait for ALL interfaces, never one four-second wait per adapter.
            if (ids.Count > 0 && !denied) Thread.Sleep(4000);
            foreach (var iid in ids)
            {
                Guid id = iid; IntPtr list;
                err = WlanGetNetworkBssList(h, ref id, IntPtr.Zero, 3 /*dot11_BSS_type_any*/, false, IntPtr.Zero, out list);
                if (err != 0) { if (err == 5) denied = true; else Log("WLAN BSS: " + err); continue; }
                try
                {
                    int bytes = Marshal.ReadInt32(list), n = Marshal.ReadInt32(list, 4);
                    // WLAN_BSS_ENTRY is 360 bytes with native alignment; MAC at 40, signed RSSI at 56.
                    for (int i = 0; i < n && 8L + (i + 1L) * 360 <= bytes; i++)
                    {
                        IntPtr bss = IntPtr.Add(list, 8 + i * 360); var mac = new byte[6]; Marshal.Copy(IntPtr.Add(bss, 40), mac, 0, 6);
                        if ((mac[0] & 3) != 0 || Array.TrueForAll(mac, b => b == 0)) continue;   // random/local or multicast
                        string key = BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant(); int rssi = Marshal.ReadInt32(bss, 56);
                        AccessPoint old; if (!points.TryGetValue(key, out old) || rssi > old.Rssi) points[key] = new AccessPoint { Mac = key, Rssi = rssi };
                    }
                }
                finally { WlanFreeMemory(list); }
            }
        }
        finally { if (interfaces != IntPtr.Zero) WlanFreeMemory(interfaces); WlanCloseHandle(h, IntPtr.Zero); }
        var result = new List<AccessPoint>(points.Values); result.Sort((a, b) => b.Rssi.CompareTo(a.Rssi));
        return result;
    }

    static void ProtoVar(Stream s, ulong n) { while (n >= 128) { s.WriteByte((byte)(n | 128)); n >>= 7; } s.WriteByte((byte)n); }
    static void ProtoBytes(Stream s, int field, byte[] bytes) { ProtoVar(s, (ulong)((field << 3) | 2)); ProtoVar(s, (ulong)bytes.Length); s.Write(bytes, 0, bytes.Length); }
    static ulong ReadVar(byte[] bytes, ref int pos, int end)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70 && pos < end; shift += 7) { byte b = bytes[pos++]; value |= (ulong)(b & 127) << shift; if ((b & 128) == 0) return value; }
        throw new InvalidDataException(Strings.T("geo.invalid_response"));
    }
    sealed class ProtoField { public int Number; public ulong Value; public byte[] Bytes; }
    static List<ProtoField> ProtoFields(byte[] bytes)
    {
        int pos = 0; var fields = new List<ProtoField>();
        while (pos < bytes.Length)
        {
            ulong tag = ReadVar(bytes, ref pos, bytes.Length); int wire = (int)(tag & 7);
            if ((tag >> 3) == 0) throw new InvalidDataException(Strings.T("geo.invalid_response"));
            var f = new ProtoField { Number = (int)(tag >> 3) };
            if (wire == 0) f.Value = ReadVar(bytes, ref pos, bytes.Length);
            else if (wire == 2)
            {
                ulong len = ReadVar(bytes, ref pos, bytes.Length); if (len > (ulong)(bytes.Length - pos)) throw new InvalidDataException(Strings.T("geo.invalid_response"));
                f.Bytes = new byte[(int)len]; Buffer.BlockCopy(bytes, pos, f.Bytes, 0, (int)len); pos += (int)len;
            }
            else if (wire == 1 || wire == 5) { pos += wire == 1 ? 8 : 4; if (pos > bytes.Length) throw new InvalidDataException(Strings.T("geo.invalid_response")); }
            else throw new InvalidDataException(Strings.T("geo.invalid_response"));
            fields.Add(f);
        }
        return fields;
    }
    static string NormalMac(string mac)
    {
        var parts = mac.ToLowerInvariant().Split(':'); if (parts.Length != 6) throw new FormatException(Strings.T("geo.bad_bssid"));
        for (int i = 0; i < 6; i++) parts[i] = byte.Parse(parts[i], System.Globalization.NumberStyles.HexNumber, Inv).ToString("x2");
        return string.Join(":", parts);
    }
    static byte[] AppleRequest(List<AccessPoint> points)
    {
        using (var payload = new MemoryStream()) using (var request = new MemoryStream())
        {
            for (int i = 0; i < points.Count && i < 20; i++) using (var device = new MemoryStream())
            { ProtoBytes(device, 1, Encoding.UTF8.GetBytes(points[i].Mac)); ProtoBytes(payload, 2, device.ToArray()); }
            payload.WriteByte(24); payload.WriteByte(0); payload.WriteByte(32); payload.WriteByte(1);
            byte[] header = Encoding.ASCII.GetBytes("\0\x01\0\x05" + "en_US" + "\0\x13" + "com.apple.locationd" + "\0\x0a" + "8.1.12B411" + "\0\0\0\x01");
            request.Write(header, 0, header.Length);
            int len = (int)payload.Length; for (int shift = 24; shift >= 0; shift -= 8) request.WriteByte((byte)(len >> shift));
            byte[] body = payload.ToArray(); request.Write(body, 0, body.Length); return request.ToArray();
        }
    }
    static List<WifiFix> DecodeApple(byte[] response)
    {
        if (response.Length < 10) throw new InvalidDataException(Strings.T("geo.invalid_response"));
        var data = new byte[response.Length - 10]; Buffer.BlockCopy(response, 10, data, 0, data.Length); var fixes = new List<WifiFix>();
        foreach (var f in ProtoFields(data)) if (f.Number == 2 && f.Bytes != null)
        {
            var fix = new WifiFix { Source = "apple", Lat = -180, Lon = -180 };
            foreach (var dev in ProtoFields(f.Bytes))
            {
                if (dev.Number == 1 && dev.Bytes != null) fix.Mac = NormalMac(Encoding.UTF8.GetString(dev.Bytes));
                if (dev.Number == 2 && dev.Bytes != null) foreach (var loc in ProtoFields(dev.Bytes))
                {
                    if (loc.Number == 1) fix.Lat = unchecked((long)loc.Value) * 1e-8;
                    if (loc.Number == 2) fix.Lon = unchecked((long)loc.Value) * 1e-8;
                    if (loc.Number == 3) fix.Accuracy = unchecked((long)loc.Value);
                }
            }
            fixes.Add(fix);
        }
        return fixes;
    }
    static List<WifiFix> QueryApple(List<AccessPoint> points, Action<string> trace)
    {
        using (var wc = GeoClient())
        {
            wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
            byte[] request = AppleRequest(points), response = wc.UploadData("https://gs-loc.apple.com/clls/wloc", "POST", request);
            if (trace != null) trace(Strings.T("geo.apple_wire", request.Length, response.Length));
            return DecodeApple(response);
        }
    }
    static double Distance(double a, double o, double b, double p)
    {
        double k = Math.PI / 180, dlat = (b - a) * k, dlon = (p - o) * k;
        double h = Math.Pow(Math.Sin(dlat / 2), 2) + Math.Cos(a * k) * Math.Cos(b * k) * Math.Pow(Math.Sin(dlon / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
    static WifiFix AverageApple(List<WifiFix> fixes, List<AccessPoint> points)
    {
        var known = new List<WifiFix>(); var rssis = new Dictionary<string, int>(); foreach (var p in points) rssis[p.Mac] = p.Rssi;
        foreach (var f in fixes) if (f.Mac != null && rssis.ContainsKey(f.Mac) && Math.Abs(f.Lat) <= 90 && Math.Abs(f.Lon) <= 180) known.Add(f);
        if (known.Count == 0) return null;
        var lat = new List<double>(); var lon = new List<double>(); foreach (var f in known) { lat.Add(f.Lat); lon.Add(f.Lon); } lat.Sort(); lon.Sort();
        double ma = (lat[(lat.Count - 1) / 2] + lat[lat.Count / 2]) / 2, mo = (lon[(lon.Count - 1) / 2] + lon[lon.Count / 2]) / 2;
        double sum = 0, a = 0, o = 0, accuracy = 0;
        foreach (var f in known)
        {
            double distance = Distance(ma, mo, f.Lat, f.Lon); if (distance > 1000) continue;
            double weight = Math.Pow(10, (rssis[f.Mac] + 100) / 20.0); sum += weight; a += f.Lat * weight; o += f.Lon * weight;
            accuracy = Math.Max(accuracy, Math.Max(0, f.Accuracy) + distance);
        }
        return sum == 0 ? null : new WifiFix { Lat = a / sum, Lon = o / sum, Accuracy = accuracy, Source = "apple" };
    }
    static string BeaconRequest(List<AccessPoint> points)
    {
        var aps = new List<object>(); for (int i = 0; i < points.Count && i < 20; i++) aps.Add(new { macAddress = points[i].Mac, signalStrength = points[i].Rssi });
        return Json().Serialize(new { considerIp = false, wifiAccessPoints = aps });
    }
    static WifiFix DecodeBeacon(string js)
    {
        var root = Json().Deserialize<Dictionary<string, object>>(js); var loc = (Dictionary<string, object>)root["location"];
        var fix = new WifiFix { Lat = Convert.ToDouble(loc["lat"], Inv), Lon = Convert.ToDouble(loc["lng"], Inv), Accuracy = Convert.ToDouble(root["accuracy"], Inv), Source = "beacondb" };
        if (Math.Abs(fix.Lat) > 90 || Math.Abs(fix.Lon) > 180 || fix.Accuracy < 0) throw new InvalidDataException(Strings.T("geo.invalid_response"));
        return fix;
    }
    static WifiFix QueryBeacon(List<AccessPoint> points)
    {
        using (var wc = GeoClient()) { wc.Headers[HttpRequestHeader.ContentType] = "application/json"; return DecodeBeacon(wc.UploadString("https://api.beacondb.net/v1/geolocate", "POST", BeaconRequest(points))); }
    }
    static string ReverseWifi(WifiFix fix)
    {
        using (var wc = GeoClient())
        {
            string js = wc.DownloadString(string.Format(Inv, "https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={0}&lon={1}&zoom=10&accept-language={2}", fix.Lat, fix.Lon, Strings.Lang == "ua" ? "uk" : Strings.Lang));
            var root = Json().Deserialize<Dictionary<string, object>>(js); object address;
            if (root.TryGetValue("address", out address))
            {
                var a = (Dictionary<string, object>)address; object value;
                foreach (string name in new[] { "city", "town", "village", "municipality", "county", "state" }) if (a.TryGetValue(name, out value)) return (string)value;
            }
            object display; return root.TryGetValue("display_name", out display) ? (string)display : "";
        }
    }
    static bool ResolveByWifi(Action<string> trace, out string error)
    {
        error = null;
        try
        {
            bool denied; var points = ScanWifi(out denied); if (trace != null) trace(Strings.T("geo.bssid_count", points.Count));
            if (points.Count == 0) { error = Strings.T("geo.wifi_permission"); return false; }
            WifiFix fix = null;
            try { fix = AverageApple(QueryApple(points, trace), points); } catch (Exception ex) { if (trace != null) trace(Strings.T("geo.source_error", "apple", ex.Message)); Log("geo apple: " + ex.Message); }
            if (fix == null) try { fix = QueryBeacon(points); } catch (Exception ex) { if (trace != null) trace(Strings.T("geo.source_error", "beacondb", ex.Message)); Log("geo beacondb: " + ex.Message); }
            if (fix == null) { error = Strings.T("geo.wifi_no_fix"); return false; }
            try { fix.Place = ReverseWifi(fix); } catch (Exception ex) { Log("geo reverse: " + ex.Message); }
            string tz = "auto";
            using (var wc = GeoClient())
            {
                string js = wc.DownloadString(string.Format(Inv, "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&forecast_days=1&timezone=auto", fix.Lat, fix.Lon));
                var root = Json().Deserialize<Dictionary<string, object>>(js); tz = (string)root["timezone"];
            }
            Store(fix.Lat.ToString(Inv), fix.Lon.ToString(Inv), fix.Place, tz);
            if (trace != null) { trace(Strings.T("geo.fix", fix.Source, fix.Lat.ToString("F8", Inv), fix.Lon.ToString("F8", Inv), fix.Accuracy.ToString("0.0", Inv), GeoPlace)); trace(Strings.T("geo.saved", LocationFile)); }
            return true;
        }
        catch (Exception ex) { error = ex.Message; Log("geo wifi: " + ex.Message); return false; }
    }
    static void WlocSelftest(string mac)
    {
        List<AccessPoint> points;
        try { points = new List<AccessPoint> { new AccessPoint { Mac = NormalMac(mac), Rssi = -50 } }; }
        catch (Exception ex) { Console.WriteLine(ex.Message); Environment.ExitCode = 1; return; }
        bool appleOk = false;
        try
        {
            foreach (var fix in QueryApple(points, Console.WriteLine)) Console.WriteLine(Strings.T("geo.decoded", fix.Mac, fix.Lat.ToString("F8", Inv), fix.Lon.ToString("F8", Inv), fix.Accuracy));
            appleOk = true;
        }
        catch (Exception ex) { Console.WriteLine(Strings.T("geo.source_error", "apple", NetworkError(ex))); }
        // Always exercise the fallback too, even when Apple's TLS/provider is unavailable.
        Console.WriteLine(Strings.T("geo.beacon_request", BeaconRequest(points)));
        try { var b = QueryBeacon(points); Console.WriteLine(Strings.T("geo.fix", b.Source, b.Lat, b.Lon, b.Accuracy, "")); }
        catch (Exception ex) { Console.WriteLine(Strings.T("geo.source_error", "beacondb", NetworkError(ex))); }
        var sample = DecodeBeacon("{\"location\":{\"lat\":50.45,\"lng\":30.52},\"accuracy\":75}");
        if (sample.Lat != 50.45 || sample.Lon != 30.52 || sample.Accuracy != 75 || !BeaconRequest(points).Contains("\"considerIp\":false")) throw new Exception(Strings.T("geo.invalid_response"));
        Console.WriteLine(Strings.T("geo.beacon_test_ok"));
        if (!appleOk) Environment.ExitCode = 1;
    }
    static string NetworkError(Exception ex) { return ex.Message + (ex.InnerException == null ? "" : " / " + NetworkError(ex.InnerException)); }
}
