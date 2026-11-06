using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

static partial class KeyboardSwitch
{
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES
    { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES security, bool manual, bool initial, string name);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint process);

    // Enumerate every time, so replacing or adding a layout never leaves a stale fixed KLID.
    // If several HKLs share a language, keep the previous installed choice; otherwise use list order.
    static void ResolveLayouts()
    {
        int count = GetKeyboardLayoutList(0, null);
        var installed = new IntPtr[Math.Max(1, count)];
        int found = GetKeyboardLayoutList(installed.Length, installed);
        for (int i = 0; i < Hkl.Length; i++)
        {
            IntPtr match = IntPtr.Zero;
            for (int j = 0; j < found; j++)
                if (Language(installed[j]) == LangIds[i])
                {
                    if (match == IntPtr.Zero) match = installed[j];
                    if (installed[j] == Hkl[i]) { match = installed[j]; break; }
                }
            Hkl[i] = match != IntPtr.Zero ? match : LoadKeyboardLayout(Klids[i], 0);
        }
    }
    static ushort Language(IntPtr hkl) { return (ushort)(hkl.ToInt64() & 0xffff); }
    static int LayoutIndex(IntPtr hkl)
    {
        for (int i = 0; i < Hkl.Length; i++) if (Hkl[i] != IntPtr.Zero && Hkl[i] == hkl) return i;
        for (int i = 0; i < LangIds.Length; i++) if (Language(hkl) == LangIds[i]) return i;
        return -1;
    }
    static uint InputThread(IntPtr app, out IntPtr focus)
    {
        focus = app;
        if (app == IntPtr.Zero || !IsWindow(app)) return 0;
        uint tid = GetWindowThreadProcessId(app, IntPtr.Zero);
        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
        if (GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero)
        { focus = gi.hwndFocus; tid = GetWindowThreadProcessId(focus, IntPtr.Zero); }
        return tid;
    }
    static IntPtr WindowLayout(IntPtr app)
    {
        IntPtr focus; uint tid = InputThread(app, out focus);
        if (tid == 0) return IntPtr.Zero; // GetKeyboardLayout(0) would describe this process instead.
        IntPtr hkl = GetKeyboardLayout(tid);
        if (hkl == IntPtr.Zero)
        {
            IntPtr ime = ImmGetDefaultIMEWnd(app);
            if (ime != IntPtr.Zero) hkl = GetKeyboardLayout(GetWindowThreadProcessId(ime, IntPtr.Zero));
        }
        return hkl;
    }
    sealed class PendingSwitch
    { public IntPtr Hkl; public int Index; public DateTime Until; }
    static readonly Dictionary<IntPtr, PendingSwitch> pendingSwitches = new Dictionary<IntPtr, PendingSwitch>();
    static int Current(IntPtr app)
    {
        IntPtr hkl = WindowLayout(app);
        PendingSwitch pending;
        if (pendingSwitches.TryGetValue(app, out pending))
        {
            if (hkl != pending.Hkl && DateTime.UtcNow < pending.Until) return pending.Index;
        }
        return LayoutIndex(hkl);
    }
    static IntPtr AppTarget()
    {
        RememberApp();
        return lastApp != IntPtr.Zero && IsWindow(lastApp) ? lastApp : GetForegroundWindow();
    }
    static void PerformAction(int action, IntPtr app)
    {
        if (!IsWindow(app)) return;
        ResolveLayouts();
        int current = Current(app);
        if (current > 0) lastLatin = current;
        int target = LayoutPlan.Target(current, lastLatin, action);
        var cls = new StringBuilder(64); GetClassName(app, cls, 64);
        Log("action " + action + " in " + cls + ": " + (current < 0 ? "?" : Codes[current]) + " -> " + Codes[target]);
        Switch(target, app);
    }
    static void Switch(int idx, IntPtr app)
    {
        if (app == IntPtr.Zero || !IsWindow(app)) return;
        ResolveLayouts();
        if (Hkl[idx] == IntPtr.Zero) { Log("layout unavailable: " + Codes[idx]); return; }
        int previous = Current(app); if (previous > 0) lastLatin = previous;
        IntPtr focus; uint tid = InputThread(app, out focus);
        if (tid == 0) return;
        // Absolute HKL requests, never a relative Windows toggle. Send only once per input thread;
        // DefWindowProc propagates WM_INPUTLANGCHANGE to children. No focus-restore timer can retarget it.
        bool posted = PostMessage(focus, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, Hkl[idx]);
        // Always the top-level window too (the original switcher did): Chromium / Electron focus children
        // (Chrome_RenderWidgetHostHWND) do not pass the request to DefWindowProc, so hotkeys did nothing there.
        if (focus != app) posted = PostMessage(app, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, Hkl[idx]) || posted;
        if (!posted) { Log("layout request failed: " + Codes[idx]); return; }
        pendingSwitches[app] = new PendingSwitch { Hkl = Hkl[idx], Index = idx, Until = DateTime.UtcNow.AddMilliseconds(750) };
        if (idx > 0) lastLatin = idx;
        UpdateIcon(idx);
        NotifyChanged(); // Every program switch signals immediately, even while the app processes its queue.
        ShowOsd(idx, app);
        // Signal again once the app has applied the request: the bar reads the actual thread HKL,
        // so an immediate event alone can race PostMessage. Never post the switch a second time.
        var timer = new System.Windows.Forms.Timer { Interval = 25 };
        PendingSwitch request = pendingSwitches[app];
        timer.Tick += (s, e) =>
        {
            PendingSwitch latest;
            bool superseded = !pendingSwitches.TryGetValue(app, out latest) || latest != request;
            bool applied = WindowLayout(app) == request.Hkl;
            if (!superseded && !applied && IsWindow(app) && DateTime.UtcNow < request.Until) return;
            timer.Stop(); timer.Dispose();
            if (superseded) return;
            pendingSwitches.Remove(app);
            if (applied) NotifyChanged();
            else Log("layout request not confirmed: " + Codes[idx]);
        };
        timer.Start();
    }

    static uint MonitorDpi(IntPtr app)
    {
        uint x, y;
        if (GetDpiForMonitor(MonitorFromWindow(app, 2 /*NEAREST*/), 0, out x, out y) == 0 && x > 0) return x;
        uint dpi = GetDpiForWindow(app); return dpi > 0 ? dpi : GetDpiForSystem();
    }
    static bool MouseButtonsDown()
    {
        foreach (int key in new[] { 1, 2, 4, 5, 6 }) if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    const string EventSddl = "D:(A;;0x1F0003;;;WD)S:(ML;;NW;;;LW)";
    static AutoResetEvent cycleEvent, changedEvent;
    static readonly ManualResetEvent runtimeStop = new ManualResetEvent(false);
    static Thread cycleThread;
    static WinEventProc winEventProc;
    static IntPtr showHook, foregroundHook;
    static System.Windows.Forms.Timer languageBarTimer;
    static readonly HashSet<string> hideLogged = new HashSet<string>();
    static AutoResetEvent SharedEvent(string name)
    {
        IntPtr sd; uint size;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(EventSddl, 1, out sd, out size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), lpSecurityDescriptor = sd };
            IntPtr handle = CreateEventW(ref sa, false, false, name);
            // ERROR_ALREADY_EXISTS is success: CreateEventW returns the existing event handle.
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), name);
            var ev = new AutoResetEvent(false);
            ev.SafeWaitHandle = new SafeWaitHandle(handle, true);
            return ev;
        }
        finally { LocalFree(sd); }
    }
    static void NotifyChanged()
    {
        if (changedEvent != null && !changedEvent.Set()) Log("LayoutChanged signal failed");
    }
    static void StartRuntime()
    {
        cycleEvent = SharedEvent(@"Local\fanatic.LayoutCycle");
        changedEvent = SharedEvent(@"Local\fanatic.LayoutChanged");
        cycleThread = new Thread(() =>
        {
            var waits = new WaitHandle[] { runtimeStop, cycleEvent };
            while (WaitHandle.WaitAny(waits) == 1)
                ui.Post(_ => { if (!runtimeStop.WaitOne(0)) PerformAction(LayoutPlan.Cycle, AppTarget()); }, null);
        }) { IsBackground = true, Name = "layout-cycle" };
        cycleThread.Start();
        winEventProc = (hook, ev, hwnd, obj, child, thread, time) =>
        {
            if (hwnd == IntPtr.Zero) return;
            if (ev == 3 /*EVENT_SYSTEM_FOREGROUND*/) { RememberApp(hwnd); return; }
            if (obj == 0 /*OBJID_WINDOW*/ && child == 0) HideLanguageBar(hwnd, "show");
        };
        foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, winEventProc, 0, 0, 0 /*OUTOFCONTEXT*/);
        showHook = SetWinEventHook(0x8002, 0x8002 /*EVENT_OBJECT_SHOW*/, IntPtr.Zero, winEventProc, 0, 0, 0);
        if (showHook == IntPtr.Zero || foregroundHook == IntPtr.Zero) Log("WinEvent hook failed: " + Marshal.GetLastWin32Error());
        HideLanguageBars("startup");
        languageBarTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        languageBarTimer.Tick += (s, e) => HideLanguageBars("safety timer");
        languageBarTimer.Start();
    }
    static void StopRuntime()
    {
        runtimeStop.Set();
        if (cycleThread != null) cycleThread.Join(1000);
        if (showHook != IntPtr.Zero) UnhookWinEvent(showHook);
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        if (languageBarTimer != null) languageBarTimer.Dispose();
        if (cycleEvent != null) cycleEvent.Dispose();
        if (changedEvent != null) changedEvent.Dispose();
    }
    static void HideLanguageBars(string reason)
    {
        for (IntPtr hwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "CiceroUIWndFrame", "TF_FloatingLangBar_WndTitle");
             hwnd != IntPtr.Zero; hwnd = FindWindowEx(IntPtr.Zero, hwnd, "CiceroUIWndFrame", "TF_FloatingLangBar_WndTitle"))
            HideLanguageBar(hwnd, reason);
    }
    static void HideLanguageBar(IntPtr hwnd, string reason)
    {
        if (!IsWindowVisible(hwnd)) return;
        var cls = new StringBuilder(128); GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != "CiceroUIWndFrame") return;
        var title = new StringBuilder(256); GetWindowText(hwnd, title, title.Capacity);
        if (title.ToString() != "TF_FloatingLangBar_WndTitle") return;
        ShowWindow(hwnd, 0 /*SW_HIDE*/);
        if (!IsWindowVisible(hwnd) && hideLogged.Add(reason)) Log("floating language bar hidden (" + reason + ")");
    }

    // Diagnostics run before the singleton, hooks, shared events, tray and settings loader.
    static int CommandLine(string[] args)
    {
        try
        {
            // winexe has no console of its own; redirected stdout is also supported.
            var output = Console.OpenStandardOutput();
            if (output == Stream.Null) { AttachConsole(unchecked((uint)-1)); output = Console.OpenStandardOutput(); }
            Console.SetOut(new StreamWriter(output) { AutoFlush = true });
            if (args.Length == 1 && args[0] == "--layouts")
            {
                ResolveLayouts();
                for (int i = 0; i < Codes.Length; i++) Console.WriteLine("{0}: HKL=0x{1:X16} LANG=0x{2:X4}", Codes[i], Hkl[i].ToInt64(), Language(Hkl[i]));
                IntPtr app = GetForegroundWindow(), hkl = WindowLayout(app); int index = LayoutIndex(hkl);
                Console.WriteLine("Foreground: HWND=0x{0:X} HKL=0x{1:X16} layout={2}", app.ToInt64(), hkl.ToInt64(), index < 0 ? "unknown" : Codes[index]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--plan")
            {
                Console.WriteLine("Start lastLatin Ctrl+Shift Alt+Shift Cycle");
                for (int current = 0; current < 3; current++) for (int latin = 1; latin <= 2; latin++)
                    Console.WriteLine("{0,-5} {1,-9} {2,-10} {3,-9} {4}", Codes[current], Codes[latin],
                        Codes[LayoutPlan.Target(current, latin, LayoutPlan.CtrlShift)], Codes[LayoutPlan.Target(current, latin, LayoutPlan.AltShift)], Codes[LayoutPlan.Target(current, latin, LayoutPlan.Cycle)]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--osd-shot")
            {
                float unit = 1.3f * MonitorDpi(GetForegroundWindow()) / 96f;
                Size size = BadgeGraphics.Size(unit); int gap = (int)Math.Ceiling(8 * unit);
                using (var bmp = new Bitmap(size.Width * 3 + gap * 4, size.Height + gap * 2))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    for (int i = 0; i < 3; i++)
                    {
                        var state = g.Save(); g.TranslateTransform(gap + i * (size.Width + gap), gap);
                        BadgeGraphics.Draw(g, Codes[i], Colors[i], unit); g.Restore(state);
                    }
                    bmp.Save(Path.GetFullPath(args[1]), ImageFormat.Png);
                    Console.WriteLine("OSD: {0} ({1}x{2}, DPI={3:0})", Path.GetFullPath(args[1]), bmp.Width, bmp.Height, unit * 32);
                }
                return 0;
            }
            Console.WriteLine("Usage: keyboard-switch.exe --layouts | --plan | --osd-shot <png>");
            return 2;
        }
        catch (Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
    }
}
