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
/// write brightness through Twinkle Tray within each monitor's write limits. Manual changes made in Twinkle
/// Tray are detected between samples, pause automatic writes for a while, and are learned into the curve once
/// the light reading is steady.
/// </summary>
public sealed class AutoBrightnessController : IAsyncDisposable
{
    private const int ManualTolerance = 2;
    /// <summary>Two raw readings this close (in stops) count as a steady light level for learning.</summary>
    private const double LearnAgreement = 0.5;
    private static readonly TimeSpan StaleHistory = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MonitorListRefresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    private readonly SettingsStore _store;
    private readonly IBrightnessBackend _backend;
    private readonly Func<CameraDevice, CameraProfile, ILightMeter> _meterFactory;
    private readonly LightSmoother _smoother;
    private readonly BrightnessCurve _curve;
    private readonly Lock _curveLock = new();
    private readonly Dictionary<string, MonitorTrack> _tracks = [];
    private readonly Dictionary<string, WritePolicy> _policies = [];
    private readonly List<HistoryPoint> _history = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    private ILightMeter? _meter;
    private IReadOnlyList<TwinkleMonitor> _monitors = [];
    private DateTime _monitorsFetched = DateTime.MinValue;
    private DateTime? _lastSample;
    private int _failures;
    private volatile string? _hold;
    private Task? _loop;

    public AutoBrightnessController(SettingsStore store, IBrightnessBackend backend,
        Func<CameraDevice, CameraProfile, ILightMeter>? meterFactory = null)
    {
        _store = store;
        _backend = backend;
        _meterFactory = meterFactory ?? ((d, p) => new LightMeter(d, p));
        var s = store.Current;
        _smoother = new LightSmoother(TimeSpan.FromSeconds(s.BrightenSeconds), TimeSpan.FromSeconds(s.DimSeconds));
        _curve = new BrightnessCurve(s.Curve);
        _store.Changed += OnSettingsChanged;
    }

    public event Action<ControllerSnapshot>? Updated;

    public ControllerSnapshot? Last { get; private set; }
    public ILightMeter? Meter => _meter;
    public DateTime? PausedUntil { get; private set; }

    /// <summary>Shortest delay between fade steps, whatever the settings say; tests lower it.</summary>
    internal TimeSpan MinFadeStepInterval { get; init; } = TimeSpan.FromMilliseconds(200);

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

    /// <summary>
    /// Installs a new calibration profile. A new tone curve moves the light scale, so the brightness curve is
    /// shifted by the difference it makes to the last reading, keeping the current brightness where it was.
    /// </summary>
    public async Task ApplyProfileAsync(CameraProfile profile)
    {
        await _sampleGate.WaitAsync();
        try
        {
            if (_meter is null) return;
            // An uncalibrated profile's scale is arbitrary; the default curve assumes a calibrated one.
            var shift = _meter.Profile.IsCalibrated ? EvShift(_meter.Profile, _meter.Roi, profile, _meter.Roi) : null;
            _meter.Profile = profile;
            ShiftCurve(shift, "the new calibration");
            _smoother.Reset();
            lock (_history) _history.Clear();
        }
        finally
        {
            _sampleGate.Release();
        }
        SampleNow();
    }

    /// <summary>Changes the measured area; the curve is shifted so the current scene keeps its brightness.</summary>
    public async Task SetRoiAsync(Roi roi)
    {
        roi = roi.Clamp();
        await _sampleGate.WaitAsync();
        try
        {
            if (_meter is not null)
            {
                ShiftCurve(EvShift(_meter.Profile, _meter.Roi, _meter.Profile, roi), "the new measurement area");
                _meter.Roi = roi;
            }
            _smoother.Reset();
        }
        finally
        {
            _sampleGate.Release();
        }
        _store.Update(s => s.Roi = roi);
        SampleNow();
    }

    /// <summary>How far the last reading's light level moves under a new profile or measurement area.</summary>
    private double? EvShift(CameraProfile oldProfile, Roi oldRoi, CameraProfile newProfile, Roi newRoi)
    {
        if (Last?.Reading is not { } r) return null;
        static double? Factor(CameraProfile p, int gain) =>
            p.GainTable.FirstOrDefault(g => g.Gain == gain)?.Factor ?? (p.GainTable.Count == 0 || !p.GainSupported ? 1.0 : null);
        if (Factor(oldProfile, r.Gain) is not { } oldGain || Factor(newProfile, r.Gain) is not { } newGain) return null;
        var before = oldProfile.Response.Ev(r.Frame.Histogram(oldRoi), r.Exposure, oldGain);
        var after = newProfile.Response.Ev(r.Frame.Histogram(newRoi), r.Exposure, newGain);
        return after - before;
    }

    private void ShiftCurve(double? shift, string reason)
    {
        if (shift is not { } s || !double.IsFinite(s) || Math.Abs(s) < 0.05) return;
        List<CurvePoint> saved;
        lock (_curveLock)
        {
            _curve.Shift(s);
            saved = [.. _curve.Points];
        }
        _store.Update(x => x.Curve = saved);
        Log.Write($"Curve shifted by {s:+0.00;-0.00} stops for {reason}");
    }

