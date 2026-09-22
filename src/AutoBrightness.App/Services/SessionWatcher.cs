using System.Runtime.InteropServices;

namespace AutoBrightness.App.Services;

/// <summary>
/// Reports when the camera shouldn't be used: the session is locked, the display is off, or the PC is going to
/// sleep. Messages arrive on the main window (hidden while the app is in the tray), which stays alive.
/// </summary>
internal sealed partial class SessionWatcher : IDisposable
{
    private const uint WmWtsSessionChange = 0x02B1, WmPowerBroadcast = 0x0218;
    private const int WtsSessionLock = 0x7, WtsSessionUnlock = 0x8;
    private const int PbtApmSuspend = 0x4, PbtApmResumeSuspend = 0x7, PbtApmResumeAutomatic = 0x12, PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private readonly nint _hwnd;
    private readonly SubclassProc _proc; // kept alive for the lifetime of the subclass
    private readonly nint _displayNotification;
    private readonly Action<string?> _changed;
    private bool _locked, _displayOff, _asleep;

    public SessionWatcher(nint hwnd, Action<string?> changed)
    {
        _hwnd = hwnd;
        _changed = changed;
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, 2, 0);
        WTSRegisterSessionNotification(hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */);
        var guid = ConsoleDisplayState;
        // Windows sends the current display state straight away.
        _displayNotification = RegisterPowerSettingNotification(hwnd, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
    }

    /// <summary>Why the camera should rest right now, or null when it may be used.</summary>
    public string? Reason =>
        _asleep ? "the PC is going to sleep"
        : _locked ? "the screen is locked"
        : _displayOff ? "the display is off"
        : null;

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nint id, nint data)
    {
        var before = Reason;
        if (msg == WmWtsSessionChange)
        {
            if ((int)wParam == WtsSessionLock) _locked = true;
            else if ((int)wParam == WtsSessionUnlock) _locked = false;
        }
        else if (msg == WmPowerBroadcast)
        {
            switch ((int)wParam)
            {
                case PbtApmSuspend:
                    _asleep = true;
                    break;
                case PbtApmResumeSuspend or PbtApmResumeAutomatic:
                    _asleep = false;
                    break;
                case PbtPowerSettingChange when lParam != 0:
                    // POWERBROADCAST_SETTING: GUID PowerSetting, DWORD DataLength, then the data (0 off, 1 on, 2 dimmed).
                    if (Marshal.PtrToStructure<Guid>(lParam) == ConsoleDisplayState)
                        _displayOff = Marshal.ReadInt32(lParam, 20) == 0;
                    break;
            }
        }
        if (Reason != before) _changed(Reason);
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_displayNotification != 0) UnregisterPowerSettingNotification(_displayNotification);
        WTSUnRegisterSessionNotification(_hwnd);
        RemoveWindowSubclass(_hwnd, _proc, 2);
    }

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nint id, nint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSetting, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(nint handle);
}
