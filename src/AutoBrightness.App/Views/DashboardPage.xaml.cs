using AutoBrightness.App.Services;
using AutoBrightness.Camera;
using AutoBrightness.Control;
using AutoBrightness.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoBrightness.App.Views;

public sealed partial class DashboardPage : Page
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _updatingMode;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _clock.Tick += (_, _) => UpdateCountdown();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Controller.Updated += OnUpdated;
        AppServices.Store.Changed += OnSettingsChanged;
        ShowMode();
        if (AppServices.Controller.Last is { } last) Render(last);
        Chart.Update(AppServices.Controller.History);
        _clock.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Controller.Updated -= OnUpdated;
        AppServices.Store.Changed -= OnSettingsChanged;
        _clock.Stop();
    }

    private void OnUpdated(ControllerSnapshot s) => AppServices.OnUi(() =>
    {
        Render(s);
        Chart.Update(AppServices.Controller.History);
    });

    private void OnSettingsChanged() => AppServices.OnUi(ShowMode);

    private void Render(ControllerSnapshot s)
    {
        StatusBar.Severity = s.Level switch
        {
            StatusLevel.Success => InfoBarSeverity.Success,
            StatusLevel.Warning => InfoBarSeverity.Warning,
            StatusLevel.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        StatusBar.Title = s.Level switch
        {
            StatusLevel.Error => "Problem",
            StatusLevel.Warning => "Attention",
            StatusLevel.Success => "Updated",
            _ => s.Mode switch { ControlMode.Auto => "Automatic", ControlMode.Preview => "Preview", _ => "Off" },
        };
        StatusBar.Message = s.Status;

        if (s.SmoothedEv is { } ev)
        {
            LightValue.Text = ev.ToString("0.0");
            LightWords.Text = Describe(ev);
        }
        else
        {
            LightValue.Text = "–";
            LightWords.Text = " ";
        }
        LightDetail.Text = s.Reading is { } r
            ? $"Last reading {r.Ev:0.00} at {r.Time:HH:mm:ss} · exposure {Calibrator.FormatExposure(r.Exposure)}, gain {r.Gain} · {r.Duration.TotalSeconds:0.0} s"
                + (r.Verdict is MeterVerdict.Saturated ? " · camera saturated" : r.Verdict is MeterVerdict.TooDark ? " · at camera's limit" : "")
            : " ";

        TargetValue.Text = s.CurveBrightness is { } b ? $"{b:0}%" : "–";
        var monitor = s.Monitors.FirstOrDefault(m => m.Current is not null) ?? s.Monitors.FirstOrDefault();
        TargetDetail.Text = monitor?.Target is { } t && s.CurveBrightness is { } cb && Math.Abs(t - cb) >= 1
            ? $"{t}% for {monitor.Name} after its offset and limits"
            : s.Mode == ControlMode.Auto ? $"Changes today: {s.WritesToday}" : " ";

        CurrentValue.Text = monitor?.Current is { } c ? $"{c}%" : "–";
        CurrentName.Text = monitor?.Name ?? "via Twinkle Tray";
        CurrentDetail.Text = string.Join(" · ", s.Monitors.Skip(1).Select(m => $"{m.Name}: {(m.Current is { } mc ? $"{mc}%" : "–")}")
            .Prepend(monitor?.Note ?? "").Where(x => x.Length > 0));
        if (CurrentDetail.Text.Length == 0) CurrentDetail.Text = " ";

        UpdatePauseButton();
        UpdateCountdown();
    }

    /// <summary>
    /// Rough words for a camera-relative light level; calibrated on a Logitech webcam, so treat as approximate.
    /// </summary>
    private static string Describe(double ev) => ev switch
    {
        < 0 => "Dark",
        < 2.5 => "Dim",
        < 5 => "Indoor lighting",
        < 8 => "Bright",
        _ => "Very bright",
    };

    private void ShowMode()
    {
        var mode = AppServices.Store.Current.Mode;
        _updatingMode = true;
        ModeButtons.SelectedIndex = (int)mode;
        _updatingMode = false;
        ModeHelp.Text = mode switch
        {
            ControlMode.Off => "The camera is not used and brightness is left alone.",
            ControlMode.Preview => "Measures light and shows the brightness it would set, without changing anything. " +
                                   "Adjusting brightness in Twinkle Tray still teaches the curve, so you can train it before switching to Automatic.",
            _ => "Measures light and adjusts brightness through Twinkle Tray. Adjusting brightness yourself teaches the curve " +
                 "and pauses automatic changes for a while.",
        };
        MeasureButton.IsEnabled = mode != ControlMode.Off;
        UpdatePauseButton();
    }

    private void UpdatePauseButton()
    {
        var paused = AppServices.Controller.PausedUntil;
        PauseButton.Content = paused is { } p ? $"Resume (paused until {p:HH:mm})" : "Pause for 1 hour";
        PauseButton.IsEnabled = AppServices.Store.Current.Mode == ControlMode.Auto;
    }

    private void UpdateCountdown()
    {
        var next = AppServices.Controller.Last?.NextSample;
        NextSample.Text = next is { } n && n > DateTime.Now && AppServices.Store.Current.Mode != ControlMode.Off
            ? $"Next reading in {(int)Math.Ceiling((n - DateTime.Now).TotalSeconds)} s"
            : "";
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMode || ModeButtons.SelectedItem is not RadioButton { Tag: string tag }) return;
        AppServices.SetMode(Enum.Parse<ControlMode>(tag));
    }

    private void OnMeasure(object sender, RoutedEventArgs e) => AppServices.Controller.SampleNow();

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (AppServices.Controller.PausedUntil is null) AppServices.Controller.Pause(TimeSpan.FromHours(1));
        else AppServices.Controller.Resume();
        UpdatePauseButton();
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangeBox.SelectedItem is ComboBoxItem { Tag: string hours } && Chart is not null)
        {
            Chart.Window = TimeSpan.FromHours(int.Parse(hours));
            Chart.Update(AppServices.Controller.History);
        }
    }
}
