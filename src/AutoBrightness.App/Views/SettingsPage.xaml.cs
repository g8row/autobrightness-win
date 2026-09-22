using System.Diagnostics;
using System.Reflection;
using AutoBrightness.App.Services;
using AutoBrightness.Settings;
using AutoBrightness.Twinkle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoBrightness.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LoadValues();
            await LoadMonitorsAsync();
        };
    }

    private void LoadValues()
    {
        _loading = true;
        var s = AppServices.Store.Current;
        IntervalBox.Value = s.SampleIntervalSeconds;
        BrightenBox.Value = s.BrightenSeconds;
        DimBox.Value = s.DimSeconds;
        MinStepBox.Value = s.WritePolicy.MinStep;
        MinIntervalBox.Value = s.WritePolicy.MinInterval.TotalSeconds;
        BudgetBox.Value = s.WritePolicy.DailyBudget;
        BigStepBox.Value = s.WritePolicy.BigStep;
        LearnToggle.IsOn = s.LearnFromManual;
        FadeToggle.IsOn = s.Transitions.Enabled;
        FadeStepBox.Value = s.Transitions.MaxStep;
        FadeIntervalBox.Value = s.Transitions.StepInterval.TotalMilliseconds;
        FadeStepBox.IsEnabled = FadeIntervalBox.IsEnabled = s.Transitions.Enabled;
        VersionText.Text = $"Version {typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}";
        PauseBox.Value = s.ManualPauseMinutes;
        StartupToggle.IsOn = StartupRegistration.IsEnabled;
        DataFolderText.Text = AppPaths.DataDirectory;
        _loading = false;
    }

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        var v = args.NewValue;
        AppServices.Store.Update(s =>
        {
            if (sender == IntervalBox) s.SampleIntervalSeconds = v;
            else if (sender == BrightenBox) s.BrightenSeconds = v;
            else if (sender == DimBox) s.DimSeconds = v;
            else if (sender == MinStepBox) s.WritePolicy = s.WritePolicy with { MinStep = (int)v };
            else if (sender == MinIntervalBox) s.WritePolicy = s.WritePolicy with { MinInterval = TimeSpan.FromSeconds(v) };
            else if (sender == BudgetBox) s.WritePolicy = s.WritePolicy with { DailyBudget = (int)v };
            else if (sender == BigStepBox) s.WritePolicy = s.WritePolicy with { BigStep = (int)v };
            else if (sender == PauseBox) s.ManualPauseMinutes = v;
            else if (sender == FadeStepBox) s.Transitions = s.Transitions with { MaxStep = (int)v };
            else if (sender == FadeIntervalBox) s.Transitions = s.Transitions with { StepInterval = TimeSpan.FromMilliseconds(v) };
        });
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Store.Update(s =>
        {
            s.LearnFromManual = LearnToggle.IsOn;
            s.Transitions = s.Transitions with { Enabled = FadeToggle.IsOn };
        });
        FadeStepBox.IsEnabled = FadeIntervalBox.IsEnabled = FadeToggle.IsOn;
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupRegistration.Set(StartupToggle.IsOn);
        AppServices.Store.Update(s => s.StartWithWindows = StartupToggle.IsOn);
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
    }

    private async void OnRefreshMonitors(object sender, RoutedEventArgs e) => await LoadMonitorsAsync();

    private async Task LoadMonitorsAsync()
    {
        MonitorList.Children.Clear();
        IReadOnlyList<TwinkleMonitor> monitors;
        try
        {
            monitors = await AppServices.Twinkle.ListAsync();
        }
        catch (TwinkleUnavailableException ex)
        {
            MonitorList.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (monitors.Count == 0)
        {
            MonitorList.Children.Add(new TextBlock { Text = "Twinkle Tray reports no monitors." });
            return;
        }
        foreach (var m in monitors) MonitorList.Children.Add(MonitorRow(m));
    }

    private FrameworkElement MonitorRow(TwinkleMonitor monitor)
    {
        var settings = AppServices.Store.Current.Monitors.FirstOrDefault(x => x.Key == monitor.Key)
                       ?? new MonitorSettings { Key = monitor.Key, Name = monitor.Name };

        void Save(Func<MonitorSettings, MonitorSettings> change) => AppServices.Store.Update(s =>
        {
            var current = s.Monitors.FirstOrDefault(x => x.Key == monitor.Key) ?? settings;
            // A new list rather than an edit: the control loop may be reading the old one.
            s.Monitors = [.. s.Monitors.Where(x => x.Key != monitor.Key), change(current) with { Name = monitor.Name }];
        });

        var grid = new Grid { ColumnSpacing = 12 };
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = monitor.Name });
        info.Children.Add(new TextBlock
        {
            Text = $"{monitor.Type.ToUpperInvariant()} · now {monitor.Brightness}%",
            Style = (Style)Application.Current.Resources["MetricCaptionStyle"],
        });
        grid.Children.Add(info);

        var enabled = new ToggleSwitch { IsOn = settings.Enabled, OnContent = "Controlled", OffContent = "Ignored", MinWidth = 0 };
        enabled.Toggled += (_, _) => Save(m => m with { Enabled = enabled.IsOn });
        Grid.SetColumn(enabled, 1);
        grid.Children.Add(enabled);

        NumberBox Number(string header, double value, double min, double max, int column, Func<MonitorSettings, int, MonitorSettings> apply)
        {
            var box = new NumberBox
            {
                Header = header, Value = value, Minimum = min, Maximum = max, Width = 100,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            box.ValueChanged += (_, a) => { if (!double.IsNaN(a.NewValue)) Save(m => apply(m, (int)a.NewValue)); };
            Grid.SetColumn(box, column);
            grid.Children.Add(box);
            return box;
        }

        Number("Offset", settings.Offset, -50, 50, 2, (m, v) => m with { Offset = v });
        Number("Minimum", settings.Min, 0, 100, 3, (m, v) => m with { Min = v });
        Number("Maximum", settings.Max, 0, 100, 4, (m, v) => m with { Max = v });
        return grid;
    }
}
