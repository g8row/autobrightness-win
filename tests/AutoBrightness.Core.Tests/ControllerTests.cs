using AutoBrightness.Camera;
using AutoBrightness.Control;
using AutoBrightness.Settings;
using AutoBrightness.Twinkle;

namespace AutoBrightness.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ab-tests-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly FakeMeter _meter = new();

    public ControllerTests() => AppPaths.DataDirectory = _dir;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<AutoBrightnessController> CreateAsync(ControlMode mode)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Update(s =>
        {
            s.Mode = mode;
            s.Curve = [new(0, 10), new(4, 50), new(8, 90)];
            s.WritePolicy = new WritePolicyOptions { MinInterval = TimeSpan.Zero };
        });
        var controller = new AutoBrightnessController(store, _backend, (_, _) => _meter);
        await controller.SelectCameraAsync(new CameraDevice(@"\\?\USB#VID_046D&PID_0819#x", "Test camera"));
        return controller;
    }

    [Fact]
    public async Task PreviewNeverWrites()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 8;
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_backend.Writes);
        var m = Assert.Single(c.Last!.Monitors);
        Assert.Equal(90, m.Target);
        Assert.Equal(30, m.Current);
    }

    [Fact]
    public async Task AutoWritesCurveTarget()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 4;
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal([("UID260", 50)], _backend.Writes);
    }

    [Fact]
    public async Task ManualChangeIsLearnedAndPausesAuto()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Ev = 4;
        await c.SampleOnceAsync(TestContext.Current.CancellationToken); // writes 50

        _backend.Brightness = 35; // user drags Twinkle Tray's slider
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(c.PausedUntil);
        Assert.Equal(35, c.EvaluateCurve(4), 6);
        Assert.Single(_backend.Writes);
        Assert.Equal(StatusLevel.Success, c.Last!.Level);
    }

    [Fact]
    public async Task PreviewLearnsWithoutPausing()
    {
        await using var c = await CreateAsync(ControlMode.Preview);
        _meter.Ev = 2;
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);
        _backend.Brightness = 45;
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(c.PausedUntil);
        Assert.Equal(45, c.EvaluateCurve(2), 6);
    }

    [Fact]
    public async Task OffModeDoesNotUseTheCamera()
    {
        await using var c = await CreateAsync(ControlMode.Off);
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _meter.Measurements);
    }

    [Fact]
    public async Task TwinkleTrayMissingIsAWarning()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _backend.Running = false;
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusLevel.Warning, c.Last!.Level);
        Assert.Contains("Twinkle Tray", c.Last.Status);
        Assert.NotNull(c.Last.Reading);
    }

    [Fact]
    public async Task CameraFailureIsAnError()
    {
        await using var c = await CreateAsync(ControlMode.Auto);
        _meter.Fail = new CameraException(CameraFailure.Stalled, "stalled");
        await c.SampleOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusLevel.Error, c.Last!.Level);
        Assert.Empty(_backend.Writes);
    }

    private sealed class FakeBackend : IBrightnessBackend
    {
        public int Brightness = 30;
        public bool Running = true;
        public List<(string, int)> Writes { get; } = [];

        public Task<IReadOnlyList<TwinkleMonitor>> ListAsync(CancellationToken ct = default) => Running
            ? Task.FromResult<IReadOnlyList<TwinkleMonitor>>([new TwinkleMonitor("UID260", "27GL650F", "ddcci", Brightness)])
            : throw new TwinkleUnavailableException("Twinkle Tray is not responding.");

        public Task<int> GetBrightnessAsync(string monitorKey, CancellationToken ct = default) => Running
            ? Task.FromResult(Brightness)
            : throw new TwinkleUnavailableException("Twinkle Tray is not responding.");

        public Task SetBrightnessAsync(string monitorKey, int percent, CancellationToken ct = default)
        {
            Writes.Add((monitorKey, percent));
            Brightness = percent;
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

        public Task<LightReading> MeasureAsync(CancellationToken ct = default)
        {
            Measurements++;
            if (Fail is not null) throw Fail;
            return Task.FromResult(new LightReading(DateTime.Now, Ev, -6, 0, 80, 0, MeterVerdict.Good, 1, TimeSpan.Zero,
                new LumaFrame(new byte[4], 2, 2, 2, DateTime.UtcNow)));
        }

        public Task StartPreviewAsync() => Task.CompletedTask;
        public Task StopPreviewAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
