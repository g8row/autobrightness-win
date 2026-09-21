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

    /// <summary>KSPROPERTY header: GUID Set, ULONG Id, ULONG Flags.</summary>
    private static byte[] Header(Guid set, uint id, uint flags)
    {
        var b = new byte[24];
        set.ToByteArray().CopyTo(b, 0);
        BitConverter.GetBytes(id).CopyTo(b, 16);
        BitConverter.GetBytes(flags).CopyTo(b, 20);
        return b;
    }

    /// <summary>
    /// Reads a KSPROPERTY_VIDEOPROCAMP_S / KSPROPERTY_CAMERACONTROL_S value. Both end in
    /// LONG Value, ULONG Flags, ULONG Capabilities; the payload may or may not repeat the 24-byte header.
    /// </summary>
    public static (int Value, int Flags, int Raw)? Get(VideoDeviceController vdc, Guid set, uint id)
    {
        var result = vdc.GetDevicePropertyByExtendedId(Header(set, id, KsPropertyTypeGet), 64u);
        if (result.Status != VideoDeviceControllerGetDevicePropertyStatus.Success || result.Value is not byte[] data)
            return null;
        var offset = data.Length >= 36 ? 24 : 0;
        return (BitConverter.ToInt32(data, offset), BitConverter.ToInt32(data, offset + 4), data.Length);
    }

    public static VideoDeviceControllerSetDevicePropertyStatus Set(VideoDeviceController vdc, Guid set, uint id, int value, int flags)
    {
        // Full KSPROPERTY_*_S structure: header, Value, Flags, Capabilities.
        var payload = new byte[36];
        Header(set, id, KsPropertyTypeSet).CopyTo(payload, 0);
        BitConverter.GetBytes(value).CopyTo(payload, 24);
        BitConverter.GetBytes(flags).CopyTo(payload, 28);
        return vdc.SetDevicePropertyByExtendedId(Header(set, id, KsPropertyTypeSet), payload);
    }
}
