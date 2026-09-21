using AutoBrightness.App.Services;
using AutoBrightness.Control;
using AutoBrightness.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AutoBrightness.App;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: a second launch (e.g. from the Start menu) just shows the running app.
        var main = AppInstance.FindOrRegisterForKey("AutoBrightness.Main");
        if (!main.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            await Task.Run(() => main.RedirectActivationToAsync(activation).AsTask());
            Exit();
            return;
        }
        main.Activated += (_, e) => AppServices.OnUi(() =>
        {
            if (IsExitRequest(e)) _ = ExitAsync();
            else ShowWindow();
        });

        if (Environment.GetCommandLineArgs().Contains(ExitArgument, StringComparer.OrdinalIgnoreCase))
        {
            Exit(); // nothing running to stop
            return;
        }

        await AppServices.InitializeAsync(DispatcherQueue.GetForCurrentThread());

        _window = new MainWindow();
        _window.AppWindow.Closing += (sender, e) =>
        {
            if (_exiting) return;
            e.Cancel = true; // closing the window keeps the app running in the tray
            sender.Hide();
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _tray = new TrayIcon(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), ShowWindow, TrayMenu);
        AppServices.Controller.Updated += s => AppServices.OnUi(() => UpdateTooltip(s));

        var background = Environment.GetCommandLineArgs().Contains(StartupRegistration.BackgroundArgument);
        if (!background) ShowWindow();
    }

    /// <summary>"AutoBrightness.exe --exit" asks the running instance to shut down cleanly.</summary>
    private static bool IsExitRequest(AppActivationArguments e) =>
        e.Kind == ExtendedActivationKind.Launch
        && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
        && launch.Arguments.Contains(ExitArgument, StringComparison.OrdinalIgnoreCase);

    public const string ExitArgument = "--exit";

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.AppWindow.Show();
        _window.Activate();
    }

    private IReadOnlyList<TrayMenuItem> TrayMenu()
    {
        var mode = AppServices.Store.Current.Mode;
        var paused = AppServices.Controller.PausedUntil is not null;
        return
        [
            new("Open AutoBrightness", ShowWindow),
            TrayMenuItem.Separator,
            new("Automatic", () => AppServices.SetMode(ControlMode.Auto), mode == ControlMode.Auto),
            new("Preview only", () => AppServices.SetMode(ControlMode.Preview), mode == ControlMode.Preview),
            new("Off", () => AppServices.SetMode(ControlMode.Off), mode == ControlMode.Off),
            TrayMenuItem.Separator,
            new("Measure now", AppServices.Controller.SampleNow, Enabled: mode != ControlMode.Off),
            paused
                ? new("Resume", AppServices.Controller.Resume)
                : new("Pause for 1 hour", () => AppServices.Controller.Pause(TimeSpan.FromHours(1)), Enabled: mode == ControlMode.Auto),
            TrayMenuItem.Separator,
            new("Exit", () => _ = ExitAsync()),
        ];
    }

    private void UpdateTooltip(ControllerSnapshot s)
    {
        if (_tray is null) return;
        var mode = s.Mode switch { ControlMode.Auto => "Automatic", ControlMode.Preview => "Preview", _ => "Off" };
        var light = s.SmoothedEv is { } ev ? $"light {ev:F1}" : null;
        var target = s.CurveBrightness is { } b ? $"target {b:F0}%" : null;
        _tray.Tooltip = string.Join(" · ", new[] { $"AutoBrightness ({mode})", light, target }.Where(x => x is not null));
    }

    private async Task ExitAsync()
    {
        _exiting = true;
        _tray?.Dispose();
        await AppServices.ShutdownAsync();
        _window?.Close();
        Exit();
    }
}
