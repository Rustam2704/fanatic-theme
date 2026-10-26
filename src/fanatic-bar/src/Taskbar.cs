// Black taskbar (owner, calib-TASK 4.2): ExplorerPatcher's Windows 10 taskbar has no colour setting, so its accent is set to an
// opaque #000000 gradient (SetWindowCompositionAttribute, what TranslucentTB does). Nothing is written to the registry: Explorer
// resets the accent on its own events (restart, theme/colour change), so it is checked every 2 s and re-applied only when lost.
// fanatic-bar.json { "taskbarBlack": false } turns it off; Explorer's own colour then returns at once (ACCENT_DISABLED).
using System;
using System.Runtime.InteropServices;

static partial class FanaticBar
{
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags, Color, Anim; }
    [StructLayout(LayoutKind.Sequential)] struct WinCompAttrData { public int Attr; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WinCompAttrData d);
    [DllImport("user32.dll")] static extern int GetWindowCompositionAttribute(IntPtr h, ref WinCompAttrData d);
    const int WCA_ACCENT_POLICY = 19, ACCENT_DISABLED = 0, ACCENT_ENABLE_GRADIENT = 1, Black = unchecked((int)0xFF000000);

    static AccentPolicy Accent(IntPtr h, AccentPolicy? set)
    {
        var a = set ?? new AccentPolicy(); int size = Marshal.SizeOf(typeof(AccentPolicy)); IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(a, p, false); var d = new WinCompAttrData { Attr = WCA_ACCENT_POLICY, Data = p, Size = size };
            if (set.HasValue) SetWindowCompositionAttribute(h, ref d); else { GetWindowCompositionAttribute(h, ref d); a = (AccentPolicy)Marshal.PtrToStructure(p, typeof(AccentPolicy)); }
            return a;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // Every taskbar (main + one per extra monitor).
    static void ForEachTaskbar(Action<IntPtr> f)
    {
        IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); if (h != IntPtr.Zero) f(h);
        IntPtr s = IntPtr.Zero;
        while ((s = FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) f(s);
    }

    static void KeepTaskbarBlack()
    {
        ForEachTaskbar(h =>
        {
            var a = Accent(h, null);
            if (a.State != ACCENT_ENABLE_GRADIENT || a.Color != Black)
                Accent(h, new AccentPolicy { State = ACCENT_ENABLE_GRADIENT, Flags = 2, Color = Black });
        });
    }
    static void ReleaseTaskbar() { ForEachTaskbar(h => Accent(h, new AccentPolicy { State = ACCENT_DISABLED })); }
}
