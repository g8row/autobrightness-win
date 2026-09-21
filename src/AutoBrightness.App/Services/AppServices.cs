using AutoBrightness.Camera;
using AutoBrightness.Control;
using AutoBrightness.Settings;
using AutoBrightness.Twinkle;
using Microsoft.UI.Dispatching;

namespace AutoBrightness.App.Services;

/// <summary>Process-wide services shared by the window, its pages and the tray icon.</summary>
internal static class AppServices
{
    public static SettingsStore Store { get; private set; } = null!;
    public static TwinkleClient Twinkle { get; } = new();
    public static AutoBrightnessController Controller { get; private set; } = null!;
    public static DispatcherQueue Dispatcher { get; private set; } = null!;
    public static IReadOnlyList<CameraDevice> Cameras { get; private set; } = [];

    public static async Task InitializeAsync(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Store = new SettingsStore();
        // A previous run that died mid-measurement may have left the camera in manual exposure.
        await CameraRecovery.RecoverAsync();
        Controller = new AutoBrightnessController(Store, Twinkle);
        await RefreshCamerasAsync();

        // Reuse the saved camera; otherwise pick the first USB camera, since virtual ones (Camo, OBS)
        // cannot lock exposure.
        var saved = Cameras.FirstOrDefault(c => c.Id == Store.Current.CameraId);
        await Controller.SelectCameraAsync(saved ?? Cameras.FirstOrDefault(c => c.IsUsb));
        Controller.Start();
    }

    public static async Task<IReadOnlyList<CameraDevice>> RefreshCamerasAsync()
    {
        Cameras = await CameraDevice.FindAllAsync();
        return Cameras;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public static void OnUi(Action action)
    {
        if (Dispatcher.HasThreadAccess) action();
        else Dispatcher.TryEnqueue(() => action());
    }

    public static void SetMode(ControlMode mode)
    {
        if (Store.Current.Mode == mode) return;
        Store.Update(s => s.Mode = mode);
        Controller.SampleNow();
    }

    public static async Task ShutdownAsync() => await Controller.DisposeAsync();
}
