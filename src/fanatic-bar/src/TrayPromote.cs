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
    static int trayPromotionRequested = 1;
    static volatile bool trayRetry = true;
    static bool spacerPreferencesReady;
    static IntPtr preferenceTray;
    static int lastTrayError;
    static DateTime nextTrayMaintenance;
    static void RequestTrayPromotion() { Interlocked.Exchange(ref trayPromotionRequested, 1); }
    static void TrayMaintenance()
    {
        bool requested = Interlocked.Exchange(ref trayPromotionRequested, 0) != 0;
        if (!requested && !trayRetry && DateTime.UtcNow < nextTrayMaintenance) return;
        nextTrayMaintenance = DateTime.UtcNow.AddSeconds(15);
        PromoteOwnTrayIcons(() => ui.Post(__ => ArrangeTray(), null));
    }
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
                    IntPtr shell = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
                    if (shell != preferenceTray) { preferenceTray = shell; spacerPreferencesReady = false; }
                    var c = new TrayCollector(); uint h; tn.RegisterCallback(c, out h);
                    try
                    {
                        int seen = -1;
                        for (int wait = 0; seen != c.Items.Count && wait < 12; wait++) { seen = c.Items.Count; Thread.CurrentThread.Join(250); }
                    }
                    finally { tn.UnregisterCallback(h); }
                    // Same template-based SetPreference as tray-prefs setids, including ids not added yet.
                    // Run after every COM recovery, not just when the current icons were promoted.
                    if (bigMode && !spacerPreferencesReady)
                    {
                        string ownExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fanatic-bar.exe");
                        int template = c.Exe.FindIndex(exe => string.Equals(exe, ownExe, StringComparison.OrdinalIgnoreCase));
                        if (template >= 0)
                        {
                            for (uint id = 7201; id <= 7230; id++)
                            {
                                var x = c.Items[template]; x.id = id; x.guid = Guid.Empty; x.pref = 2;
                                x.exePtr = Marshal.StringToCoTaskMemUni(c.Exe[template]); x.tipPtr = Marshal.StringToCoTaskMemUni("");
                                try { tn.SetPreference(ref x); }
                                finally { Marshal.FreeCoTaskMem(x.exePtr); Marshal.FreeCoTaskMem(x.tipPtr); }
                            }
                            spacerPreferencesReady = true;
                            Log("tray: spacer preferences 7201..7230 ready");
                        }
                    }
                    int n = 0;
                    for (int j = 0; j < c.Items.Count; j++)
                    {
                        string exe = c.Exe[j];
                        bool systemMic = bigMode && exe != null && string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase)
                            && c.Items[j].guid == new Guid("7820ae82-23e3-4229-82c1-e41cb67d5b9c");
                        uint preference = systemMic ? 0u : 2u;
                        if (c.Items[j].pref == preference || exe == null || (!systemMic && !exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))) continue;
                        var x = c.Items[j]; x.pref = preference;
                        x.exePtr = Marshal.StringToCoTaskMemUni(exe); x.tipPtr = Marshal.StringToCoTaskMemUni(c.Tip[j] ?? "");
                        try { tn.SetPreference(ref x); n++; }
                        catch (ArgumentException) { }   // the icon went away since the listing
                        finally { Marshal.FreeCoTaskMem(x.exePtr); Marshal.FreeCoTaskMem(x.tipPtr); }
                    }
                    bool recovered = lastTrayError != 0;
                    lastTrayError = 0; trayRetry = bigMode && !spacerPreferencesReady;
                    if (n > 0) Log("tray: updated " + n + " own/microphone preference(s)");
                    if ((n > 0 || recovered || !trayRetry) && onChanged != null) onChanged();
                }
                finally { Marshal.ReleaseComObject(tn); }
            }
            catch (Exception ex)
            {
                trayRetry = true; spacerPreferencesReady = false;
                int error = Marshal.GetHRForException(ex);
                if (error != lastTrayError) Log("tray promote: 0x" + error.ToString("x") + "; retrying every 2 s");
                lastTrayError = error;
            }
            finally { Interlocked.Exchange(ref trayPromoteBusy, 0); }
        }) { IsBackground = true, Name = "tray-promote" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
