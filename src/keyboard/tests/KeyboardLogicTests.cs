// Offline checks: no production hook, tray, window, layout switch, or production event is started.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

static class KeyboardLogicTests
{
    static int assertions;
    static void Equal(int expected, int actual, string label)
    {
        assertions++;
        if (actual != expected) throw new Exception(label + ": expected " + expected + ", got " + actual);
    }
    static int Gesture(PureChord chord, params int[] transitions)
    {
        int result = 0;
        foreach (int key in transitions) result += chord.Key(Math.Abs(key), key > 0, false);
        return result;
    }
    static void Main(string[] args)
    {
        for (int shift = 0xA0; shift <= 0xA1; shift++)
        for (int ctrl = 0xA2; ctrl <= 0xA3; ctrl++)
        {
            foreach (bool reverse in new[] { false, true })
            foreach (bool releaseReverse in new[] { false, true })
            {
                int first = reverse ? shift : ctrl, second = reverse ? ctrl : shift;
                int upFirst = releaseReverse ? second : first, upSecond = releaseReverse ? first : second;
                Equal(1, Gesture(new PureChord(), first, second, -upFirst, -upSecond), "Ctrl+Shift order/release");
                Equal(1, Gesture(new PureChord(), first, second, second, second, -upFirst, -upSecond), "modifier auto-repeat");
                Equal(0, Gesture(new PureChord(), first, second, 0x41, -0x41, -upFirst, -upSecond), "typing cancels");
                Equal(0, Gesture(new PureChord(), 0x41, first, second, -0x41, -upFirst, -upSecond), "preheld key cancels");
                var c = new PureChord(); Gesture(c, first, second); c.Interrupt();
                Equal(0, Gesture(c, -upFirst, -upSecond), "mouse cancels");
            }
        }
        for (int shift = 0xA0; shift <= 0xA1; shift++)
        {
            Equal(2, Gesture(new PureChord(), 0xA4, shift, -shift, -0xA4), "left Alt+Shift");
            Equal(2, Gesture(new PureChord(), shift, 0xA4, -shift, -0xA4), "left Alt last");
            Equal(0, Gesture(new PureChord(), 0xA5, shift, -shift, -0xA5), "AltGr untouched");
            Equal(0, Gesture(new PureChord(), 0xA2, 0xA5, shift, -0xA5, -shift, -0xA2), "AltGr synthetic Ctrl");
            Equal(0, Gesture(new PureChord(), 0xA2, shift, 0xA4, -0xA4, -shift, -0xA2), "third modifier poisons chord");
        }
        Equal(1, Gesture(new PureChord(), 0xA2, 0xA0, -0xA0, 0xA0, -0xA0, -0xA2), "no second switch while Ctrl held");
        Equal(2, Gesture(new PureChord(), 0xA2, 0xA0, -0xA0, -0xA2, 0xA2, 0xA0, -0xA2, -0xA0), "fresh chord re-arms");
        var mouse = new PureChord(); mouse.Key(0xA2, true, true); mouse.Key(0xA0, true, true);
        Equal(0, Gesture(mouse, -0xA2, -0xA0), "mouse already held");
        var recovered = new PureChord(); Gesture(recovered, 0xA2, 0xA0);
        recovered.Reconcile(k => k == 0xA2);
        Equal(0, Gesture(recovered, 0xA0, -0xA0, -0xA2), "lost release cancels gesture");
        Equal(1, Gesture(recovered, 0xA2, 0xA0, -0xA2, -0xA0), "recovers after lost release");
        Equal(0, Gesture(new PureChord(), -0xA2, -0xA0), "orphan releases ignored");

        int[,] expected = { { 1, 1, 1 }, { 2, 2, 1 }, { 2, 0, 2 }, { 2, 0, 2 }, { 1, 0, 0 }, { 1, 0, 0 } };
        for (int start = 0; start < 3; start++) for (int latin = 1; latin <= 2; latin++) for (int action = 1; action <= 3; action++)
            Equal(expected[start * 2 + latin - 1, action - 1], LayoutPlan.Target(start, latin, action), "switch table");

        // Exercise the actual P/Invoke event factory in the built executable, with a unique test name.
        // Opening the same name twice proves ERROR_ALREADY_EXISTS keeps the returned handle;
        // one successful wait followed by a timeout proves auto-reset semantics.
        Assembly assembly = Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0]));
        MethodInfo factory = assembly.GetType("KeyboardSwitch").GetMethod("SharedEvent", BindingFlags.NonPublic | BindingFlags.Static);
        string name = @"Local\fanatic.KeyboardSwitch.Tests." + Guid.NewGuid().ToString("N");
        using (var first = (AutoResetEvent)factory.Invoke(null, new object[] { name }))
        using (var second = (AutoResetEvent)factory.Invoke(null, new object[] { name }))
        {
            Equal(0, second.WaitOne(0) ? 1 : 0, "event initially reset");
            first.Set(); Equal(1, second.WaitOne(0) ? 1 : 0, "existing event shares signal");
            Equal(0, first.WaitOne(0) ? 1 : 0, "event auto resets");
            uint needed;
            GetKernelObjectSecurity(first.SafeWaitHandle.DangerousGetHandle(), 4 | 0x10, null, 0, out needed);
            byte[] sd = new byte[needed];
            if (!GetKernelObjectSecurity(first.SafeWaitHandle.DangerousGetHandle(), 4 | 0x10, sd, needed, out needed)) throw new System.ComponentModel.Win32Exception();
            IntPtr sddl; uint length;
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, 4 | 0x10, out sddl, out length)) throw new System.ComponentModel.Win32Exception();
            try
            {
                string actual = Marshal.PtrToStringUni(sddl);
                Equal(1, actual.Contains("(A;;0x1f0003;;;WD)") ? 1 : 0, "world event rights");
                Equal(1, actual.Contains("(ML;;NW;;;LW)") ? 1 : 0, "low integrity label");
                Console.WriteLine("Test event security: " + actual);
            }
            finally { LocalFree(sddl); }
        }
        Console.WriteLine("PASS: " + assertions + " assertions (chords, switching table, shared auto-reset event and SDDL).");
    }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetKernelObjectSecurity(IntPtr handle, uint info, byte[] sd, uint length, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(byte[] sd, uint revision, uint info, out IntPtr sddl, out uint length);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
}
