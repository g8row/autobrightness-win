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
    private SessionWatcher? _session;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        // Leave a trace of anything that would otherwise end the process silently.
        UnhandledException += (_, e) => Log.Write("Unhandled exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write("Unobserved task exception", e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: a second launch (e.g. from the Start menu) just shows the running app.
        var main = AppInstance.FindOrRegisterForKey("AutoBrightness.Main");
        if (!main.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var mainPid = (int)main.ProcessId;
            await Task.Run(() => main.RedirectActivationToAsync(activation).AsTask());
            if (Environment.GetCommandLineArgs().Contains(ExitArgument, StringComparer.OrdinalIgnoreCase))
            {
                // Callers such as the installer need the files unlocked when this returns.
                await Task.Run(() =>
                {
                    try { System.Diagnostics.Process.GetProcessById(mainPid).WaitForExit(15_000); }
                    catch (ArgumentException) { /* already gone */ }
                });
            }
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

        try
        {
            await AppServices.InitializeAsync(DispatcherQueue.GetForCurrentThread());
        }
        catch (Exception ex)
        {
            Log.Write("Startup failed", ex);
            MessageBox(0, $"AutoBrightness could not start: {ex.Message}\n\nDetails are in {Log.PathName}.", "AutoBrightness", 0x10 /* MB_ICONERROR */);
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.AppWindow.Closing += (sender, e) =>
        {
            if (_exiting) return;
            e.Cancel = true; // closing the window keeps the app running in the tray
            sender.Hide();
            AppServices.NotifyWindowHidden();
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _tray = new TrayIcon(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), ShowWindow, TrayMenu);
        // No camera while the screen is locked or off, or the PC is going to sleep.
        _session = new SessionWatcher(hwnd, reason => AppServices.Controller.SetHold(reason));
        AppServices.Controller.SetHold(_session.Reason);
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
        _session?.Dispose();
        await AppServices.ShutdownAsync();
        _window?.Close();
        Exit();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint hwnd, string text, string caption, uint type);
}
