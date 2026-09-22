namespace AutoBrightness.Camera;

public sealed record CalibrationProgress(double Fraction, string Step, string? Detail = null);

/// <summary>
/// Measures a camera's metering characteristics against a static scene:
/// usable exposure range, settle time, black pedestal and response curve (from an exposure sweep),
/// gain sensitivity table, and noise. The scene must not change while it runs (about 20-40 s).
/// </summary>
public sealed class Calibrator(CameraDevice device)
{
    private const int FramesPerPoint = 4;
    private static readonly int[] GainCandidates = [0, 16, 32, 48, 64, 96, 128, 160, 192, 224, 255];

    public async Task<CameraProfile> RunAsync(IProgress<CalibrationProgress>? progress = null, CancellationToken ct = default)
    {
        void Report(double f, string step, string? detail = null) => progress?.Report(new CalibrationProgress(f, step, detail));
        var notes = new List<string>();

        Report(0, "Opening camera");
        await using var session = await CameraSession.OpenAsync(device);
        var c = session.Controls;
        if (!c.ExposureSupported)
            throw new CameraException(CameraFailure.Unsupported, $"{device.Name} does not allow manual exposure, so it cannot measure light.");
        c.LockImageProcessing();

        // Exposure range. Exposures longer than one frame period lower the frame rate, and on some cameras
        // (Logitech 046D:0819) hang the stream, so the usable maximum is capped at the frame period.
        var caps = c.ExposureCaps;
        var expMin = (int)Math.Ceiling(caps.Min);
        var framePeriodLog2 = (int)Math.Floor(Math.Log2(1.0 / Math.Max(1, session.FrameRate)));
        var expMax = Math.Min((int)Math.Floor(caps.Max), framePeriodLog2);
        Report(0.05, "Exposure range", $"{FormatExposure(expMin)} to {FormatExposure(expMax)} (camera allows up to {FormatExposure((int)caps.Max)}; capped at the {session.FrameRate:F0} fps frame period)");

        // Gain: usable only if writes are confirmed by read-back.
        var gainRange = c.GainRange;
        var gainAtStart = c.Gain;
        var gainMin = gainRange?.Min ?? 0;
        var gainWritable = gainAtStart is not null && c.TrySetGain(gainMin);
        Report(0.08, "Gain", gainWritable
            ? $"writable, range {gainRange?.Min.ToString() ?? "?"}..{gainRange?.Max.ToString() ?? "?"}"
            : "not writable through Frame Server; exposure only");
        if (!gainWritable) notes.Add("Gain could not be set; metering uses exposure only.");

        // Settle time: frames until the image stops changing after an exposure step.
        var start = Math.Clamp(-6, expMin, expMax);
        c.SetExposure(start);
        await session.SettleAsync(5, ct);
        c.SetExposure(Math.Max(expMin, start - 2));
        var means = new List<double>();
        for (var i = 0; i < 15; i++) means.Add((await session.NextFrameAsync(ct)).Histogram(Roi.Full).Mean);
        var settle = SettleFrames(means);
        Report(0.15, "Settle time", $"{settle} frame(s)");

        // Exposure sweep.
        var sweep = new List<(int Exposure, Histogram Histogram)>();
        for (var e = expMin; e <= expMax; e++)
        {
            ct.ThrowIfCancellationRequested();
            c.SetExposure(e);
            await session.SettleAsync(settle + 1, ct);
            var (h, _) = await session.CaptureAsync(FramesPerPoint, Roi.Full, ct);
            sweep.Add((e, h));
            Report(0.15 + 0.5 * (e - expMin + 1) / (expMax - expMin + 1), "Exposure sweep",
                $"{FormatExposure(e)}: mean {h.Mean:F1}, clipped {h.FractionAtOrAbove(ExposurePlanner.ClipLevel):P0}");
        }

        var (black, gamma, spread, used) = FitResponse(sweep);
        Report(0.7, "Response curve", $"black level {black:F1}, gamma {gamma:F2} from {used} exposures (consistency ±{spread:F2} stops)");
        if (used < 3)
            throw new CameraException(CameraFailure.UnsuitableScene,
                "Not enough light to calibrate: fewer than three exposures gave a usable picture. " +
                "Turn on a light or point the camera at a lit wall, then try again. The previous calibration is unchanged.");
        var response = new ResponseModel(black, gamma);

        // Noise at the exposure closest to a mid-grey image.
        var mid = sweep.OrderBy(p => Math.Abs(p.Histogram.Mean - 70)).First().Exposure;
        c.SetExposure(mid);
        await session.SettleAsync(settle + 1, ct);
        var noiseMeans = new List<double>();
        for (var i = 0; i < 12; i++) noiseMeans.Add((await session.NextFrameAsync(ct)).Histogram(Roi.Full).Mean);
        var noise = StdDev(noiseMeans);
        Report(0.78, "Noise", $"{noise:F2} levels at {FormatExposure(mid)}");

        // Gain table, measured at an exposure dark enough to leave headroom.
        var gainTable = new List<GainStep> { new(gainMin, 1.0) };
        if (gainWritable)
        {
            // A dim but measurable exposure; if every usable one is brighter than that, the shortest usable one.
            // (Never a near-black one: its linear light is close to zero and every gain factor would blow up.)
            var dim = sweep.Where(p => p.Histogram.Mean is >= 12 and < 45).Select(p => (int?)p.Exposure).LastOrDefault()
                      ?? sweep.First(p => p.Histogram.Mean >= 12).Exposure;
            c.SetExposure(dim);
            double? baseline = null;
            var candidates = GainCandidates.Where(g => g >= gainMin && (gainRange is null || g <= gainRange.Value.Max)).ToList();
            foreach (var g in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!c.TrySetGain(g)) break;
                await session.SettleAsync(settle + 1, ct);
                var (h, _) = await session.CaptureAsync(FramesPerPoint, Roi.Full, ct);
                if (h.FractionAtOrAbove(ExposurePlanner.ClipLevel) > ExposurePlanner.MaxClipped) break;
                var lin = response.MeanLinear(h);
                baseline ??= lin;
                var factor = lin / Math.Max(baseline.Value, 1e-9);
                if (g != gainMin && factor > gainTable[^1].Factor * 1.15) gainTable.Add(new GainStep(g, factor));
                Report(0.78 + 0.2 * (candidates.IndexOf(g) + 1) / candidates.Count, "Gain table", $"gain {g}: x{factor:F2}");
            }
            c.TrySetGain(gainMin);
            if (gainTable.Count < 2) notes.Add("Gain had no measurable effect; metering uses exposure only.");
        }

