using Windows.Media.Devices;

namespace CameraProbe;

/// <summary>
/// UVC property access through MediaCapture's VideoDeviceController, so every request goes through
/// Windows Camera Frame Server instead of opening a second DirectShow handle to the device.
/// </summary>
public static class KsControls
{
    public static readonly Guid VideoProcAmpSet = new("C6E13360-30AC-11d0-A18C-00A0C9118956");
    public static readonly Guid CameraControlSet = new("C6E13370-30AC-11d0-A18C-00A0C9118956");

    private const uint KsPropertyTypeGet = 0x1;
    private const uint KsPropertyTypeSet = 0x2;

    // KSPROPERTY_VIDEOPROCAMP_S / KSPROPERTY_CAMERACONTROL_S: KSPROPERTY header (GUID Set, ULONG Id, ULONG Flags),
    // LONG Value, ULONG Flags, ULONG Capabilities; 36 bytes padded to 40 by the header's 8-byte alignment.
    // Frame Server rejects anything but the full structure for both the request and the reply.
    private const int StructSize = 40;

    private static byte[] Header(Guid set, uint id, uint flags)
    {
        var b = new byte[StructSize];
        set.ToByteArray().CopyTo(b, 0);
        BitConverter.GetBytes(id).CopyTo(b, 16);
        BitConverter.GetBytes(flags).CopyTo(b, 20);
        return b;
    }

    public static (int Value, int Flags)? Get(VideoDeviceController vdc, Guid set, uint id)
    {
        var result = vdc.GetDevicePropertyByExtendedId(Header(set, id, KsPropertyTypeGet), StructSize);
        if (result.Status != VideoDeviceControllerGetDevicePropertyStatus.Success || result.Value is not byte[] data)
            return null;
        return (BitConverter.ToInt32(data, 24), BitConverter.ToInt32(data, 28));
    }

    public static VideoDeviceControllerSetDevicePropertyStatus Set(VideoDeviceController vdc, Guid set, uint id, int value, int flags)
    {
        var s = Header(set, id, KsPropertyTypeSet);
        BitConverter.GetBytes(value).CopyTo(s, 24);
        BitConverter.GetBytes(flags).CopyTo(s, 28);
        return vdc.SetDevicePropertyByExtendedId(s, s);
    }
}

public static class KsDiagnostics
{
    /// <summary>Tries the plausible request layouts for extended-id property access and reports each status.</summary>
    public static void Run(VideoDeviceController vdc)
    {
        foreach (var (name, set, id) in new[] { ("Exposure", KsControls.CameraControlSet, 4u), ("Gain", KsControls.VideoProcAmpSet, 9u), ("Brightness", KsControls.VideoProcAmpSet, 0u) })
        {
            foreach (var idLen in new[] { 24, 40 })
            foreach (uint? max in new uint?[] { 40u, 128u, 1024u, 65536u })
            {
                var req = new byte[idLen];
                set.ToByteArray().CopyTo(req, 0);
                BitConverter.GetBytes(id).CopyTo(req, 16);
                BitConverter.GetBytes(1u).CopyTo(req, 20); // KSPROPERTY_TYPE_GET
                try
                {
                    var r = vdc.GetDevicePropertyByExtendedId(req, max);
                    var bytes = r.Value as byte[];
                    Console.WriteLine($"  GET {name,-10} id {idLen}B max {max?.ToString() ?? "null",-4}: {r.Status,-22} {(bytes is null ? r.Value?.GetType().Name ?? "-" : $"{bytes.Length}B {BitConverter.ToString(bytes)}")}");
                }
                catch (Exception ex) { Console.WriteLine($"  GET {name,-10} id {idLen}B max {max?.ToString() ?? "null",-4}: threw {ex.GetType().Name} {ex.Message.Split('\n')[0]}"); }
            }
        }
    }
}
