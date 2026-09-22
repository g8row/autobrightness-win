using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;

namespace AutoBrightness.Camera;

/// <summary>A video capture device as seen by Windows.</summary>
public sealed partial record CameraDevice(string Id, string Name)
{
    /// <summary>"VID_xxxx&amp;PID_xxxx" for USB cameras; the name for virtual ones.</summary>
    public string Key => HardwareId(Id) ?? Name;

    public bool IsUsb => HardwareId(Id) is not null;

    public static async Task<IReadOnlyList<CameraDevice>> FindAllAsync()
    {
        var infos = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return infos.Where(i => i.IsEnabled).Select(i => new CameraDevice(i.Id, i.Name)).ToList();
    }

    internal static string? HardwareId(string id)
    {
        var m = VidPid().Match(id);
        return m.Success ? $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}" : null;
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    public override string ToString() => Name;
}

public enum CameraFailure { Unavailable, AccessDenied, Stalled, Unsupported, UnsuitableScene }

public sealed class CameraException(CameraFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public CameraFailure Failure { get; } = failure;
}
