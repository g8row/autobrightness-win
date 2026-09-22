using AutoBrightness.Camera;
using AutoBrightness.Control;
using AutoBrightness.Settings;
using AutoBrightness.Twinkle;

namespace AutoBrightness.Tests;

public sealed class ControllerTests : IDisposable
{
    private const string Monitor = "UID260";
    private readonly string _dir = Directory.CreateTempSubdirectory("ab-tests-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly FakeMeter _meter = new();
    private SettingsStore _store = null!;

    public ControllerTests() => AppPaths.DataDirectory = _dir;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AutoBrightnessController> CreateAsync(ControlMode mode, Action<AppSettings>? configure = null)
    {
        _store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _store.Update(s =>
        {
            s.Mode = mode;
            s.Curve = [new(0, 10), new(4, 50), new(8, 90)];
            s.WritePolicy = new WritePolicyOptions { MinInterval = TimeSpan.Zero };
            s.Transitions = new TransitionOptions { Enabled = true, StepInterval = TimeSpan.Zero };
            configure?.Invoke(s);
        });
        var controller = new AutoBrightnessController(_store, _backend, (_, _) => _meter) { MinFadeStepInterval = TimeSpan.Zero };
        await controller.SelectCameraAsync(new CameraDevice(@"\\?\USB#VID_046D&PID_0819#x", "Test camera"));
        return controller;
    }

    /// <summary>Two samples: the controller never writes on the first reading after a reset.</summary>
    private static async Task SettleAsync(AutoBrightnessController c)
    {
        await c.SampleOnceAsync(Ct);
        await c.SampleOnceAsync(Ct);
    }

    [Fact]
    public async Task PreviewNeverWrites()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 8;
        await SettleAsync(c);

        Assert.Empty(_backend.Writes);
        var m = Assert.Single(c.Last!.Monitors);
        Assert.Equal(90, m.Target);
        Assert.Equal(30, m.Current);
    }

    [Fact]
    public async Task FirstReadingDoesNotWrite()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 8;
        await c.SampleOnceAsync(Ct);

