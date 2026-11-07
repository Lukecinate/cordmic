using System.Runtime.InteropServices;

namespace CordMic.App;

/// <summary>
/// The slice of WASAPI CordMic actually uses (MMDevice enumeration, IAudioClient3 shared-mode
/// periods, event-driven render). Replaces NAudio in 0.5.0.
/// </summary>
internal static class Wasapi
{
    public const int AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED = unchecked((int)0x88890019);
    public const uint StreamEventCallback = 0x00040000;
    public const uint StreamAutoConvertPcm = 0x80000000;
    public const uint StreamSrcDefaultQuality = 0x08000000;
    public const uint BufferSilent = 0x2;
    public const int ShareShared = 0, ShareExclusive = 1;
    public const int Render = 0, Capture = 1, AllDevices = 2;
    public const int StgmRead = 0, StgmReadWrite = 1;
    public const int DeviceActive = 0x1;
    public const int ClsCtxAll = 23;

    public static readonly Guid IidAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    public static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly PropertyKey ManufacturerKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 13);
    public static readonly PropertyKey DeviceFormatKey = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
    public static readonly PropertyKey OemFormatKey = new(new Guid("e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04"), 3);
    public static readonly PropertyKey DisableSysFxKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 5);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant p);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr p);

    public static void Check(int hr, string what)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static MMDeviceEnumeratorCom CreateEnumerator() =>
        (MMDeviceEnumeratorCom)Activator.CreateInstance(typeof(MMDeviceEnumeratorCom))!;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
    public PropertyKey(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr ptr;
    [FieldOffset(8)] public uint ulVal;
    [FieldOffset(8)] public uint blobSize;
    [FieldOffset(16)] public IntPtr blobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;

    public static WaveFormatEx Pcm(int rate, int channels)
    {
        int block = channels * 2;
        return new WaveFormatEx
        {
            FormatTag = 1, Channels = (ushort)channels, SamplesPerSec = (uint)rate,
            AvgBytesPerSec = (uint)(rate * block), BlockAlign = (ushort)block, BitsPerSample = 16, Size = 0,
        };
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

/// <summary>IAudioClient3, vtable order from audioclient.h (IAudioClient, then 2, then 3).</summary>
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient3
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    [PreserveSig] int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(IntPtr properties);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodFrames, out uint fundamentalPeriodFrames, out uint minPeriodFrames, out uint maxPeriodFrames);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);
    [PreserveSig] int InitializeSharedAudioStream(uint flags, uint periodFrames, IntPtr format, IntPtr session);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class WasapiNotifications : IMMNotificationClient
{
    public event Action? Changed;
    public void OnDeviceStateChanged(string deviceId, int newState) => Changed?.Invoke();
    public void OnDeviceAdded(string deviceId) => Changed?.Invoke();
    public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();
    public void OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
}

internal static class WasapiDevices
{
    public static string IdOf(IMMDevice dev)
    {
        Wasapi.Check(dev.GetId(out var p), "GetId");
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Wasapi.CoTaskMemFree(p); }
    }

    public static string NameOf(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) < 0) return IdOf(dev);
        try
        {
            var key = Wasapi.FriendlyNameKey;
            if (store.GetValue(ref key, out var v) < 0) return IdOf(dev);
            try
            {
                if (v.vt == 31 && v.ptr != IntPtr.Zero) return Marshal.PtrToStringUni(v.ptr) ?? "";
                return "";
            }
            finally { Wasapi.PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    public static List<RenderDevice> ListRender(IMMDeviceEnumerator en)
    {
        var list = new List<RenderDevice>();
        Wasapi.Check(en.EnumAudioEndpoints(Wasapi.Render, Wasapi.DeviceActive, out var col), "EnumAudioEndpoints");
        try
        {
            Wasapi.Check(col.GetCount(out int n), "GetCount");
            for (int i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) < 0) continue;
                try { list.Add(new RenderDevice(IdOf(dev), NameOf(dev))); }
                catch (Exception) { }
                finally { Marshal.ReleaseComObject(dev); }
            }
        }
        finally { Marshal.ReleaseComObject(col); }
        return list;
    }
}
