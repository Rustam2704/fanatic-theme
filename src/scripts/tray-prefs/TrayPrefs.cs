// tray-prefs: per-icon notification-area preferences through Explorer's ITrayNotify (the classic Win10 tray that
// ExplorerPatcher restores; the same as "Select which icons appear on the taskbar").
//   tray-prefs list                         every known icon: preference, exe, id, tip (read-only)
//   tray-prefs set <pref> <exe-substr>...   set the preference of every icon whose exe path contains a substring
//   tray-prefs setids <pref> <exe-substr> <from> <to>  also ids not shown yet (an icon the app adds later), from a template
//   tray-prefs autotray on|off              off = "Always show all icons" (the overflow ^ is unused)
// pref: 0 = hide (overflow ^, notifications only), 1 = hide icon and notifications, 2 = always show on the taskbar
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class TrayPrefs
{
    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYITEM
    {
        public IntPtr exePtr, tipPtr;   // owned by Explorer: read, never freed (string marshalling freed them -> heap corruption)
        public string exe { get { return Marshal.PtrToStringUni(exePtr); } }
        public string tip { get { return Marshal.PtrToStringUni(tipPtr); } }
        public IntPtr icon, hwnd;
        public uint pref, id;
        public Guid guid;
    }
    [ComImport, Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INotificationCB { void Notify(uint ev, ref NOTIFYITEM item); }
    [ComImport, Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITrayNotify
    {
        void RegisterCallback(INotificationCB cb, out uint handle);
        void UnregisterCallback(uint handle);
        void SetPreference(ref NOTIFYITEM item);
        void EnableAutoTray([MarshalAs(UnmanagedType.Bool)] bool on);
    }
    [ComImport, Guid("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD")] class TrayNotify { }

    sealed class Collector : INotificationCB
    {
        public readonly List<NOTIFYITEM> Items = new List<NOTIFYITEM>();
        public readonly List<string[]> Text = new List<string[]>();
        public void Notify(uint ev, ref NOTIFYITEM item) { Items.Add(item); Text.Add(new[] { item.exe, item.tip }); }
    }

    static Collector Items(ITrayNotify tn)
    {
        var c = new Collector(); uint h; tn.RegisterCallback(c, out h);
        // the items arrive as callbacks: pump COM (STA) until none came for 300 ms
        int seen = -1; while (seen != c.Items.Count) { seen = c.Items.Count; System.Threading.Thread.CurrentThread.Join(300); }
        tn.UnregisterCallback(h);
        return c;
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length == 0) { Console.Error.WriteLine("usage: tray-prefs list | set <0|1|2> <exe-substr>... | autotray on|off"); return 2; }
        var tn = (ITrayNotify)new TrayNotify();
        try
        {
            if (a[0] == "list")
            {
                var c = Items(tn);
                for (int j = 0; j < c.Items.Count; j++) { var i = c.Items[j]; Console.WriteLine("{0} | {1} | id {2} | {3} | {4}", i.pref, c.Text[j][0], i.id, i.guid == Guid.Empty ? "-" : i.guid.ToString(), (c.Text[j][1] ?? "").Replace("\n", " / ").Replace("\r", "")); }
                return 0;
            }
            if (a[0] == "set" && a.Length >= 3)
            {
                uint pref = uint.Parse(a[1]); int n = 0;
                var c = Items(tn);
                for (int j = 0; j < c.Items.Count; j++)
                {
                    string exe = c.Text[j][0];
                    for (int k = 2; k < a.Length; k++)
                        if (exe != null && exe.IndexOf(a[k], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // fresh native strings for the call (the callback's pointers are gone by now)
                            var x = c.Items[j]; x.pref = pref; x.exePtr = Marshal.StringToCoTaskMemUni(exe); x.tipPtr = Marshal.StringToCoTaskMemUni(c.Text[j][1] ?? "");
                            try { tn.SetPreference(ref x); }
                            catch (ArgumentException) { Console.WriteLine("gone: {0} id {1}", exe, x.id); break; }   // removed since the listing
                            finally { Marshal.FreeCoTaskMem(x.exePtr); Marshal.FreeCoTaskMem(x.tipPtr); }
                            n++; Console.WriteLine("set {0}: {1} id {2}", pref, exe, x.id); break;
                        }
                }
                Console.WriteLine("{0} icon(s) changed", n); return 0;
            }
            if (a[0] == "setids" && a.Length == 5)
            {
                uint pref = uint.Parse(a[1]), from = uint.Parse(a[3]), to = uint.Parse(a[4]); var c = Items(tn); int j = c.Text.FindIndex(t => t[0] != null && t[0].IndexOf(a[2], StringComparison.OrdinalIgnoreCase) >= 0);
                if (j < 0) { Console.Error.WriteLine("no icon of " + a[2]); return 1; }
                for (uint id = from; id <= to; id++)
                {
                    var x = c.Items[j]; x.pref = pref; x.id = x.guid == Guid.Empty ? id : x.id; x.exePtr = Marshal.StringToCoTaskMemUni(c.Text[j][0]); x.tipPtr = Marshal.StringToCoTaskMemUni("");
                    try { tn.SetPreference(ref x); } finally { Marshal.FreeCoTaskMem(x.exePtr); Marshal.FreeCoTaskMem(x.tipPtr); }
                }
                Console.WriteLine("set {0}: {1} ids {2}..{3}", pref, c.Text[j][0], from, to); return 0;
            }
            if (a[0] == "autotray" && a.Length == 2) { tn.EnableAutoTray(a[1] == "on"); Console.WriteLine("autotray " + a[1]); return 0; }
            Console.Error.WriteLine("bad arguments"); return 2;
        }
        finally { Marshal.ReleaseComObject(tn); }
    }
}
