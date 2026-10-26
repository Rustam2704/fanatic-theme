// 60-second probe: logs every MSI_Event (BIOS ACPI-WMI hotkey events) and brightness changes (polled 100 ms).
using System;
using System.IO;
using System.Management;
using System.Threading;

static class FnProbe2
{
    static void Main(string[] a)
    {
        var log = new StreamWriter(a[0], false) { AutoFlush = true };
        object sync = new object();
        Action<string> W = s => { lock (sync) log.WriteLine("{0:HH:mm:ss.fff} {1}", DateTime.Now, s); };
        var w = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new EventQuery("SELECT * FROM MSI_Event"));
        w.EventArrived += (s, e) => { object v = e.NewEvent["MSIEvt"]; W("MSI_Event MSIEvt=" + (v is Array ? string.Join(",", (object[])((Array)v).Cast()) : string.Format("0x{0:X}", v))); };
        w.Start();
        int prev = -1; var end = DateTime.Now.AddSeconds(60);
        var q = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
        while (DateTime.Now < end)
        {
            foreach (ManagementObject o in q.Get()) { int b = Convert.ToInt32(o["CurrentBrightness"]); if (b != prev) { W("brightness=" + b); prev = b; } }
            Thread.Sleep(100);
        }
        w.Stop();
    }
    static object[] Cast(this Array x) { var r = new object[x.Length]; x.CopyTo(r, 0); return r; }
}