        Report(1, "Done");
        return new CameraProfile
        {
            Key = device.Key,
            Name = device.Name,
            ExposureMin = expMin,
            ExposureMax = expMax,
            ExposureStart = Math.Clamp(mid, expMin, expMax),
            GainSupported = gainTable.Count > 1,
            GainTable = gainTable,
            Black = black,
            Gamma = gamma,
            SettleFrames = settle,
            NoiseLevels = noise,
            CalibratedAt = DateTime.Now,
            Notes = notes,
        };
    }

    public static string FormatExposure(int log2) => log2 >= 0 ? $"{Math.Pow(2, log2):0.##} s" : $"1/{Math.Pow(2, -log2):0} s";

    /// <summary>First frame index after which every mean stays within 2% (or 1 level) of the final value.</summary>
    internal static int SettleFrames(IReadOnlyList<double> means)
    {
        var final = means.Skip(means.Count - 3).Average();
        var tolerance = Math.Max(1.0, final * 0.02);
        for (var i = 0; i < means.Count; i++)
            if (means.Skip(i).All(m => Math.Abs(m - final) <= tolerance))
                return Math.Clamp(i, 1, 10);
        return 10;
    }

    /// <summary>
    /// Fits the black pedestal and gamma together: the right model makes log2(mean linear light) - exposure
    /// the same at every well-exposed point of a static scene. Only points well above any plausible pedestal
    /// are used, so near-black frames (dominated by quantization) do not drive the fit.
    /// </summary>
    internal static (double Black, double Gamma, double Spread, int Used) FitResponse(IReadOnlyList<(int Exposure, Histogram Histogram)> sweep)
    {
        var usable = sweep
            .Where(p => p.Histogram.Mean is >= 12 and <= 200 && p.Histogram.FractionAtOrAbove(ExposurePlanner.ClipLevel) <= 0.01)
            .ToList();
        if (usable.Count < 3) return (0, 2.2, double.NaN, usable.Count);

        // The pedestal cannot exceed what the darkest frame shows.
        var maxBlack = Math.Min(20, sweep.OrderBy(p => p.Exposure).First().Histogram.Percentile(0.5));
        double Spread(double black, double gamma)
        {
            var model = new ResponseModel(black, gamma);
            return StdDev(usable.Select(p => model.Ev(p.Histogram, p.Exposure, 1.0)).ToList());
        }

        var best = (Black: 0.0, Gamma: 2.2, Spread: double.MaxValue);
        for (var b = 0.0; b <= maxBlack; b += 0.5)
        for (var g = 0.5; g <= 3.5; g += 0.02)
        {
            var s = Spread(b, g);
            if (s < best.Spread) best = (b, g, s);
        }
        for (var g = best.Gamma - 0.02; g <= best.Gamma + 0.02; g += 0.002)
        {
            var s = Spread(best.Black, g);
            if (s < best.Spread) best = (best.Black, g, s);
        }
        return (best.Black, Math.Round(best.Gamma, 3), best.Spread, usable.Count);
    }

    private static double StdDev(IReadOnlyCollection<double> xs)
    {
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / xs.Count);
    }
}
