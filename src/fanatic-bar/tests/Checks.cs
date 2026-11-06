// Compile with src/*.cs and /main:BarChecks; run beside the isolated exe's assets. Never starts tray/runtime services.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

class BarChecks
{
    [STAThread] static int Main(string[] args)
    {
        try { FanaticBar.Checks(Path.GetFullPath(args[0])); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
static partial class FanaticBar
{
    static void Check(bool yes, string why) { if (!yes) throw new Exception(why); }
    static byte[] HexBytes(string hex) { var b=new byte[hex.Length/2]; for(int i=0;i<b.Length;i++) b[i]=Convert.ToByte(hex.Substring(i*2,2),16); return b; }
    public static void Checks(string dir)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Strings.Load("en"); LoadLocalizedArrays(); ui = new SynchronizationContext();
        var points = new List<AccessPoint> { new AccessPoint { Mac = "d8:47:32:45:d0:2c", Rssi = -50 } };
        byte[] request = AppleRequest(points), expected = HexBytes("00010005656e5f55530013636f6d2e6170706c652e6c6f636174696f6e64000a382e312e313242343131000000010000001912130a1164383a34373a33323a34353a64303a326318002001");
        Check(Convert.ToBase64String(request) == Convert.ToBase64String(expected), "Apple wire framing differs from live HTTP 200 request");
        string live = Path.Combine(dir,"apple-response.bin");
        // Recorded HTTP 200 response for the acceptance BSSID (2026-11-06); use a fresh capture when available.
        var decoded = DecodeApple(File.Exists(live) ? File.ReadAllBytes(live) : HexBytes("0001000000010000004312410a1164383a34373a33323a34353a64303a3263122c088098f7f8bcffffffff01108098f7f8bcffffffff0118ffffffffffffffffff0128ffffffffffffffffff01"));
        Check(decoded.Count == 1 && decoded[0].Mac == points[0].Mac, "Live WLOC response BSSID");
        foreach (var f in decoded) Console.WriteLine(Strings.T("geo.decoded", f.Mac, f.Lat.ToString("F8", Inv), f.Lon.ToString("F8", Inv), f.Accuracy));
        Check(AverageApple(decoded, points) == null, "Unknown WLOC sentinel must be rejected");
        var many = new List<AccessPoint>(); for (int i = 0; i < 20; i++) many.Add(points[0]);
        byte[] bigRequest = AppleRequest(many); Check(bigRequest.Length > 255 && bigRequest[46] == 0 && bigRequest[47] == 0 && bigRequest[48] == 1 && bigRequest[49] == 168, "Multi-AP big-endian length");
        var observations = new List<AccessPoint> { new AccessPoint { Mac="00:00:00:00:00:01", Rssi=-40 }, new AccessPoint { Mac="00:00:00:00:00:02", Rssi=-60 }, new AccessPoint { Mac="00:00:00:00:00:03", Rssi=-20 } };
        var fixes = new List<WifiFix> { new WifiFix { Mac=observations[0].Mac, Lat=50.45,Lon=30.52,Accuracy=30 }, new WifiFix { Mac=observations[1].Mac,Lat=50.4501,Lon=30.5201,Accuracy=40 }, new WifiFix { Mac=observations[2].Mac,Lat=40,Lon=-70,Accuracy=20 } };
        var mean = AverageApple(fixes,observations); Check(mean != null && mean.Lat < 50.45005 && mean.Lat >= 50.45, "RSSI weighting/outlier cut");
        var beacon = DecodeBeacon("{\"location\":{\"lat\":50.45,\"lng\":30.52},\"accuracy\":75}");
        Check(beacon.Lat==50.45 && beacon.Lon==30.52 && beacon.Accuracy==75 && BeaconRequest(points).Contains("\"considerIp\":false"), "BeaconDB codec");
        Console.WriteLine("PASS: live Apple wire/decoder, 20-AP framing, unknown sentinel, RSSI/outliers, BeaconDB JSON");

        geoLat = 50.45; geoLon = 30.52;
        var parsed=ParseWeather("{\"timezone\":\"Europe/Kyiv\",\"current\":{\"temperature_2m\":-7.5,\"weather_code\":71,\"is_day\":1},\"hourly\":{\"time\":[\"2026-11-06T10:00\",\"2026-11-06T11:00\"],\"temperature_2m\":[-7.5,null],\"weather_code\":[71,3],\"is_day\":[1,1]}}");
        Check(parsed.Temp==-7.5 && parsed.Code==71 && parsed.Hours.Count==1 && parsed.Hours[0].Time.Hour==10,"Combined weather JSON/null hours");
        var hours = new List<HourForecast>(); for(int i=0;i<24;i++) hours.Add(new HourForecast { Time=DateTime.Today.AddHours(i),Temp=i-7,Code=2,Day=true });
        var cached = new WeatherCache { Lat=geoLat,Lon=geoLon,Place="Test",Tz="Europe/Kyiv",At=DateTime.UtcNow.AddHours(-2),Temp=-7,Code=71,Day=true,Hours=hours };
        string cache = Path.Combine(dir,"test-weather-cache.json"); SaveWeatherCache(cached,cache);
        cached.Temp=12; SaveWeatherCache(cached,cache); // exercise File.Replace as well as first File.Move
        forecast=null; wTempVal=null; LoadWeatherCache(cache);
        Check(forecast!=null && forecast.Count==24 && wTempVal==12 && (DateTime.Now-wOk).TotalMinutes>60 && !File.Exists(cache+".tmp"), "Atomic cache/old startup data");
        using(var popup = new WeatherPopup())
        {
            popup.Preview(); IntPtr handle=popup.Handle; Check(popup.Controls.Count >= 4, "Cache popup content");
            foreach(Control control in popup.Controls) if(control is HourList) typeof(HourList).GetMethod("ScrollTo",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(control,new object[]{5});
            forecast=new List<HourForecast>(hours); forecastAt=DateTime.Now;
            WeatherChanged(); Check(popup.Handle==handle && popup.Visible, "Refresh must preserve popup handle and visibility");
            foreach(Control control in popup.Controls) if(control is HourList) Check(((HourList)control).ScrollPosition==5,"Refresh must preserve hour scroll position");
        }
        Console.WriteLine("PASS: atomic cache create/replace/load, old-cache startup, popup refresh preserves handle");

        using(var bar = new BigBar(true))
        {
            bigBatShown=75; bigVol=60;
            foreach(int height in new[]{48,60,66,90})
            {
                bar.Size=new Size(1800,height);
                using(var bmp=new Bitmap(bar.Width,height)) bar.DrawToBitmap(bmp,bar.ClientRectangle);
                int natural=(int)typeof(BigBar).GetField("needWidth",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bar);
                bar.Width=natural; using(var bmp=new Bitmap(natural,height)) bar.DrawToBitmap(bmp,bar.ClientRectangle);
                var baseZones=new List<KeyValuePair<Rectangle,Action<bool,Rectangle>>>((List<KeyValuePair<Rectangle,Action<bool,Rectangle>>>)typeof(BigBar).GetField("zones",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bar));
                int width=(natural+47)/48*48; bar.Width=width;
                using(var bmp=new Bitmap(width,height)) bar.DrawToBitmap(bmp,bar.ClientRectangle);
                var zones=(List<KeyValuePair<Rectangle,Action<bool,Rectangle>>>)typeof(BigBar).GetField("zones",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bar);
                Check(zones.Count==8 && zones[0].Key.Left==0 && zones[zones.Count-1].Key.Right==width,"Justified strip edges");
                for(int i=1;i<zones.Count;i++)
                {
                    Check(zones[i].Key.Left>=zones[i-1].Key.Right,"Zones overlap");
                    double added=zones[i].Key.Left-zones[i-1].Key.Right-(baseZones[i].Key.Left-baseZones[i-1].Key.Right);
                    Check(Math.Abs(added-(width-natural)/(double)(zones.Count-1))<=1.01,"Unequal extra zone spacing");
                }
            }
        }
        for(int i=0;i<5;i++) using(var bmp=DrawGpu(24,i))
        {
            Check(bmp.Width==24,"GPU size");
            using(var stream=new MemoryStream()) { bmp.Save(stream,System.Drawing.Imaging.ImageFormat.Png); Check(stream.Length>100,"GPU bitmap"); }
        }
        Check(GpuLevel(-1)==0 && GpuLevel(0)==1 && GpuLevel(25)==1 && GpuLevel(26)==2 && GpuLevel(100)==4,"GPU levels");
        string name=@"Local\fanatic.LayoutTest."+Guid.NewGuid().ToString("N"); IntPtr ev=CreateLayoutEvent(name), other=OpenEventW(LayoutAccess,false,name);
        Check(ev!=IntPtr.Zero && other!=IntPtr.Zero,"Shared event ACL/open");
        try { Check(SetEvent(other) && WaitForSingleObject(ev,0)==0 && WaitForSingleObject(ev,0)==258,"Event set/wait/auto-reset"); }
        finally { CloseHandle(other); CloseHandle(ev); }
        Console.WriteLine("PASS: justified click zones at 48/60/66/90px, GPU levels, shared auto-reset event ACL");
    }
}
