// Shared with elevated keyboard-switch: an ordinary .NET named event gets a medium integrity ACL and cannot cross this boundary.
using System;
using System.Runtime.InteropServices;
using System.Threading;

static partial class FanaticBar
{
    [StructLayout(LayoutKind.Sequential)] struct LayoutSecurity { public int Size; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string s, uint revision, out IntPtr sd, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateEventW(ref LayoutSecurity sa, bool manual, bool initial, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern bool SetEvent(IntPtr ev);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
    const uint LayoutAccess = 0x00100002;   // SYNCHRONIZE | EVENT_MODIFY_STATE
    static IntPtr layoutChangedEvent;
    static IntPtr CreateLayoutEvent(string name)
    {
        IntPtr existing = OpenEventW(LayoutAccess, false, name); if (existing != IntPtr.Zero) return existing;
        IntPtr sd; uint size;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;0x1F0003;;;WD)S:(ML;;NW;;;LW)", 1, out sd, out size)) return IntPtr.Zero;
        try
        {
            var sa = new LayoutSecurity { Size = Marshal.SizeOf(typeof(LayoutSecurity)), Descriptor = sd };
            IntPtr h = CreateEventW(ref sa, false, false, name);
            return h != IntPtr.Zero ? h : OpenEventW(LayoutAccess, false, name);
        }
        finally { LocalFree(sd); }
    }
    static void StartLayoutEvents()
    {
        layoutChangedEvent = CreateLayoutEvent(@"Local\fanatic.LayoutChanged");
        // Create the cycle event too when first. Close this bootstrap handle: an existing event on click then means the switcher owns it.
        IntPtr cycle = CreateLayoutEvent(@"Local\fanatic.LayoutCycle"); if (cycle != IntPtr.Zero) CloseHandle(cycle);
        if (layoutChangedEvent == IntPtr.Zero) { Log("layout event: " + Marshal.GetLastWin32Error()); return; }
        new Thread(() =>
        {
            while (WaitForSingleObject(layoutChangedEvent, 0xFFFFFFFF) == 0)
            {
                int[] delays = { 0, 40, 80, 180 };   // absolute deadlines: 0, 40, 120, 300 ms
                foreach (int delay in delays)
                {
                    if (delay != 0) Thread.Sleep(delay);
                    ui.Post(_ => { var b = big; if (b != null && !b.IsDisposed) b.LayoutSignal(); }, null);
                }
            }
        }) { IsBackground = true, Name = "layout-changed" }.Start();
    }
    static bool RequestLayoutCycle()
    {
        IntPtr h = OpenEventW(LayoutAccess, false, @"Local\fanatic.LayoutCycle");
        if (h == IntPtr.Zero) return false;
        try { return SetEvent(h); } finally { CloseHandle(h); }
    }
}
