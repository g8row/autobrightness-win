using System.Diagnostics;

namespace AutoBrightness.Camera;

public sealed record LightReading(
    DateTime Time,
    double Ev,
    int Exposure,
    int Gain,
    double Mean,
    double Clipped,
    MeterVerdict Verdict,
    int Attempts,
    TimeSpan Duration,
    LumaFrame Frame);

/// <summary>
/// Measures relative scene luminance with a locked-exposure camera. The camera is opened only for the
/// duration of a measurement unless a preview holds it open.
/// </summary>
public sealed class LightMeter : IAsyncDisposable
{
    private const int FramesPerReading = 3;
    private const int MaxAttempts = 8;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CameraSession? _session;
    private int _previewRefs;
    private ExposureSetting? _last;
    private bool _gainWorks = true;

    public LightMeter(CameraDevice device, CameraProfile profile)
    {
        Device = device;
        Profile = profile;
    }

    public CameraDevice Device { get; }

    /// <summary>Replaced after calibration; takes effect at the next measurement.</summary>
    public CameraProfile Profile { get; set; }

    public Roi Roi { get; set; } = Roi.Full;

    /// <summary>Frames while a preview is active, raised on the capture thread.</summary>
    public event Action<LumaFrame>? PreviewFrame;

    public bool IsOpen => _session is not null;

    public async Task<LightReading> MeasureAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var session = _session ?? await OpenAsync();
            try
            {
                return await MeterAsync(session, ct);
            }
            catch (CameraException)
            {
                await CloseAsync();
                throw;
            }
            finally
            {
                if (_previewRefs == 0) await CloseAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartPreviewAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _previewRefs++;
            if (_session is null)
            {
                var session = await OpenAsync();
                // Show the locked, metered view rather than the camera's auto-exposed one.
                Apply(session, _last ?? new ExposureSetting(Profile.ExposureStart, 0));
            }
        }
        catch
        {
            _previewRefs--;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopPreviewAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_previewRefs > 0 && --_previewRefs == 0) await CloseAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CameraSession> OpenAsync()
    {
        var session = await CameraSession.OpenAsync(Device);
        if (!session.Controls.ExposureSupported)
        {
            await session.DisposeAsync();
            throw new CameraException(CameraFailure.Unsupported,
                $"{Device.Name} does not allow manual exposure, so it cannot measure light.");
        }
        session.Controls.LockImageProcessing();
        session.FrameArrived += OnFrame;
        _session = session;
        return session;
    }

    private async Task CloseAsync()
    {
        if (_session is null) return;
        _session.FrameArrived -= OnFrame;
        await _session.DisposeAsync();
        _session = null;
    }

    private void OnFrame(LumaFrame f)
    {
        if (_previewRefs > 0) PreviewFrame?.Invoke(f);
    }

    private async Task<LightReading> MeterAsync(CameraSession session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var profile = Profile;
        var gainSteps = profile.GainSupported && _gainWorks ? profile.GainTable.Count : 1;
        var setting = _last ?? new ExposureSetting(profile.ExposureStart, 0);
        setting = new ExposureSetting(
            Math.Clamp(setting.Exposure, profile.ExposureMin, profile.ExposureMax),
            Math.Clamp(setting.GainIndex, 0, gainSteps - 1));
        var effective = profile with { GainSupported = gainSteps > 1 };

        Histogram histogram;
        LumaFrame frame;
        MeterVerdict verdict;
        var attempts = 0;
        while (true)
        {
            attempts++;
            Apply(session, setting);
            await session.SettleAsync(profile.SettleFrames, ct);
            (histogram, frame) = await session.CaptureAsync(FramesPerReading, Roi, ct);
            (verdict, var next) = ExposurePlanner.Decide(histogram, setting, effective);
            if (verdict != MeterVerdict.Adjust || attempts >= MaxAttempts) break;
            setting = next;
        }

        _last = setting;
        var gain = effective.GainTable[setting.GainIndex];
        var ev = profile.Response.Ev(histogram, setting.Exposure, gain.Factor);
        return new LightReading(DateTime.Now, ev, setting.Exposure, gain.Gain, histogram.Mean,
            histogram.FractionAtOrAbove(ExposurePlanner.ClipLevel), verdict, attempts, sw.Elapsed, frame);
    }

    private void Apply(CameraSession session, ExposureSetting s)
    {
        session.Controls.SetExposure(s.Exposure);
        if (Profile.GainSupported && _gainWorks)
        {
            var gain = Profile.GainTable[Math.Clamp(s.GainIndex, 0, Profile.GainTable.Count - 1)].Gain;
            if (session.Controls.Gain != gain && !session.Controls.TrySetGain(gain))
            {
                _gainWorks = false;
                _last = s with { GainIndex = 0 };
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _previewRefs = 0;
            await CloseAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
