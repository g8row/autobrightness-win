using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using AutoBrightness.App.Services;
using AutoBrightness.Camera;
using AutoBrightness.Control;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AutoBrightness.App.Views;

public sealed partial class CameraPage : Page
{
    private readonly ObservableCollection<string> _log = [];
    private WriteableBitmap? _bitmap;
    private DateTime _lastPreview;
    private bool _live;
    private bool _loading;
    private CancellationTokenSource? _calibration;
    private Windows.Foundation.Point? _dragStart;
    private ILightMeter? _previewMeter;

    public CameraPage()
    {
        InitializeComponent();
        CalibrationLog.ItemsSource = _log;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Controller.Updated += OnControllerUpdated;
        AppServices.WindowHidden += OnWindowHidden;
        await LoadCamerasAsync(AppServices.Cameras);
        ShowRoi(AppServices.Store.Current.Roi);
        if (AppServices.Controller.Last?.Reading is { } r) ShowFrame(r.Frame);
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Controller.Updated -= OnControllerUpdated;
        AppServices.WindowHidden -= OnWindowHidden;
        _calibration?.Cancel();
        await StopLiveAsync();
    }

    /// <summary>A hidden window doesn't unload its page; without this the camera would stay on in the tray.</summary>
    private async void OnWindowHidden() => await StopLiveAsync();

    private async Task LoadCamerasAsync(IReadOnlyList<CameraDevice> cameras)
    {
        _loading = true;
        CameraBox.ItemsSource = cameras;
        var current = AppServices.Controller.Meter?.Device;
        CameraBox.SelectedItem = cameras.FirstOrDefault(c => c.Id == current?.Id);
        _loading = false;
        UpdateCameraInfo();
        await Task.CompletedTask;
    }

    private async void OnRefreshCameras(object sender, RoutedEventArgs e) =>
        await LoadCamerasAsync(await AppServices.RefreshCamerasAsync());

