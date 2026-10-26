// 60-second probe: logs WmiMonitorBrightnessEvent and non-character key events (no letters/digits/punctuation),
// to see what the Fn brightness keys produce when the brightness is already at 0.
using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class FnProbe
{
    delegate IntPtr HookProc(int c, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [StructLayout(LayoutKind.Sequential)] struct K { public int vk, scan, flags, time; public IntPtr extra; }
    static HookProc proc; static StreamWriter log;

    static void Main(string[] a)
    {
        log = new StreamWriter(a[0], false) { AutoFlush = true };
        var w = new ManagementEventWatcher(new ManagementScope(@"root\wmi"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
        w.EventArrived += (s, e) => log.WriteLine("{0:HH:mm:ss.fff} WMI brightness={1}", DateTime.Now, e.NewEvent["Brightness"]);
        w.Start();
        proc = (c, wp, lp) =>
        {
            var k = (K)Marshal.PtrToStructure(lp, typeof(K));
            bool ch = (k.vk >= 0x30 && k.vk <= 0x5A) || (k.vk >= 0xBA && k.vk <= 0xE2) || k.vk == 0x20;
            if (!ch) log.WriteLine("{0:HH:mm:ss.fff} key msg=0x{1:X} vk=0x{2:X2} scan=0x{3:X2} flags=0x{4:X}", DateTime.Now, (int)wp, k.vk, k.scan, k.flags);
            return CallNextHookEx(IntPtr.Zero, c, wp, lp);
        };
        SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
        var t = new Timer { Interval = 60000 }; t.Tick += (s, e) => Application.Exit(); t.Start();
        Application.Run();
    }
}
