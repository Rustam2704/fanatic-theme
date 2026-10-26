// Sound flyout (port of menus-src\audiomenu.py for Windows): output and input sliders with mute buttons, the output device
// and the mixer. Every change applies at once. Core Audio: IMMDeviceEnumerator / IAudioEndpointVolume; the default device is
// switched with IPolicyConfig (undocumented, what the Windows sound settings use). Not ported: key sounds and the PipeWire
// echo canceller (Linux-only).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static partial class FanaticBar
{
    const int ERender = 0, ECapture = 1;
    sealed class AudioDev { public string Id, Name; }

    // One enumerator per thread (COM objects stay in the apartment that made them; pool threads are reused, so it is made rarely).
    [ThreadStatic] static IMMDeviceEnumerator threadEnum;
    static IMMDeviceEnumerator NewEnum() { return threadEnum ?? (threadEnum = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom()); }
    static string DefaultId(int flow)
    {
        IMMDevice d = null;
        try { string id; return NewEnum().GetDefaultAudioEndpoint(flow, 1, out d) == 0 && d != null && d.GetId(out id) == 0 ? id : null; }
        catch { return null; }
        finally { if (d != null) Marshal.ReleaseComObject(d); }
    }
    static List<AudioDev> Devices(int flow)
    {
        var list = new List<AudioDev>(); IMMDeviceCollection c = null;
        try
        {
            if (NewEnum().EnumAudioEndpoints(flow, 1 /*DEVICE_STATE_ACTIVE*/, out c) != 0 || c == null) return list;
            uint n; c.GetCount(out n);
            for (uint i = 0; i < n; i++)
            {
                IMMDevice d; if (c.Item(i, out d) != 0 || d == null) continue;
                try
                {
                    string id; d.GetId(out id); IPropertyStore ps; string name = id;
                    if (d.OpenPropertyStore(0 /*STGM_READ*/, out ps) == 0 && ps != null)
                    {
                        var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // PKEY_Device_FriendlyName
                        PropVariant pv; if (ps.GetValue(ref key, out pv) == 0) { if (pv.vt == 31 /*VT_LPWSTR*/) name = Marshal.PtrToStringUni(pv.p); PropVariantClear(ref pv); }
                        Marshal.ReleaseComObject(ps);
                    }
                    list.Add(new AudioDev { Id = id, Name = name });
                }
                finally { Marshal.ReleaseComObject(d); }
            }
        }
        catch (Exception ex) { threadEnum = null; Log("audio devices: " + ex.Message); }
        finally { if (c != null) Marshal.ReleaseComObject(c); }
        return list;
    }
    // Volume of the default endpoint: f(endpoint) runs with it; false when there is none.
    static bool WithEndpoint(int flow, Action<IAudioEndpointVolume> f)
    {
        IMMDevice d = null; object o = null;
        try
        {
            if (NewEnum().GetDefaultAudioEndpoint(flow, 1, out d) != 0 || d == null) return false;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (d.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return false;
            f((IAudioEndpointVolume)o); return true;
        }
        catch (Exception ex) { threadEnum = null; Log("audio endpoint: " + ex.Message); return false; }
        finally { if (o != null) Marshal.ReleaseComObject(o); if (d != null) Marshal.ReleaseComObject(d); }
    }
    static void SetDefaultDevice(string id)
    {
        object pc = null;
        try { pc = new PolicyConfigClient(); var p = (IPolicyConfig)pc; for (int role = 0; role < 3; role++) p.SetDefaultEndpoint(id, role); }
        catch (Exception ex) { Log("audio default: " + ex.Message); }
        finally { if (pc != null) Marshal.ReleaseComObject(pc); }
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    // One slider row of audiomenu.py: icon, long slider with its value, a Mute toggle. Writes at most every 60 ms.
    sealed class VolumeRow : Control
    {
        readonly int flow; readonly Func<int, bool, Bitmap> icon; readonly SkyButton mute;
        int value; bool muted, known, drag; System.Windows.Forms.Timer pending;
        public VolumeRow(int flow, Func<int, bool, Bitmap> icon)
        {
            this.flow = flow; this.icon = icon; DoubleBuffered = true; BackColor = Background; Font = SkyFont(11, false);
            mute = new SkyButton(Strings.T("audio.mute"), null); mute.Size = mute.Preferred();
            mute.Click += (s, e) => { if (!known) return; muted = !muted; mute.Checked = muted; mute.Invalidate(); Invalidate(); bool m = muted; ThreadPool.QueueUserWorkItem(_ => { WithEndpoint(flow, ep => { Guid g = Guid.Empty; ep.SetMute(m, ref g); }); PollAudio(); }); };
            Controls.Add(mute);
            int h = Math.Max(Px(48), mute.Height);
            Size = new Size(Px(48) + Px(14) + Px(320) + Px(14) + TextRenderer.MeasureText("100", Font).Width + Px(14) + mute.Width, h);
            mute.Location = new Point(Width - mute.Width, (h - mute.Height) / 2);
            float lv = 0; bool mu = false;
            known = WithEndpoint(flow, ep => { ep.GetMasterVolumeLevelScalar(out lv); ep.GetMute(out mu); });
            value = (int)Math.Round(lv * 100); muted = mu; mute.Checked = muted; mute.Enabled = known;
        }
        Rectangle Track() { int x0 = Px(48) + Px(14); return new Rectangle(x0, (Height - Px(8)) / 2, Px(320), Px(8)); }
        void SetFromX(int x)
        {
            var t = Track(); int v = Math.Max(0, Math.Min(100, (int)Math.Round((x - t.X) * 100.0 / t.Width)));
            if (v == value) return; value = v; Invalidate();
            if (pending == null) { pending = new System.Windows.Forms.Timer { Interval = 60 }; pending.Tick += (s, e) => { pending.Stop(); Write(); }; }
            if (!pending.Enabled) pending.Start();
        }
        // One writer at a time, always the latest value: a fast drag never applies an older value after a newer one.
        int want = -1, writing;
        void Write()
        {
            Interlocked.Exchange(ref want, value);
            if (Interlocked.Exchange(ref writing, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int v;
                    while ((v = Interlocked.Exchange(ref want, -1)) >= 0)
                        WithEndpoint(flow, ep => { Guid g = Guid.Empty; ep.SetMasterVolumeLevelScalar(v / 100f, ref g); });
                    PollAudio();
                }
                finally { Interlocked.Exchange(ref writing, 0); }
                if (Interlocked.CompareExchange(ref want, -1, -1) >= 0) try { BeginInvoke(new Action(Write)); } catch (InvalidOperationException) { }   // a value set between the loop and the reset
            });
        }
        protected override void Dispose(bool d) { if (d && pending != null) pending.Dispose(); base.Dispose(d); }
        protected override void OnMouseDown(MouseEventArgs e) { if (known && e.Button == MouseButtons.Left && e.X < Track().Right + Px(8)) { drag = true; SetFromX(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { if (known) { var t = Track(); SetFromX(t.X + (Math.Max(0, Math.Min(100, value + (e.Delta > 0 ? 2 : -2)))) * t.Width / 100); } base.OnMouseWheel(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = icon(known ? value : -1, muted)) g.DrawImage(b, 0, (Height - Px(48)) / 2, Px(48), Px(48));
            var t = Track();
            using (var p = Rounded(t, t.Height / 2f)) using (var b = new SolidBrush(SkySurface)) using (var pen = new Pen(SkyBorder)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            float fx = t.X + value * t.Width / 100f;
            if (known && value > 0) using (var p = Rounded(new RectangleF(t.X, t.Y, fx - t.X, t.Height), t.Height / 2f)) using (var b = new SolidBrush(muted ? Muted : Light)) g.FillPath(b, p);
            float k = Px(22); using (var b = new SolidBrush(known ? Foreground : Muted)) g.FillEllipse(b, fx - k / 2, (Height - k) / 2, k, k);
            TextRenderer.DrawText(g, known ? value.ToString() : "—", Font, new Rectangle(t.Right + Px(14), 0, mute.Left - t.Right - Px(14), Height), Foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Microphone icon for the input row (Segoe Fluent Icons), muted / no device in the muted colour.
    static Bitmap DrawMic(int size, int vol, bool muted)
    {
        Graphics g; var bmp = NewIcon(size, out g);
        using (g) using (var f = new Font(IconFont, size * 0.62f, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, muted ? "" : "", f, new Rectangle(0, 0, size, size), muted || vol < 0 ? Muted : Foreground, Background, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return bmp;
    }

    sealed class AudioPopup : SkyPopup
    {
        protected override List<Control> Build()
        {
            var rows = new List<Control>();
            rows.Add(RowLabel(Strings.T("audio.output")));
            rows.Add(new VolumeRow(ERender, (v, m) => DrawSpeaker(Px(48), v, m)));
            if (DefaultId(ECapture) != null) { rows.Add(RowLabel(Strings.T("audio.microphone"))); rows.Add(new VolumeRow(ECapture, (v, m) => DrawMic(Px(48), v, m))); }
            var devs = Devices(ERender); string def = DefaultId(ERender);
            if (devs.Count > 1)
            {
                rows.Add(RowLabel(Strings.T("audio.output_device")));
                foreach (var d in devs)
                {
                    string id = d.Id; var b = Button(d.Name, "", () => { SetDefaultDevice(id); PollAudio(); Relayout(); });
                    b.Checked = id == def; rows.Add(b);
                }
            }
            rows.Add(Button(Strings.T("audio.mixer_btn"), "", () => { Close(); Run(Path.Combine(Environment.SystemDirectory, "SndVol.exe"), ""); }));
            return rows;
        }
    }
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}
[StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public int pid; }
[StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p, p2; }
[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}
[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr defPeriod, IntPtr minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}
