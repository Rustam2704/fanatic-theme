// Tray icons of the fanatic programs always stay on the taskbar (owner 26.10).
// Explorer keeps a preference per icon, but a NEW process of the same exe gets NEW entries with the default preference
// (0 = overflow ^ while EnableAutoTray=1): after a restart / logon the bar's spacer icons landed in ^, the big indicators
// had no room and vanished. One-time "tray-prefs set 2" at install cannot cover that, so the bar itself re-promotes, through
// Explorer's ITrayNotify (the classic tray of ExplorerPatcher), every icon of an exe under the install root (the bar, the
// keyboard switch, fan-tray) that is not "always show" yet. Runs on its own STA thread (the COM callbacks are pumped there),
// never on the UI thread; un-elevated like the bar, which is what Explorer's tray object accepts.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static partial class FanaticBar
{
    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYITEM
    {
        public IntPtr exePtr, tipPtr;   // owned by Explorer: read, never freed
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
    [ComImport, Guid("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD")] class TrayNotifyObj { }

    sealed class TrayCollector : INotificationCB
    {
        public readonly List<NOTIFYITEM> Items = new List<NOTIFYITEM>();
        public readonly List<string> Exe = new List<string>(), Tip = new List<string>();
        public void Notify(uint ev, ref NOTIFYITEM item)
        { Items.Add(item); Exe.Add(Marshal.PtrToStringUni(item.exePtr)); Tip.Add(Marshal.PtrToStringUni(item.tipPtr)); }
    }

    static int trayPromoteBusy;
    // Call from anywhere; does the work on a short-lived STA thread; onChanged runs (on that thread) when something moved.
    static void PromoteOwnTrayIcons(Action onChanged)
    {
        if (Interlocked.Exchange(ref trayPromoteBusy, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                string root = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) + "\\";
                var tn = (ITrayNotify)new TrayNotifyObj();
                try
                {
                    var c = new TrayCollector(); uint h; tn.RegisterCallback(c, out h);
                    int seen = -1; while (seen != c.Items.Count) { seen = c.Items.Count; Thread.CurrentThread.Join(250); }
                    tn.UnregisterCallback(h);
                    int n = 0;
                    for (int j = 0; j < c.Items.Count; j++)
                    {
                        string exe = c.Exe[j];
                        if (c.Items[j].pref == 2 || exe == null || !exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                        var x = c.Items[j]; x.pref = 2;
                        x.exePtr = Marshal.StringToCoTaskMemUni(exe); x.tipPtr = Marshal.StringToCoTaskMemUni(c.Tip[j] ?? "");
                        try { tn.SetPreference(ref x); n++; }
                        catch (ArgumentException) { }   // the icon went away since the listing
                        finally { Marshal.FreeCoTaskMem(x.exePtr); Marshal.FreeCoTaskMem(x.tipPtr); }
                    }
                    if (n > 0) { Log("tray: promoted " + n + " icon(s) to always-show"); if (onChanged != null) onChanged(); }
                }
                finally { Marshal.ReleaseComObject(tn); }
            }
            catch (Exception ex) { Log("tray promote: " + ex.Message); }
            finally { Interlocked.Exchange(ref trayPromoteBusy, 0); }
        }) { IsBackground = true, Name = "tray-promote" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