    /// <summary>Runs one sample immediately; for tests.</summary>
    internal Task SampleOnceAsync(CancellationToken ct = default) => SampleAsync(DateTime.Now, ct);

    /// <summary>
    /// Stops sampling until the returned handle is disposed, so something else (calibration) can own the camera.
    /// The meter's camera is closed while suspended.
    /// </summary>
    public async Task<IAsyncDisposable> SuspendSamplingAsync()
    {
        await _sampleGate.WaitAsync();
        return new Suspension(this);
    }

    private sealed class Suspension(AutoBrightnessController owner) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._sampleGate.Release();
                owner.SampleNow();
            }
            return ValueTask.CompletedTask;
        }
    }

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    public void SampleNow()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another thread woke the loop first.
        }
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

    /// <summary>
    /// Stops using the camera while <paramref name="reason"/> is set (screen locked, display off, sleeping).
    /// Brightness changes made meanwhile are not learned, and the first readings afterwards start fresh.
    /// </summary>
    public void SetHold(string? reason)
    {
        if (_hold == reason) return;
        _hold = reason;
        SampleNow();
    }

    private void OnSettingsChanged()
    {
        var s = _store.Current;
        _smoother.BrightenTime = TimeSpan.FromSeconds(s.BrightenSeconds);
        _smoother.DimTime = TimeSpan.FromSeconds(s.DimSeconds);
        lock (_policies)
            foreach (var policy in _policies.Values)
                policy.Options = s.WritePolicy;
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
                _failures++;
                Log.Write("Unexpected error while sampling", ex);
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
            // Whatever happens to brightness while off is not a preference for whatever the light will be later.
            ForgetObservations();
            Publish(null, "Off. The camera is not used.", StatusLevel.Info, [], null);
            return;
        }
        if (_hold is { } hold)
        {
            ForgetObservations();
            _smoother.Reset();
            Publish(null, $"Paused while {hold}. The camera is not used.", StatusLevel.Info, [], null);
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
            if (++_failures == 1) Log.Write($"Camera: {ex.Message}");
            Publish(null, ex.Message, StatusLevel.Error, [], nextSample);
            return;
        }

        if (_lastSample is { } prev && now - prev > StaleHistory)
        {
            // Long gap (sleep, camera trouble): old readings and observed brightness no longer apply.
            _smoother.Reset();
            ForgetObservations();
        }
        _lastSample = now;
        var smoothed = _smoother.Add(reading.Ev, now);
        var curveValue = EvaluateCurve(smoothed);

        var (monitors, status, level) = await UpdateMonitorsAsync(settings, reading.Ev, smoothed, ct);
        curveValue = EvaluateCurve(smoothed);
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
        AppSettings settings, double rawEv, double smoothed, CancellationToken ct)
    {
        var now = DateTime.Now;
        var result = new List<MonitorState>();
        var status = settings.Mode == ControlMode.Auto
            ? PausedUntil is { } p ? $"Paused until {p:HH:mm}." : "Adjusting brightness to the room."
            : "Showing the brightness it would set, without changing it.";
        var level = StatusLevel.Info;
        // One reading after a reset could be anything (someone in front of the camera, a lamp switching on).
        var steady = _smoother.Readings >= 2;

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
            string? note = null;

            // A change we did not make is the user's preference for the current light level. It is learned once
            // two readings agree, so a light that is still changing (or a reading taken with someone walking past)
            // doesn't get paired with it.
            if (track.Expected is { } expected && Math.Abs(current - expected) >= ManualTolerance)
            {
                track.Pending = settings.LearnFromManual ? new PendingLearn(current - ms.Offset, rawEv) : null;
                if (settings.Mode == ControlMode.Auto) PausedUntil = now + TimeSpan.FromMinutes(settings.ManualPauseMinutes);
                status = settings.LearnFromManual
                    ? $"Noticed your change to {current}%; it will be learned at the next steady reading."
                    : "Noticed your change.";
                if (settings.Mode == ControlMode.Auto) status += $" Automatic changes paused until {PausedUntil:HH:mm}.";
                level = StatusLevel.Success;
                note = "Manual change";
            }
            else if (track.Pending is { } pending)
            {
                if (!settings.LearnFromManual)
                {
                    track.Pending = null;
                }
                else if (Math.Abs(rawEv - pending.Ev) <= LearnAgreement)
                {
                    var ev = (rawEv + pending.Ev) / 2;
                    var learned = Math.Clamp(pending.Brightness, 0, 100);
                    List<CurvePoint> saved;
                    lock (_curveLock)
                    {
                        _curve.Learn(ev, learned);
                        saved = [.. _curve.Points];
                    }
                    _store.Update(s => s.Curve = saved);
                    track.Pending = null;
                    status = $"Learned your adjustment: light level {ev:F1} → {learned}%.";
                    level = StatusLevel.Success;
                    Log.Write($"Learned {monitor.Name}: light {ev:F2} -> {learned}%");
                }
                else
                {
                    track.Pending = pending with { Ev = rawEv }; // the light is still changing; wait for it to settle
                }
            }
            track.Expected = current;

            var (lo, hi) = ms.Range;
            var target = Math.Clamp((int)Math.Round(EvaluateCurve(smoothed) + ms.Offset), lo, hi);

            if (settings.Mode == ControlMode.Auto && PausedUntil is null && note is null)
            {
                var policy = PolicyFor(monitor.Key);
                switch (policy.Decide(current, target, now))
                {
                    case WriteDecision.Write when !steady:
                        note = "Waiting for a second reading";
                        break;
                    case WriteDecision.Write:
                        var steps = BrightnessRamp.Steps(current, target, settings.Transitions, policy.WritesAllowed(current, target, now));
                        // Counted before writing, so a crash mid-fade can't lose writes from the day's total.
                        policy.Record(now, steps.Count);
                        SaveWriteCount(monitor.Key, policy);
                        Log.Write($"Set {monitor.Name}: {current}% -> {target}% in {steps.Count} write(s); {policy.WritesToday} today");
                        try
                        {
                            var (reached, interrupted) = await FadeAsync(monitor.Key, track, steps, settings.Transitions, ct);
                            if (interrupted)
                            {
                                // The user took over mid-fade: treat it like any other manual change.
                                track.Pending = settings.LearnFromManual ? new PendingLearn(reached - ms.Offset, rawEv) : null;
                                PausedUntil = now + TimeSpan.FromMinutes(settings.ManualPauseMinutes);
                                note = $"Stopped at {reached}%: brightness was changed during the fade";
                            }
                            else
                            {
                                note = $"Set {current}% → {target}%";
                            }
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
                        note = "Daily write limit for this monitor reached";
                        break;
                }
            }
            result.Add(new MonitorState(monitor.Key, monitor.Name, current, target, note));
        }

        if (_monitors.Count == 0) (status, level) = ("Twinkle Tray reports no monitors.", StatusLevel.Warning);
        return (result, status, level);
    }

    /// <summary>
    /// Writes <paramref name="steps"/> one at a time so the change is a fade rather than a jump. Stops if someone
    /// else (the user in Twinkle Tray) changes brightness mid-fade, and returns the value reached. The track's
    /// expected brightness follows every write, so a failure part-way isn't mistaken for a manual change.
    /// </summary>
    private async Task<(int Reached, bool Interrupted)> FadeAsync(string key, MonitorTrack track, IReadOnlyList<int> steps,
        TransitionOptions options, CancellationToken ct)
    {
        var last = track.Expected ?? 0;
        var interval = options.StepInterval > MinFadeStepInterval ? options.StepInterval : MinFadeStepInterval;
        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0)
            {
                if (interval > TimeSpan.Zero) await Task.Delay(interval, ct);
                var now = await _backend.GetBrightnessAsync(key, ct);
                if (Math.Abs(now - last) >= ManualTolerance)
                {
                    track.Expected = now;
                    return (now, true);
                }
            }
            await _backend.SetBrightnessAsync(key, steps[i], ct);
            last = steps[i];
            track.Expected = last;
        }
        return (last, false);
    }

    private WritePolicy PolicyFor(string key)
    {
        lock (_policies)
        {
            if (_policies.TryGetValue(key, out var policy)) return policy;
            var s = _store.Current;
            policy = new WritePolicy(s.WritePolicy);
            // Monitors without their own counter start from the single count older versions kept.
            policy.Restore(s.WriteCounters.TryGetValue(key, out var counter) ? counter : new WriteCounter(s.WritesDay, s.WritesToday, null));
            return _policies[key] = policy;
        }
    }

    private void SaveWriteCount(string key, WritePolicy policy)
    {
        var state = policy.State;
        _store.Update(s => s.WriteCounters = new Dictionary<string, WriteCounter>(s.WriteCounters) { [key] = state });
    }

    private void ForgetObservations()
    {
        foreach (var track in _tracks.Values)
        {
            track.Expected = null;
            track.Pending = null;
        }
    }

    private int WritesToday()
    {
        lock (_policies) return _policies.Count == 0 ? 0 : _policies.Values.Max(p => p.WritesToday);
    }

    private void Publish(LightReading? reading, string status, StatusLevel level, IReadOnlyList<MonitorState> monitors,
        DateTime? next, double? smoothed = null, double? curve = null)
    {
        var snapshot = new ControllerSnapshot(DateTime.Now, _store.Current.Mode, reading, smoothed ?? _smoother.Value,
            curve, monitors, status, level, PausedUntil, WritesToday(), next);
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

    private sealed record PendingLearn(int Brightness, double Ev);

    private sealed class MonitorTrack
    {
        /// <summary>Brightness we last observed or wrote; anything else next time is a manual change.</summary>
        public int? Expected;
        /// <summary>A manual change waiting for a steady reading before it is learned.</summary>
        public PendingLearn? Pending;
    }
}
