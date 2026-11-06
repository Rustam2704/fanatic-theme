// PDH wildcard arrays enumerate the current processes in one bulk read, without a PerformanceCounter object per instance.
// The busiest physical engine is the Task Manager utilisation; summing independent engines would exceed 100%.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

static partial class FanaticBar
{
    [StructLayout(LayoutKind.Sequential)] struct GpuRaw { public uint Status, StampLow, StampHigh; public long First, Second; public uint Count; }
    [StructLayout(LayoutKind.Sequential)] struct GpuRawItem { public IntPtr Name; public GpuRaw Raw; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetRawCounterArrayW(IntPtr counter, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
    static readonly object gpuLock = new object();
    static IntPtr gpuQuery, gpuCounter;
    static Dictionary<string, GpuRaw> gpuPrevious = new Dictionary<string, GpuRaw>();
    static bool gpuFailed;
    static readonly Regex gpuEngine = new Regex(@"^pid_\d+_(luid_.+?_phys_\d+_eng_\d+)_engtype_", RegexOptions.Compiled);
    static int ReadGpuPercent()
    {
        lock (gpuLock)
        {
            if (gpuFailed) return -1;
            try
            {
                if (gpuQuery == IntPtr.Zero)
                {
                    if (PdhOpenQuery(null, IntPtr.Zero, out gpuQuery) != 0 ||
                        PdhAddEnglishCounter(gpuQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuCounter) != 0)
                        throw new Exception("GPU Engine counter unavailable");
                }
                uint status = PdhCollectQueryData(gpuQuery);
                if (status != 0) throw new Exception("PDH collect 0x" + status.ToString("x"));
                uint bytes = 0, count;
                status = PdhGetRawCounterArrayW(gpuCounter, ref bytes, out count, IntPtr.Zero);
                if (bytes == 0) { gpuPrevious.Clear(); return 0; }
                if (status != 0 && status != 0x800007D2) throw new Exception("PDH size 0x" + status.ToString("x"));
                var mem = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    status = PdhGetRawCounterArrayW(gpuCounter, ref bytes, out count, mem);
                    if (status != 0) throw new Exception("PDH array 0x" + status.ToString("x"));
                    var next = new Dictionary<string, GpuRaw>(); var sums = new Dictionary<string, double>();
                    int stride = Marshal.SizeOf(typeof(GpuRawItem));
                    for (int i = 0; i < count; i++)
                    {
                        var item = (GpuRawItem)Marshal.PtrToStructure(IntPtr.Add(mem, i * stride), typeof(GpuRawItem));
                        string name = Marshal.PtrToStringUni(item.Name); var m = gpuEngine.Match(name);
                        if (!m.Success || item.Raw.Status > 1) continue;
                        next[name] = item.Raw; GpuRaw old;
                        if (!gpuPrevious.TryGetValue(name, out old)) continue;
                        // PERF_100NSEC_TIMER: both the running-time numerator and timestamp denominator are in 100 ns.
                        long dt = item.Raw.Second - old.Second, dv = item.Raw.First - old.First;
                        if (dt <= 0 || dv < 0) continue;
                        string engine = m.Groups[1].Value; double value; sums.TryGetValue(engine, out value);
                        sums[engine] = value + 100.0 * dv / dt;
                    }
                    bool primed = gpuPrevious.Count > 0; gpuPrevious = next;
                    double max = 0; foreach (double value in sums.Values) max = Math.Max(max, value);
                    return !primed && next.Count > 0 ? -1 : (int)Math.Round(Math.Max(0, Math.Min(100, max)));
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            catch (Exception ex)
            {
                gpuFailed = true; Log("GPU: " + ex.Message);
                if (gpuQuery != IntPtr.Zero) { PdhCloseQuery(gpuQuery); gpuQuery = IntPtr.Zero; }
                return -1;
            }
        }
    }
    static int GpuLevel(int pct) { return pct < 0 ? 0 : Math.Max(1, Math.Min(4, (int)Math.Ceiling(pct / 25.0))); }
    static Icon GpuIcon(int size, int level) { return new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gpu-icons", "gpu-L" + level + ".ico"), new Size(size, size)); }
    static Bitmap DrawGpu(int size, int level)
    {
        // Framework Icon.ToBitmap interprets PNG-only ICO frames as DIBs on some builds (coloured noise).
        // DrawIcon asks Windows to decode the selected ICO frame instead.
        Graphics g; var bmp = NewIcon(size, out g);
        using (g) using (var icon = GpuIcon(size, level)) g.DrawIcon(icon, new Rectangle(0, 0, size, size));
        return bmp;
    }
    static void PollGpu()
    {
        int pct = ReadGpuPercent(), level = GpuLevel(pct);
        Show(gpu, level.ToString(), s => DrawGpu(s, level), Strings.T("tip.gpu", pct < 0 ? "--" : pct.ToString()));
    }
    static void GpuProbe()
    {
        ReadGpuPercent();
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(1000); var sw = Stopwatch.StartNew(); int pct = ReadGpuPercent(); sw.Stop();
            Console.WriteLine(Strings.T("probe.gpu", pct < 0 ? "--" : pct.ToString(), sw.Elapsed.TotalMilliseconds.ToString("0.00", Inv)));
        }
    }
}