        Assert.Empty(_backend.Writes);
        Assert.Equal("Waiting for a second reading", c.Last!.Monitors.Single().Note);
    }

    [Fact]
    public async Task AutoWritesCurveTarget()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 4;
        await SettleAsync(c);

        // 30 -> 50 fades in 2% steps.
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (Monitor, 30 + 2 * i)), _backend.Writes);
    }

    [Fact]
    public async Task EveryFadeStepCountsAsAWriteAndIsSaved()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 4;
        await SettleAsync(c);

        Assert.Equal(10, c.Last!.WritesToday);
        Assert.Equal(10, new SettingsStore(Path.Combine(_dir, "settings.json")).Current.WriteCounters[Monitor].Count);
    }

    [Fact]
    public async Task FadeIsShortenedToFitTheBudget()
    {
        await using var c = await CreateAsync(ControlMode.Auto, s => s.WritePolicy = s.WritePolicy with { DailyBudget = 3 });
        _meter.Ev = 4; // 30 -> 50 is a small change: 3 writes allowed
        await SettleAsync(c);

        Assert.Equal([(Monitor, 37), (Monitor, 43), (Monitor, 50)], _backend.Writes);
    }

    [Fact]
    public async Task ManualChangeIsLearnedAtTheNextSteadyReadingAndPausesAuto()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 4;
        await SettleAsync(c); // fades to 50
        var writes = _backend.Writes.Count;

        _backend.Brightness = 35; // user drags Twinkle Tray's slider
        await c.SampleOnceAsync(Ct);
        Assert.NotNull(c.PausedUntil);
        Assert.Equal(50, c.EvaluateCurve(4), 6); // not yet: waiting for a steady reading

        await c.SampleOnceAsync(Ct);
        Assert.Equal(35, c.EvaluateCurve(4), 6);
        Assert.Equal(writes, _backend.Writes.Count);
        Assert.Equal(StatusLevel.Success, c.Last!.Level);
    }

    [Fact]
    public async Task AdjustmentAfterLightsOffIsLearnedAtTheNewLightLevel()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 8;
        await SettleAsync(c);
        await c.SampleOnceAsync(Ct);

        // Lights off and the user dims straight away; the smoothed level still says "bright".
        _meter.Ev = 0;
        _backend.Brightness = 5;
        await c.SampleOnceAsync(Ct);
        await c.SampleOnceAsync(Ct);

        Assert.Equal(5, c.EvaluateCurve(0), 6);
        Assert.True(c.EvaluateCurve(8) > 50, "the bright end of the curve should survive");
    }

    [Fact]
    public async Task ChangingLightDelaysLearning()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 2;
        await SettleAsync(c);

        _backend.Brightness = 45;
        await c.SampleOnceAsync(Ct);
        _meter.Ev = 3; // still changing
        await c.SampleOnceAsync(Ct);
        Assert.NotEqual(45, c.EvaluateCurve(2.5), 6);

        await c.SampleOnceAsync(Ct); // steady at 3
        Assert.Equal(45, c.EvaluateCurve(3), 6);
    }

    [Fact]
    public async Task FadeStopsWhenUserTakesOverAndStaysPaused()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 8; // target 90 from 30
        await c.SampleOnceAsync(Ct);
        _backend.OnSet = n => { if (n == 3) _backend.Brightness = 70; }; // user drags the slider mid-fade
        await c.SampleOnceAsync(Ct);

        Assert.Equal(3, _backend.Writes.Count);
        Assert.Contains("Stopped at 70%", c.Last!.Monitors.Single().Note);
        Assert.NotNull(c.PausedUntil);

        await c.SampleOnceAsync(Ct);
        Assert.Equal(3, _backend.Writes.Count); // the user's 70% stands
        Assert.Equal(70, c.EvaluateCurve(8), 6);
    }

    [Fact]
    public async Task FailedFadeIsNotMistakenForAManualChange()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 4;
        _backend.FailOnSet = 4;
        await SettleAsync(c);
        Assert.Equal(3, _backend.Writes.Count);

        _backend.FailOnSet = null;
        await c.SampleOnceAsync(Ct);

        Assert.Null(c.PausedUntil);
        Assert.Equal(50, c.EvaluateCurve(4), 6);
    }

    [Fact]
    public async Task FadeCanBeTurnedOff()
    {
        await using var c = await CreateAsync(ControlMode.Auto, s => s.Transitions = s.Transitions with { Enabled = false });
        _meter.Ev = 4;
        await SettleAsync(c);
        Assert.Equal([(Monitor, 50)], _backend.Writes);
    }

    [Fact]
    public async Task PreviewLearnsWithoutPausing()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 2;
        await SettleAsync(c);
        _backend.Brightness = 45;
        await c.SampleOnceAsync(Ct);
        await c.SampleOnceAsync(Ct);

        Assert.Null(c.PausedUntil);
        Assert.Equal(45, c.EvaluateCurve(2), 6);
    }

    [Fact]
    public async Task ChangesWhileOffAreNotLearned()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 2;
        await SettleAsync(c);

        _store.Update(s => s.Mode = ControlMode.Off);
        await c.SampleOnceAsync(Ct);
        _backend.Brightness = 80;
        _store.Update(s => s.Mode = ControlMode.Preview);
        await SettleAsync(c);

        Assert.Equal(30, c.EvaluateCurve(2), 6);
        Assert.NotEqual("Manual change", c.Last!.Monitors.Single().Note);
    }

    [Fact]
    public async Task HoldStopsTheCameraAndForgetsWhatHappenedMeanwhile()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 2;
        await SettleAsync(c);
        var measured = _meter.Measurements;

        c.SetHold("the screen is locked");
        await c.SampleOnceAsync(Ct);
        Assert.Equal(measured, _meter.Measurements);
        Assert.Contains("locked", c.Last!.Status);

        _backend.Brightness = 80;
        c.SetHold(null);
        await SettleAsync(c);
        Assert.Equal(30, c.EvaluateCurve(2), 6);
    }

    [Fact]
    public async Task MonitorLimitsInEitherOrderWork()
    {
        await using var c = await CreateAsync(ControlMode.Preview,
            s => s.Monitors = [new MonitorSettings { Key = Monitor, Min = 80, Max = 20 }]);
        _meter.Ev = 8;
        await SettleAsync(c);

        Assert.NotEqual(StatusLevel.Error, c.Last!.Level);
        Assert.Equal(80, c.Last.Monitors.Single().Target);
    }

    [Fact]
    public async Task EachMonitorHasItsOwnLimits()
    {
        _backend.Levels["UID261"] = 30;
        await using var c = await CreateAsync(ControlMode.Auto,
            s => s.Transitions = s.Transitions with { Enabled = false });
        _meter.Ev = 4;
        await SettleAsync(c);

        Assert.Equal([(Monitor, 50), ("UID261", 50)], _backend.Writes);
    }

    [Fact]
    public async Task MeasurementAreaChangeShiftsTheCurve()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 4;
        await SettleAsync(c);
        var before = c.CurvePoints.Select(p => p.Ev).ToList();

        await c.SetRoiAsync(new Roi(0.5, 0, 0.5, 1)); // the bright half of the frame

        var shift = c.CurvePoints[0].Ev - before[0];
        Assert.True(shift > 0.5, $"shift {shift}");
        Assert.All(c.CurvePoints.Zip(before), x => Assert.Equal(shift, x.First.Ev - x.Second, 2));
    }

    [Fact]
    public async Task TwinkleTrayMissingIsAWarning()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _backend.Running = false;
        await c.SampleOnceAsync(Ct);

        Assert.Equal(StatusLevel.Warning, c.Last!.Level);
        Assert.Contains("Twinkle Tray", c.Last.Status);
        Assert.NotNull(c.Last.Reading);
    }

    [Fact]
    public async Task OffModeDoesNotUseTheCamera()
    {
        await using var c = await CreateAsync(ControlMode.Off);
        await c.SampleOnceAsync(Ct);
        Assert.Equal(0, _meter.Measurements);
    }

    [Fact]
    public async Task CameraFailureIsAnError()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Fail = new CameraException(CameraFailure.Stalled, "stalled");
        await c.SampleOnceAsync(Ct);

        Assert.Equal(StatusLevel.Error, c.Last!.Level);
        Assert.Empty(_backend.Writes);
    }

    private sealed class FakeBackend : IBrightnessBackend
    {
        public readonly Dictionary<string, int> Levels = new() { [Monitor] = 30 };
        public bool Running = true;
        public List<(string, int)> Writes { get; } = [];
        public Action<int>? OnSet;
        /// <summary>The write with this number (1-based) fails without taking effect.</summary>
        public int? FailOnSet;

        public int Brightness
        {
            get => Levels[Monitor];
            set => Levels[Monitor] = value;
        }

        public Task<IReadOnlyList<TwinkleMonitor>> ListAsync(CancellationToken ct = default) => Running
            ? Task.FromResult<IReadOnlyList<TwinkleMonitor>>(Levels.Select(l => new TwinkleMonitor(l.Key, "27GL650F", "ddcci", l.Value)).ToList())
            : throw new TwinkleUnavailableException("Twinkle Tray is not responding.");

        public Task<int> GetBrightnessAsync(string monitorKey, CancellationToken ct = default) => Running
            ? Task.FromResult(Levels[monitorKey])
            : throw new TwinkleUnavailableException("Twinkle Tray is not responding.");

        public Task SetBrightnessAsync(string monitorKey, int percent, CancellationToken ct = default)
        {
            if (FailOnSet == Writes.Count + 1) throw new TwinkleUnavailableException("Twinkle Tray is not responding.");
            Writes.Add((monitorKey, percent));
            Levels[monitorKey] = percent;
            OnSet?.Invoke(Writes.Count);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMeter : ILightMeter
    {
        public double Ev;
        public int Measurements;
        public CameraException? Fail;

        public CameraDevice Device { get; } = new("id", "Test camera");
        public CameraProfile Profile { get; set; } = CameraProfile.Uncalibrated("k", "Test camera") with { CalibratedAt = DateTime.Now };
        public Roi Roi { get; set; } = Roi.Full;
        public event Action<LumaFrame>? PreviewFrame { add { } remove { } }

        /// <summary>Left half dim, right half bright.</summary>
        private static LumaFrame Frame() => new([20, 200, 20, 200], 2, 2, 2, DateTime.UtcNow);

        public Task<LightReading> MeasureAsync(CancellationToken ct = default)
        {
            Measurements++;
            if (Fail is not null) throw Fail;
            return Task.FromResult(new LightReading(DateTime.Now, Ev, -6, 0, 80, 0, MeterVerdict.Good, 1, TimeSpan.Zero, Frame()));
        }

        public Task StartPreviewAsync() => Task.CompletedTask;
        public Task StopPreviewAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
