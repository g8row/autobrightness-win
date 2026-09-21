using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CameraProbe;

/// <summary>UVC property access through the DirectShow IAMCameraControl / IAMVideoProcAmp interfaces.</summary>
public sealed class UvcDevice : IDisposable
{
    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum { [PreserveSig] int CreateClassEnumerator([In] ref Guid cat, out IEnumMoniker? e, int flags); }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, out object val, IntPtr log);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object val);
    }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int p, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int p, int v, int flags);
        [PreserveSig] int Get(int p, out int v, out int flags);
    }

    [ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMVideoProcAmp
    {
        [PreserveSig] int GetRange(int p, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int p, int v, int flags);
        [PreserveSig] int Get(int p, out int v, out int flags);
    }

    private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid IBaseFilter = new("56a86895-0ad4-11ce-b03a-0020af0ba770");

    private object _filter;
    private readonly IAMCameraControl? _cam;
    private readonly IAMVideoProcAmp? _amp;

    public string Name { get; }
    public string DevicePath { get; }
    public bool HasCameraControl => _cam is not null;
    public bool HasVideoProcAmp => _amp is not null;

    private UvcDevice(string name, string path, object filter)
    {
        Name = name;
        DevicePath = path;
        _filter = filter;
        _cam = filter as IAMCameraControl;
        _amp = filter as IAMVideoProcAmp;
    }

    /// <summary>Enumerates DirectShow video input devices; each returned device holds a bound filter.</summary>
    public static List<UvcDevice> Enumerate()
    {
        var result = new List<UvcDevice>();
        var devEnum = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(SystemDeviceEnum)!)!;
        var cat = VideoInputCategory;
        if (devEnum.CreateClassEnumerator(ref cat, out var monikers, 0) != 0 || monikers is null) return result;

        var m = new IMoniker[1];
        while (monikers.Next(1, m, IntPtr.Zero) == 0)
        {
            var bagId = typeof(IPropertyBag).GUID;
            m[0].BindToStorage(null!, null, ref bagId, out var bagObj);
            var bag = (IPropertyBag)bagObj;
            bag.Read("FriendlyName", out var name, IntPtr.Zero);
            bag.Read("DevicePath", out var path, IntPtr.Zero);
            var filterId = IBaseFilter;
            try
            {
                m[0].BindToObject(null!, null, ref filterId, out var filter);
                result.Add(new UvcDevice(name as string ?? "?", path as string ?? "", filter));
            }
            catch (COMException) { /* device busy or unavailable */ }
        }
        return result;
    }

    /// <summary>Key shared by DirectShow paths and WinRT device ids: everything before the interface class GUID.</summary>
    public static string InstanceKey(string path)
    {
        var i = path.IndexOf("#{", StringComparison.Ordinal);
        return (i > 0 ? path[..i] : path).ToLowerInvariant();
    }

    public UvcRange? GetRange(UvcProp p)
    {
        int hr, min, max, step, def, caps;
        if (p.IsCameraControl) { if (_cam is null) return null; hr = _cam.GetRange(p.Id, out min, out max, out step, out def, out caps); }
        else { if (_amp is null) return null; hr = _amp.GetRange(p.Id, out min, out max, out step, out def, out caps); }
        return hr == 0 ? new UvcRange(min, max, step, def, (UvcFlags)caps) : null;
    }

    public (int Value, UvcFlags Flags)? Get(UvcProp p)
    {
        int hr, v, f;
        if (p.IsCameraControl) { if (_cam is null) return null; hr = _cam.Get(p.Id, out v, out f); }
        else { if (_amp is null) return null; hr = _amp.Get(p.Id, out v, out f); }
        return hr == 0 ? (v, (UvcFlags)f) : null;
    }

    public int Set(UvcProp p, int value, UvcFlags flags = UvcFlags.Manual)
    {
        if (p.IsCameraControl) return _cam?.Set(p.Id, value, (int)flags) ?? -1;
        return _amp?.Set(p.Id, value, (int)flags) ?? -1;
    }

    public void Dispose()
    {
        if (_filter is not null) Marshal.ReleaseComObject(_filter);
        _filter = null!;
    }
}

[Flags]
public enum UvcFlags { None = 0, Auto = 1, Manual = 2 }

public readonly record struct UvcRange(int Min, int Max, int Step, int Default, UvcFlags Caps);

public sealed record UvcProp(string Name, bool IsCameraControl, int Id)
{
    // KSPROPERTY_CAMERACONTROL_* and KSPROPERTY_VIDEOPROCAMP_* ids
    public static readonly UvcProp Pan = new("Pan", true, 0);
    public static readonly UvcProp Tilt = new("Tilt", true, 1);
    public static readonly UvcProp Roll = new("Roll", true, 2);
    public static readonly UvcProp Zoom = new("Zoom", true, 3);
    public static readonly UvcProp Exposure = new("Exposure", true, 4);
    public static readonly UvcProp Iris = new("Iris", true, 5);
    public static readonly UvcProp Focus = new("Focus", true, 6);
    public static readonly UvcProp AutoExposurePriority = new("AutoExposurePriority", true, 19);

    public static readonly UvcProp Brightness = new("Brightness", false, 0);
    public static readonly UvcProp Contrast = new("Contrast", false, 1);
    public static readonly UvcProp Hue = new("Hue", false, 2);
    public static readonly UvcProp Saturation = new("Saturation", false, 3);
    public static readonly UvcProp Sharpness = new("Sharpness", false, 4);
    public static readonly UvcProp Gamma = new("Gamma", false, 5);
    public static readonly UvcProp WhiteBalance = new("WhiteBalance", false, 7);
    public static readonly UvcProp BacklightCompensation = new("BacklightCompensation", false, 8);
    public static readonly UvcProp Gain = new("Gain", false, 9);
    public static readonly UvcProp PowerlineFrequency = new("PowerlineFrequency", false, 13);

    public static readonly UvcProp[] All =
    [
        Pan, Tilt, Roll, Zoom, Exposure, Iris, Focus, AutoExposurePriority,
        Brightness, Contrast, Hue, Saturation, Sharpness, Gamma, WhiteBalance, BacklightCompensation, Gain, PowerlineFrequency,
    ];
}
