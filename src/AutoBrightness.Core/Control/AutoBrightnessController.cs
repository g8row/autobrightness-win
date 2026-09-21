using AutoBrightness.Camera;
using AutoBrightness.Settings;
using AutoBrightness.Twinkle;

namespace AutoBrightness.Control;

public enum StatusLevel { Info, Success, Warning, Error }

public sealed record MonitorState(string Key, string Name, int? Current, int? Target, string? Note);

public sealed record HistoryPoint(DateTime Time, double Ev, double SmoothedEv, double CurveBrightness, int? Current);

public sealed record ControllerSnapshot(
    DateTime Time,
    ControlMode Mode,
    LightReading? Reading,
    double? SmoothedEv,
    double? CurveBrightness,
    IReadOnlyList<MonitorState> Monitors,
    string Status,
    StatusLevel Level,
    DateTime? PausedUntil,
    int WritesToday,
    DateTime? NextSample);

/// <summary>
/// The measurement and control loop: meter the room, smooth, map through the curve, and (in Auto mode)
/// write brightness through Twinkle Tray within the write budget. Manual changes made in Twinkle Tray are
/// detected between samples, learned into the curve, and pause automatic writes for a while.
/// </summary>
public sealed class AutoBrightnessController : IAsyncDisposable
{
    private const int ManualTolerance = 2;
    private static readonly TimeSpan StaleHistory = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MonitorListRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    private readonly SettingsStore _store;
    private readonly IBrightnessBackend _backend;
    private readonly Func<CameraDevice, CameraProfile, ILightMeter> _meterFactory;
    private readonly LightSmoother _smoother;
    private readonly WritePolicy _policy;
    private readonly BrightnessCurve _curve;
    private readonly Lock _curveLock = new();
    private readonly Dictionary<string, MonitorTrack> _tracks = [];
    private readonly List<HistoryPoint> _history = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    private ILightMeter? _meter;
    private IReadOnlyList<TwinkleMonitor> _monitors = [];
    private DateTime _monitorsFetched = DateTime.MinValue;
    private DateTime? _lastSample;
    private int _failures;
    private Task? _loop;

    public AutoBrightnessController(SettingsStore store, IBrightnessBackend backend,
        Func<CameraDevice, CameraProfile, ILightMeter>? meterFactory = null)
    {
        _store = store;
        _backend = backend;
        _meterFactory = meterFactory ?? ((d, p) => new LightMeter(d, p));
        var s = store.Current;
        _smoother = new LightSmoother(TimeSpan.FromSeconds(s.BrightenSeconds), TimeSpan.FromSeconds(s.DimSeconds));
        _policy = new WritePolicy(s.WritePolicy);
        _policy.Restore(s.WritesDay, s.WritesToday);
        _curve = new BrightnessCurve(s.Curve);
        _store.Changed += OnSettingsChanged;
    }

    public event Action<ControllerSnapshot>? Updated;

    public ControllerSnapshot? Last { get; private set; }
    public ILightMeter? Meter => _meter;
    public DateTime? PausedUntil { get; private set; }

    public IReadOnlyList<HistoryPoint> History
    {
        get { lock (_history) return [.. _history]; }
    }

    public IReadOnlyList<CurvePoint> CurvePoints
    {
        get { lock (_curveLock) return [.. _curve.Points]; }
    }

    public void SetCurve(IEnumerable<CurvePoint> points)
    {
        List<CurvePoint> saved;
        lock (_curveLock)
        {
            _curve.Set(points);
            saved = [.. _curve.Points];
        }
        _store.Update(s => s.Curve = saved);
    }

    public double EvaluateCurve(double ev)
    {
        lock (_curveLock) return _curve.Evaluate(ev);
    }

    /// <summary>Selects the camera and loads its calibration profile (or an uncalibrated default).</summary>
    public async Task SelectCameraAsync(CameraDevice? device)
    {
        await _sampleGate.WaitAsync();
        try
        {
            if (_meter is not null) await _meter.DisposeAsync();
            _meter = null;
            if (device is not null)
            {
                var profile = CameraProfile.Load(device.Key) ?? CameraProfile.Uncalibrated(device.Key, device.Name);
                _meter = _meterFactory(device, profile);
                _meter.Roi = _store.Current.Roi;
            }
            _smoother.Reset();
            _failures = 0;
        }
        finally
        {
            _sampleGate.Release();
        }
        if (_store.Current.CameraId != device?.Id) _store.Update(s => s.CameraId = device?.Id);
        SampleNow();
    }

