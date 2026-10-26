// Network readings for the Wi-Fi / ping indicator and its tooltip (whitebook format, owner batch 4):
//   <SSID> (-XX dBm, XX%) / status / internet (1.1.1.1) and router: now, median, loss over a window / interface traffic.
// One ping per second to each target; the median and the loss are over the last 10 s (internet) and 30 s (router).
// RSSI comes from the native Wi-Fi API (wlan_intf_opcode_rssi needs no location permission, unlike the current-connection query);
// the quality is Windows' own mapping 2 * (rssi + 100), clamped to 0..100.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

static partial class FanaticBar
{
    const string InternetHost = "1.1.1.1";
    const int InternetWindowS = 10, RouterWindowS = 30;

    sealed class PingSeries
    {
        readonly Queue<KeyValuePair<DateTime, int>> samples = new Queue<KeyValuePair<DateTime, int>>();
        readonly int windowS;
        public PingSeries(int windowS) { this.windowS = windowS; }
        public void Add(int ms)
        {
            lock (samples)
            {
                var now = DateTime.UtcNow; samples.Enqueue(new KeyValuePair<DateTime, int>(now, ms));
                while (samples.Count > 0 && (now - samples.Peek().Key).TotalSeconds > windowS) samples.Dequeue();
            }
        }
        public void Clear() { lock (samples) samples.Clear(); }
        // "зараз X ms, медіана Y ms, втрати Z%" (or "немає даних")
        public string Summary()
        {
            int[] all; lock (samples) all = samples.Select(s => s.Value).ToArray();
            if (all.Length == 0) return Strings.T("net.ping.no_data");
            var ok = all.Where(v => v >= 0).OrderBy(v => v).ToArray();
            int loss = (int)Math.Round(100.0 * (all.Length - ok.Length) / all.Length), last = all[all.Length - 1];
            string now = last < 0 ? "—" : last + " ms", med = ok.Length == 0 ? "—" : Median(ok) + " ms";
            return Strings.T("net.ping.summary", now, med, loss, windowS);
        }
        public int LossPct() { int[] all; lock (samples) all = samples.Select(s => s.Value).ToArray(); return all.Length == 0 ? 0 : (int)Math.Round(100.0 * all.Count(v => v < 0) / all.Length); }
        public int MedianMs() { int[] ok; lock (samples) ok = samples.Select(s => s.Value).Where(v => v >= 0).OrderBy(v => v).ToArray(); return ok.Length == 0 ? -1 : Median(ok); }
        static int Median(int[] sorted) { int n = sorted.Length; return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2] + 1) / 2; }
    }
    static readonly PingSeries internetPings = new PingSeries(InternetWindowS), routerPings = new PingSeries(RouterWindowS);

    // the interface that carries the default route (refreshed with the Wi-Fi poll), its gateway and the byte counters
    static volatile NetworkInterface netIface; static volatile string gateway;
    static long lastRx = -1, lastTx = -1; static DateTime lastBytesAt; static long rxRate, txRate;
    static void PollIface()
    {
        NetworkInterface best = null; string gw = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var g = ni.GetIPProperties().GatewayAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.Equals(System.Net.IPAddress.Any));
            if (g == null) continue;
            best = ni; gw = g.Address.ToString();
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) break;   // prefer Wi-Fi when both are up
        }
        if (best == null || netIface == null || best.Id != netIface.Id) { lastRx = lastTx = -1; routerPings.Clear(); }
        netIface = best; gateway = gw;
    }
    static void PollBytes()
    {
        var ni = netIface; if (ni == null) { rxRate = txRate = 0; return; }
        try
        {
            var st = ni.GetIPStatistics(); var now = DateTime.UtcNow;
            if (lastRx >= 0)
            {
                double dt = Math.Max(0.2, (now - lastBytesAt).TotalSeconds);
                rxRate = (long)((st.BytesReceived - lastRx) / dt); txRate = (long)((st.BytesSent - lastTx) / dt);
            }
            lastRx = st.BytesReceived; lastTx = st.BytesSent; lastBytesAt = now;
        }
        catch { rxRate = txRate = 0; }
    }
    static string Rate(long bps)
    {
        if (bps < 0) bps = 0;
        if (bps < 1024) return bps + " B/s";
        if (bps < 1024 * 1024) return (bps / 1024.0).ToString("0.0", Inv) + " KB/s";
        return (bps / 1048576.0).ToString("0.0", Inv) + " MB/s";
    }

    // ---- native Wi-Fi: RSSI of the first connected interface (int.MinValue = none) ----
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint v, IntPtr r, out uint nv, out IntPtr h);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr r, out int size, out IntPtr data, IntPtr t);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr h, IntPtr r);
    static IntPtr wlan;
    static int WifiRssi()
    {
        try
        {
            uint nv; if (wlan == IntPtr.Zero && WlanOpenHandle(2, IntPtr.Zero, out nv, out wlan) != 0) { wlan = IntPtr.Zero; return int.MinValue; }
            IntPtr list; if (WlanEnumInterfaces(wlan, IntPtr.Zero, out list) != 0) return int.MinValue;
            try
            {
                int n = Marshal.ReadInt32(list);
                for (int i = 0; i < n; i++)
                {
                    IntPtr it = list + 8 + i * 532;   // WLAN_INTERFACE_INFO: GUID, WCHAR[256], state
                    if (Marshal.ReadInt32(it + 528) != 1 /*wlan_interface_state_connected*/) continue;
                    var g = (Guid)Marshal.PtrToStructure(it, typeof(Guid)); int sz; IntPtr d;
                    if (WlanQueryInterface(wlan, ref g, 0x10000102 /*wlan_intf_opcode_rssi*/, IntPtr.Zero, out sz, out d, IntPtr.Zero) != 0) continue;
                    int rssi = Marshal.ReadInt32(d); WlanFreeMemory(d); return rssi;
                }
            }
            finally { WlanFreeMemory(list); }
        }
        catch { }
        return int.MinValue;
    }
    static volatile string wifiName;   // SSID (profile name) or null when not on Wi-Fi; "(дріт)" names for wired
    static volatile bool wired;

    // The multi-line tooltip of the Wi-Fi / ping indicator.
    static string NetTip()
    {
        var sb = new StringBuilder(); string name = wifiName; var ni = netIface;
        if (name == null) sb.Append(Strings.T("net.tip.no_connection"));
        else if (wired) sb.Append(Strings.T("net.tip.wired", name));
        else
        {
            int rssi = WifiRssi();
            sb.Append(rssi == int.MinValue ? name : name + " (" + rssi + " dBm, " + Math.Max(0, Math.Min(100, 2 * (rssi + 100))) + "%)");
        }
        sb.Append('\n').Append(NetStatus()).Append("\n\n");
        sb.Append(Strings.T("net.tip.internet", InternetHost, internetPings.Summary())).Append('\n');
        string gw = gateway;
        sb.Append(Strings.T("net.tip.router", (gw ?? "—"), (gw == null ? Strings.T("net.tip.no_gateway") : routerPings.Summary()))).Append('\n');
        sb.Append(Strings.T("net.tip.interface", (ni != null ? ni.Name : "—"), Rate(rxRate), Rate(txRate)));
        return sb.ToString();
    }
    static string NetStatus()
    {
        if (wifiName == null) return Strings.T("net.state.not_connected");
        int iLoss = internetPings.LossPct(), rLoss = routerPings.LossPct(), med = internetPings.MedianMs();
        if (gateway != null && rLoss >= 100) return Strings.T("net.state.router_down");
        if (iLoss >= 100) return Strings.T("net.state.no_internet");
        if (iLoss > 0) return Strings.T("net.state.packet_loss");
        if (med >= 100) return Strings.T("net.state.high_latency");
        return Strings.T("net.state.ok");
    }
}
