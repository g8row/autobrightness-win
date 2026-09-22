using Windows.Media.Devices;

namespace AutoBrightness.Camera;

/// <summary>
/// UVC controls through MediaCapture's VideoDeviceController. Every request goes through Windows Camera
/// Frame Server; DirectShow's IAMCameraControl opens a second handle to the device and was observed to
/// wedge a Logitech 046D:0819 until it was replugged.
/// </summary>
internal sealed class CameraControls(VideoDeviceController vdc)
{
    private static readonly Guid VideoProcAmpSet = new("C6E13360-30AC-11d0-A18C-00A0C9118956");
    private const uint GainId = 9;

    private const uint KsGet = 0x1;
    private const uint KsSet = 0x2;
    private const uint KsBasicSupport = 0x200;

    // KSPROPERTY_VIDEOPROCAMP_S: KSPROPERTY header (GUID Set, ULONG Id, ULONG Flags), LONG Value, ULONG Flags,
    // ULONG Capabilities; 36 bytes padded to 40 by the header's 8-byte alignment. Frame Server rejects
    // anything but the full structure for both the request and the reply.
    private const int PropertySize = 40;
    private const int FlagManual = 2;

    public MediaDeviceControlCapabilities ExposureCaps => vdc.Exposure.Capabilities;
    public bool ExposureSupported => ExposureCaps.Supported && ExposureCaps.Max > ExposureCaps.Min;

    public bool SetExposure(int log2Seconds) =>
        vdc.Exposure.TrySetAuto(false) & vdc.Exposure.TrySetValue(log2Seconds);

    public int? Exposure => vdc.Exposure.TryGetValue(out var v) ? (int)Math.Round(v) : null;

    /// <summary>Freezes white balance and turns off backlight compensation so neither renormalizes the image.</summary>
    public void LockImageProcessing()
    {
        var wb = vdc.WhiteBalance;
        if (wb.Capabilities.Supported && wb.TryGetValue(out var kelvin)) { wb.TrySetAuto(false); wb.TrySetValue(kelvin); }
        var blc = vdc.BacklightCompensation;
        if (blc.Capabilities.Supported) { blc.TrySetAuto(false); blc.TrySetValue(blc.Capabilities.Min); }
    }

    public int? Gain
    {
        get
        {
            var r = vdc.GetDevicePropertyByExtendedId(Property(GainId, KsGet), PropertySize);
            return r.Status == VideoDeviceControllerGetDevicePropertyStatus.Success && r.Value is byte[] b
                ? BitConverter.ToInt32(b, 24)
                : null;
        }
    }

    /// <summary>Sets gain and confirms it by reading it back; some drivers accept the call but ignore it.</summary>
    public bool TrySetGain(int value)
    {
        var p = Property(GainId, KsSet);
        BitConverter.GetBytes(value).CopyTo(p, 24);
        BitConverter.GetBytes(FlagManual).CopyTo(p, 28);
        var status = vdc.SetDevicePropertyByExtendedId(p, p);
        return status == VideoDeviceControllerSetDevicePropertyStatus.Success && Gain == value;
    }

    /// <summary>Reads the gain range via KSPROPERTY_TYPE_BASICSUPPORT; null when the driver doesn't report one.</summary>
    public (int Min, int Max)? GainRange
    {
        get
        {
            // Reply: KSPROPERTY_DESCRIPTION (40), KSPROPERTY_MEMBERSHEADER (16), KSPROPERTY_STEPPING_LONG (step, reserved, min, max).
            var r = vdc.GetDevicePropertyByExtendedId(Property(GainId, KsBasicSupport), 256);
            if (r.Status != VideoDeviceControllerGetDevicePropertyStatus.Success || r.Value is not byte[] b || b.Length < 72)
                return null;
            var min = BitConverter.ToInt32(b, 64);
            var max = BitConverter.ToInt32(b, 68);
            return max > min ? (min, max) : null;
        }
    }

    /// <summary>Every setting this app changes, so <see cref="Restore"/> can put all of them back.</summary>
    public Snapshot Save()
    {
        vdc.Exposure.TryGetAuto(out var expAuto);
        vdc.WhiteBalance.TryGetAuto(out var wbAuto);
        var wb = vdc.WhiteBalance.TryGetValue(out var kelvin) ? kelvin : (double?)null;
        var blc = vdc.BacklightCompensation;
        var blcSupported = blc.Capabilities.Supported;
        var blcAuto = false;
        if (blcSupported) blc.TryGetAuto(out blcAuto);
        var blcValue = blcSupported && blc.TryGetValue(out var b) ? b : (double?)null;
        return new Snapshot(expAuto, Exposure, wbAuto, Gain, wb, blcValue, blcAuto);
    }

    public void Restore(Snapshot s)
    {
        // Each control separately, so one that fails doesn't leave the rest changed.
        static void Try(Action restore)
        {
            try { restore(); }
            catch (Exception) { /* device gone, or the control went away */ }
        }

        if (s.Gain is { } g) Try(() => TrySetGain(g));
        if (s.ExposureAuto) Try(() => vdc.Exposure.TrySetAuto(true));
        else if (s.Exposure is { } e) Try(() => vdc.Exposure.TrySetValue(e));
        if (s.WhiteBalanceAuto) Try(() => vdc.WhiteBalance.TrySetAuto(true));
        else if (s.WhiteBalance is { } wb) Try(() => vdc.WhiteBalance.TrySetValue(wb));
        if (s.Backlight is { } blc) Try(() => vdc.BacklightCompensation.TrySetValue(blc));
        if (s.BacklightAuto) Try(() => vdc.BacklightCompensation.TrySetAuto(true));
    }

    /// <summary>Returns exposure and white balance to automatic; for diagnostics.</summary>
    public void RestoreAuto()
    {
        vdc.Exposure.TrySetAuto(true);
        vdc.WhiteBalance.TrySetAuto(true);
    }

    /// <remarks>Fields after <see cref="Gain"/> were added later; they default so older recovery files still load.</remarks>
    public sealed record Snapshot(bool ExposureAuto, int? Exposure, bool WhiteBalanceAuto, int? Gain,
        double? WhiteBalance = null, double? Backlight = null, bool BacklightAuto = false);

    private static byte[] Property(uint id, uint flags)
    {
        var b = new byte[PropertySize];
        VideoProcAmpSet.ToByteArray().CopyTo(b, 0);
        BitConverter.GetBytes(id).CopyTo(b, 16);
        BitConverter.GetBytes(flags).CopyTo(b, 20);
        return b;
    }
}
