using System.Runtime.InteropServices;

namespace AutoBrightness.App.Services;

/// <summary>A tray menu entry. Separators have no text.</summary>
internal sealed record TrayMenuItem(string? Text, Action? Invoke = null, bool Checked = false, bool Enabled = true)
{
    public static readonly TrayMenuItem Separator = new((string?)null);
}

/// <summary>
/// Notification-area icon implemented directly on Shell_NotifyIcon. Messages are received by subclassing the
/// main window, which stays alive (hidden) while the app runs in the tray.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B, NinSelect = 0x0400, NinKeySelect = 0x0401;
    private const uint MfString = 0x0, MfGrayed = 0x1, MfChecked = 0x8, MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2, TpmBottomAlign = 0x20;

    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly nint _hwnd;
    private readonly nint _icon;
    private readonly SubclassProc _proc; // kept alive for the lifetime of the subclass
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly Action _activate;
    private string _tooltip = "AutoBrightness";
    private bool _disposed;

    public TrayIcon(nint hwnd, string iconPath, Action activate, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        _hwnd = hwnd;
        _activate = activate;
        _menu = menu;
        _icon = LoadImage(0, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, 1, 0);
        Add();
    }

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            _tooltip = value.Length > 127 ? value[..127] : value;
            var data = Data(NifTip | NifShowTip);
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        Shell_NotifyIcon(NimAdd, ref data);
        data.uVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
    };

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nint id, nint data)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case WmLButtonUp or NinSelect or NinKeySelect:
                    _activate();
                    break;
                case WmRButtonUp or WmContextMenu:
                    ShowMenu();
                    break;
            }
            return 0;
        }
        if (msg == TaskbarCreated && !_disposed) Add(); // Explorer restarted
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = _menu();
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Text is null) { AppendMenu(menu, MfSeparator, 0, null); continue; }
                var flags = MfString | (item.Checked ? MfChecked : 0) | (item.Enabled ? 0 : MfGrayed);
                AppendMenu(menu, flags, (nuint)(i + 1), item.Text);
            }
            GetCursorPos(out var pt);
            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmBottomAlign, pt.X, pt.Y, _hwnd, 0);
            PostMessage(_hwnd, 0, 0, 0);
            if (cmd > 0 && cmd <= items.Count) items[cmd - 1].Invoke?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, ref data);
        RemoveWindowSubclass(_hwnd, _proc, 1);
        if (_icon != 0) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nint id, nint data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
