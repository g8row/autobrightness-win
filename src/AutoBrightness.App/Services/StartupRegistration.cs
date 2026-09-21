using Microsoft.Win32;

namespace AutoBrightness.App.Services;

/// <summary>Start-with-Windows through the per-user Run key; the app starts minimized to the tray.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoBrightness";
    public const string BackgroundArgument = "--background";

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value == Command;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