    /// <summary>Installs a new calibration profile; the smoothed history is on the old scale, so it restarts.</summary>
    public void ApplyProfile(CameraProfile profile)
    {
        if (_meter is null) return;
        _meter.Profile = profile;
        _smoother.Reset();
        lock (_history) _history.Clear();
        SampleNow();
    }

    /// <summary>Runs one sample immediately; for tests.</summary>
    internal Task SampleOnceAsync(CancellationToken ct = default) => SampleAsync(DateTime.Now, ct);

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    public void SampleNow()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    public void Pause(TimeSpan duration)
    {
        PausedUntil = DateTime.Now + duration;
        SampleNow();
    }

    public void Resume()
    {
        PausedUntil = null;
        SampleNow();
    }

    private void OnSettingsChanged()
    {
        var s = _store.Current;
        _smoother.BrightenTime = TimeSpan.FromSeconds(s.BrightenSeconds);
        _smoother.DimTime = TimeSpan.FromSeconds(s.DimSeconds);
        _policy.Options = s.WritePolicy;
        if (_meter is not null) _meter.Roi = s.Roi;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(5, _store.Current.SampleIntervalSeconds));
            if (_failures > 0)
                delay = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, delay.Ticks * (1L << Math.Min(_failures, 6))));

            try
            {
                await SampleAsync(DateTime.Now + delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Publish(null, $"Unexpected error: {ex.Message}", StatusLevel.Error, [], DateTime.Now + delay);
            }

            try { await _wake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SampleAsync(DateTime nextSample, CancellationToken ct)
    {
        await _sampleGate.WaitAsync(ct);
        try
        {
            await SampleCoreAsync(nextSample, ct);
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    private async Task SampleCoreAsync(DateTime nextSample, CancellationToken ct)
    {
        var settings = _store.Current;
        var now = DateTime.Now;
        if (PausedUntil is { } until && now >= until) PausedUntil = null;

        if (settings.Mode == ControlMode.Off)
        {
            Publish(null, "Off. The camera is not used.", StatusLevel.Info, [], null);
            return;
        }
        if (_meter is null)
        {
            Publish(null, "Choose a camera on the Camera page.", StatusLevel.Warning, [], nextSample);
            return;
        }

        LightReading reading;
        try
        {
            reading = await _meter.MeasureAsync(ct);
            _failures = 0;
        }
        catch (CameraException ex)
        {
            _failures++;
            Publish(null, ex.Message, StatusLevel.Error, [], nextSample);
            return;
        }

        if (_lastSample is { } prev && now - prev > StaleHistory) _smoother.Reset();
        _lastSample = now;
        var smoothed = _smoother.Add(reading.Ev, now);
        var curveValue = EvaluateCurve(smoothed);

        var (monitors, status, level) = await UpdateMonitorsAsync(settings, smoothed, curveValue, ct);
        if (reading.Verdict == MeterVerdict.Saturated && level < StatusLevel.Warning)
            (status, level) = ("Too bright for the camera even at its shortest exposure; readings are capped.", StatusLevel.Warning);
        else if (reading.Verdict == MeterVerdict.TooDark && level < StatusLevel.Warning)
            (status, level) = ("Too dark for the camera to measure precisely; readings are a lower bound.", StatusLevel.Warning);
        if (_meter.Profile is { IsCalibrated: false } && level < StatusLevel.Warning)
            (status, level) = ("The camera is not calibrated yet; light levels use a generic model.", StatusLevel.Warning);

        lock (_history)
        {
            _history.Add(new HistoryPoint(now, reading.Ev, smoothed, curveValue, monitors.FirstOrDefault()?.Current));
            _history.RemoveAll(h => now - h.Time > TimeSpan.FromHours(24));
        }
        Publish(reading, status, level, monitors, nextSample, smoothed, curveValue);
    }

    private async Task<(List<MonitorState>, string, StatusLevel)> UpdateMonitorsAsync(
        AppSettings settings, double smoothed, double curveValue, CancellationToken ct)
    {
        var now = DateTime.Now;
        var result = new List<MonitorState>();
        var status = settings.Mode == ControlMode.Auto
            ? PausedUntil is { } p ? $"Paused until {p:HH:mm}." : "Adjusting brightness automatically."
            : "Preview: showing the target brightness without changing it.";
        var level = StatusLevel.Info;

        try
        {
            if (now - _monitorsFetched > MonitorListRefresh || _monitors.Count == 0)
            {
                _monitors = await _backend.ListAsync(ct);
                _monitorsFetched = now;
            }
        }
        catch (TwinkleUnavailableException ex)
        {
            _monitors = [];
            return (result, ex.Message, StatusLevel.Warning);
        }

        foreach (var monitor in _monitors)
        {
            var ms = settings.Monitors.FirstOrDefault(m => m.Key == monitor.Key) ?? new MonitorSettings { Key = monitor.Key, Name = monitor.Name };
            if (!ms.Enabled)
            {
                result.Add(new MonitorState(monitor.Key, monitor.Name, null, null, "Not controlled"));
                continue;
            }

            int current;
            try
            {
                current = await _backend.GetBrightnessAsync(monitor.Key, ct);
            }
            catch (TwinkleUnavailableException ex)
            {
                _monitorsFetched = DateTime.MinValue;
                result.Add(new MonitorState(monitor.Key, monitor.Name, null, null, ex.Message));
                level = StatusLevel.Warning;
                continue;
            }

            var track = _tracks.TryGetValue(monitor.Key, out var t) ? t : _tracks[monitor.Key] = new MonitorTrack();
            var target = Math.Clamp((int)Math.Round(curveValue + ms.Offset), ms.Min, ms.Max);
            string? note = null;

            // A change we did not make is the user's preference for the current light level.
            if (track.Expected is { } expected && Math.Abs(current - expected) >= ManualTolerance)
            {
                if (settings.LearnFromManual)
                {
                    var learned = Math.Clamp(current - ms.Offset, 0, 100);
                    List<CurvePoint> saved;
                    lock (_curveLock)
                    {
                        _curve.Learn(smoothed, learned);
                        saved = [.. _curve.Points];
                    }
                    _store.Update(s => s.Curve = saved);
                    curveValue = EvaluateCurve(smoothed);
                    target = Math.Clamp((int)Math.Round(curveValue + ms.Offset), ms.Min, ms.Max);
                    status = $"Learned your adjustment: light level {smoothed:F1} → {learned}%.";
                    level = StatusLevel.Success;
                }
                if (settings.Mode == ControlMode.Auto)
                {
                    PausedUntil = now + TimeSpan.FromMinutes(settings.ManualPauseMinutes);
                    status += $" Automatic changes paused until {PausedUntil:HH:mm}.";
                }
                note = "Manual change";
            }
            track.Expected = current;

            if (settings.Mode == ControlMode.Auto && PausedUntil is null && note is null)
            {
                switch (_policy.Decide(current, target, now))
                {
                    case WriteDecision.Write:
                        try
                        {
                            await _backend.SetBrightnessAsync(monitor.Key, target, ct);
                            _policy.Record(now);
                            track.Expected = target;
                            note = $"Set {current}% → {target}%";
                            SaveWriteCount();
                        }
                        catch (TwinkleUnavailableException ex)
                        {
                            note = ex.Message;
                            level = StatusLevel.Warning;
                        }
                        break;
                    case WriteDecision.TooSoon:
                        note = "Waiting (minimum interval between changes)";
                        break;
                    case WriteDecision.BudgetExhausted:
                        note = "Daily change budget used; only large changes are applied";
                        break;
                }
            }
            result.Add(new MonitorState(monitor.Key, monitor.Name, current, target, note));
        }

        if (_monitors.Count == 0) (status, level) = ("Twinkle Tray reports no monitors.", StatusLevel.Warning);
        return (result, status, level);
    }

    private void SaveWriteCount() => _store.Update(s =>
    {
        s.WritesDay = _policy.Day;
        s.WritesToday = _policy.WritesToday;
    });

    private void Publish(LightReading? reading, string status, StatusLevel level, IReadOnlyList<MonitorState> monitors,
        DateTime? next, double? smoothed = null, double? curve = null)
    {
        var snapshot = new ControllerSnapshot(DateTime.Now, _store.Current.Mode, reading, smoothed ?? _smoother.Value,
            curve, monitors, status, level, PausedUntil, _policy.WritesToday, next);
        Last = snapshot;
        Updated?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Changed -= OnSettingsChanged;
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        if (_meter is not null) await _meter.DisposeAsync();
    }

    private sealed class MonitorTrack
    {
        /// <summary>Brightness we last observed or wrote; anything else next time is a manual change.</summary>
        public int? Expected;
    }
}