    private async void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CameraBox.SelectedItem is not CameraDevice device) return;
        await StopLiveAsync();
        await AppServices.Controller.SelectCameraAsync(device);
        _bitmap = null;
        FrameImage.Source = null;
        NoFrameText.Visibility = Visibility.Visible;
        UpdateCameraInfo();
    }

    private void UpdateCameraInfo()
    {
        var device = AppServices.Controller.Meter?.Device;
        CameraHint.Text = device is null
            ? "Choose a camera. Virtual cameras (Camo, OBS) cannot lock exposure, so they cannot measure light."
            : device.IsUsb
                ? $"{device.Name} · {device.Key}. Profiles are shared by every camera of the same model."
                : $"{device.Name} looks like a virtual camera; it probably cannot lock exposure.";
        ShowProfile(AppServices.Controller.Meter?.Profile);
        CalibrateButton.IsEnabled = device is not null && _calibration is null;
        LiveToggle.IsEnabled = device is not null;
    }

    private void ShowProfile(CameraProfile? p)
    {
        ProfilePanel.Children.Clear();
        if (p is null)
        {
            CalibrationStatus.Text = "No camera selected.";
            return;
        }
        CalibrationStatus.Text = p.IsCalibrated
            ? $"Calibrated {p.CalibratedAt:g}."
            : "Not calibrated. Readings use a generic tone curve until you calibrate.";
        if (!p.IsCalibrated) return;

        void Row(string label, string value)
        {
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["MetricCaptionStyle"] });
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            ProfilePanel.Children.Add(g);
        }

        Row("Exposure", $"{Calibrator.FormatExposure(p.ExposureMin)} to {Calibrator.FormatExposure(p.ExposureMax)}");
        Row("Tone curve", $"gamma {p.Gamma:0.00}, black level {p.Black:0.0}");
        Row("Gain", p.GainSupported ? $"up to ×{p.GainTable[^1].Factor:0.0} ({p.GainTable.Count} steps)" : "not used");
        Row("Settle time", $"{p.SettleFrames} frames");
        Row("Noise", $"{p.NoiseLevels:0.00} levels");
        foreach (var note in p.Notes) Row("Note", note);
    }

    // Live preview ------------------------------------------------------------------------------------

    private async void OnLiveToggled(object sender, RoutedEventArgs e)
    {
        if (LiveToggle.IsOn) await StartLiveAsync();
        else await StopLiveAsync();
    }

    private async Task StartLiveAsync()
    {
        if (_live || AppServices.Controller.Meter is not { } meter) return;
        _live = true;
        _previewMeter = meter;
        meter.PreviewFrame += OnPreviewFrame;
        try
        {
            await meter.StartPreviewAsync();
        }
        catch (CameraException ex)
        {
            meter.PreviewFrame -= OnPreviewFrame;
            _live = false;
            LiveToggle.IsOn = false;
            FrameStats.Text = ex.Message;
        }
    }

    private async Task StopLiveAsync()
    {
        if (!_live || _previewMeter is null) return;
        _live = false;
        _previewMeter.PreviewFrame -= OnPreviewFrame;
        await _previewMeter.StopPreviewAsync();
        _previewMeter = null;
        if (LiveToggle.IsOn) LiveToggle.IsOn = false;
    }

    private void OnPreviewFrame(LumaFrame frame)
    {
        // ~10 fps is plenty for aiming the camera.
        if ((DateTime.UtcNow - _lastPreview).TotalMilliseconds < 100) return;
        _lastPreview = DateTime.UtcNow;
        AppServices.OnUi(() => ShowFrame(frame));
    }

    private void OnControllerUpdated(ControllerSnapshot s)
    {
        if (!_live && s.Reading is { } r) AppServices.OnUi(() => ShowFrame(r.Frame));
    }

    private void ShowFrame(LumaFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height);
            FrameImage.Source = _bitmap;
            FrameHost.Height = FrameHost.Width * frame.Height / frame.Width;
            ShowRoi(AppServices.Store.Current.Roi);
        }

        var bgra = new byte[frame.Width * frame.Height * 4];
        for (var y = 0; y < frame.Height; y++)
        {
            var src = y * frame.Stride;
            var dst = y * frame.Width * 4;
            for (var x = 0; x < frame.Width; x++)
            {
                var v = frame.Pixels[src + x];
                bgra[dst + x * 4] = v;
                bgra[dst + x * 4 + 1] = v;
                bgra[dst + x * 4 + 2] = v;
                bgra[dst + x * 4 + 3] = 255;
            }
        }
        using (var stream = _bitmap.PixelBuffer.AsStream()) stream.Write(bgra);
        _bitmap.Invalidate();
        NoFrameText.Visibility = Visibility.Collapsed;

        var h = frame.Histogram(AppServices.Store.Current.Roi);
        FrameStats.Text = $"{frame.Width}×{frame.Height} · measured area: mean {h.Mean:0.0}, clipped {h.FractionAtOrAbove(ExposurePlanner.ClipLevel):P1}"
                          + (_live ? " · live" : $" · from reading at {frame.Timestamp.ToLocalTime():HH:mm:ss}");
    }

    // Region of interest -------------------------------------------------------------------------------

    private void ShowRoi(Roi roi)
    {
        Canvas.SetLeft(RoiRect, roi.X * FrameHost.Width);
        Canvas.SetTop(RoiRect, roi.Y * FrameHost.Height);
        RoiRect.Width = roi.Width * FrameHost.Width;
        RoiRect.Height = roi.Height * FrameHost.Height;
    }

    private void OnRoiPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(RoiCanvas).Position;
        RoiCanvas.CapturePointer(e.Pointer);
    }

    private void OnRoiMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is { } start) ShowRoi(RoiFrom(start, e.GetCurrentPoint(RoiCanvas).Position));
    }

    private async void OnRoiReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start) return;
        _dragStart = null;
        RoiCanvas.ReleasePointerCapture(e.Pointer);
        var roi = RoiFrom(start, e.GetCurrentPoint(RoiCanvas).Position).Clamp();
        ShowRoi(roi);
        await AppServices.Controller.SetRoiAsync(roi);
    }

    private Roi RoiFrom(Windows.Foundation.Point a, Windows.Foundation.Point b)
    {
        double w = FrameHost.Width, h = FrameHost.Height;
        var x0 = Math.Clamp(Math.Min(a.X, b.X), 0, w);
        var y0 = Math.Clamp(Math.Min(a.Y, b.Y), 0, h);
        var x1 = Math.Clamp(Math.Max(a.X, b.X), 0, w);
        var y1 = Math.Clamp(Math.Max(a.Y, b.Y), 0, h);
        return new Roi(x0 / w, y0 / h, (x1 - x0) / w, (y1 - y0) / h);
    }

    private async void OnResetRoi(object sender, RoutedEventArgs e)
    {
        ShowRoi(Roi.Full);
        await AppServices.Controller.SetRoiAsync(Roi.Full);
    }

    // Calibration --------------------------------------------------------------------------------------

    private async void OnCalibrate(object sender, RoutedEventArgs e)
    {
        if (AppServices.Controller.Meter?.Device is not { } device) return;
        await StopLiveAsync();

        _calibration = new CancellationTokenSource();
        _log.Clear();
        CalibrateButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        CalibrationProgress.Visibility = Visibility.Visible;
        CalibrationProgress.Value = 0;
        LiveToggle.IsEnabled = false;

        var progress = new Progress<CalibrationProgress>(p =>
        {
            CalibrationProgress.Value = p.Fraction;
            if (p.Detail is not null) _log.Add($"{p.Step}: {p.Detail}");
        });

        try
        {
            CameraProfile profile;
            await using (await AppServices.Controller.SuspendSamplingAsync())
            {
                profile = await Task.Run(() => new Calibrator(device).RunAsync(progress, _calibration.Token));
            }
            profile.Save();
            await AppServices.Controller.ApplyProfileAsync(profile);
            _log.Add("Saved. Light levels now use this calibration.");
        }
        catch (OperationCanceledException)
        {
            _log.Add("Calibration cancelled; the previous profile is unchanged.");
        }
        catch (CameraException ex)
        {
            _log.Add(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Write("Calibration failed", ex);
            _log.Add($"Calibration failed: {ex.Message}");
        }
        finally
        {
            _calibration.Dispose();
            _calibration = null;
            CancelButton.Visibility = Visibility.Collapsed;
            CalibrationProgress.Visibility = Visibility.Collapsed;
            UpdateCameraInfo();
        }
    }

    private void OnCancelCalibration(object sender, RoutedEventArgs e) => _calibration?.Cancel();
}
