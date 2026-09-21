using AutoBrightness.Camera;

namespace AutoBrightness.Tests;

public class HistogramTests
{
    [Fact]
    public void Statistics()
    {
        var bins = new int[256];
        bins[10] = 50;
        bins[100] = 40;
        bins[255] = 10;
        var h = new Histogram(bins);

        Assert.Equal(100, h.Count);
        Assert.Equal((10 * 50 + 100 * 40 + 255 * 10) / 100.0, h.Mean, 6);
        Assert.Equal(10, h.Percentile(0.5));
        Assert.Equal(100, h.Percentile(0.9));
        Assert.Equal(0.1, h.FractionAtOrAbove(250), 6);
    }

    [Fact]
    public void FrameHistogramRespectsRoi()
    {
        // Left half black, right half white.
        var pixels = new byte[4 * 2];
        pixels[2] = pixels[3] = pixels[6] = pixels[7] = 200;
        var frame = new LumaFrame(pixels, 4, 2, 4, DateTime.UtcNow);

        Assert.Equal(100, frame.Histogram(Roi.Full).Mean, 6);
        Assert.Equal(200, frame.Histogram(new Roi(0.5, 0, 0.5, 1)).Mean, 6);
        Assert.Equal(0, frame.Histogram(new Roi(0, 0, 0.5, 1)).Mean, 6);
    }

    [Fact]
    public void DegenerateRoiFallsBackToFullFrame() =>
        Assert.Equal(Roi.Full, new Roi(0.5, 0.5, 0, 0).Clamp());
}

public class ResponseModelTests
{
    [Fact]
    public void CalibratedModelGivesSameEvAtEveryExposure()
    {
        var camera = new SyntheticCamera(black: 4, gamma: 1.3);
        var scene = SyntheticCamera.Scene(meanRadiance: 4);
        var model = new ResponseModel(4, 1.3);

        var evs = new[] { -9, -8, -7, -6 }.Select(e => model.Ev(camera.Capture(scene, e), e, 1.0)).ToList();

        Assert.True(evs.Max() - evs.Min() < 0.1, $"EVs spread {evs.Max() - evs.Min():F3}: {string.Join(", ", evs)}");
        Assert.Equal(Math.Log2(4), evs.Average(), 1);
    }

    [Fact]
    public void GainFactorIsCompensated()
    {
        var camera = new SyntheticCamera(4, 1.3);
        var scene = SyntheticCamera.Scene(1);
        var model = new ResponseModel(4, 1.3);

        var plain = model.Ev(camera.Capture(scene, -7), -7, 1.0);
        var boosted = model.Ev(camera.Capture(scene, -7, gainFactor: 2), -7, 2.0);

        Assert.Equal(plain, boosted, 1);
    }

    [Theory]
    [InlineData(4, 1.25)]
    [InlineData(0, 2.2)]
    [InlineData(8, 0.9)]
    public void FitResponseRecoversBlackAndGamma(double black, double gamma)
    {
        var camera = new SyntheticCamera(black, gamma);
        var scene = SyntheticCamera.Scene(8);
        var sweep = Enumerable.Range(-13, 9).Select(e => (e, camera.Capture(scene, e))).ToList();

        var (fittedBlack, fitted, spread, used) = Calibrator.FitResponse(sweep);

        Assert.InRange(fittedBlack, black - 1, black + 1);
        Assert.InRange(fitted, gamma - 0.15, gamma + 0.15);
        Assert.True(used >= 3);
        Assert.True(spread < 0.1);
    }

    [Fact]
    public void SettleFramesFindsWhenImageStopsChanging()
    {
        double[] means = [80, 60, 45, 41, 40, 40.2, 39.9, 40.1, 40, 40];
        Assert.Equal(3, Calibrator.SettleFrames(means));
    }

    [Theory]
    [InlineData(-13, "1/8192 s")]
    [InlineData(-5, "1/32 s")]
    [InlineData(0, "1 s")]
    public void FormatsExposure(int log2, string expected) => Assert.Equal(expected, Calibrator.Seconds(log2));
}

public class ExposurePlannerTests
{
    private static readonly CameraProfile Profile = new()
    {
        Key = "k", Name = "n", ExposureMin = -13, ExposureMax = -5,
        GainSupported = true, GainTable = [new(0, 1), new(64, 2), new(128, 4)],
    };

    [Fact]
    public void GoodExposureIsKept()
    {
        var (verdict, next) = ExposurePlanner.Decide(SyntheticCamera.Flat(90), new(-6, 0), Profile);
        Assert.Equal(MeterVerdict.Good, verdict);
        Assert.Equal(new ExposureSetting(-6, 0), next);
    }

    [Fact]
    public void ClippedImageShortensExposure()
    {
        var (verdict, next) = ExposurePlanner.Decide(SyntheticCamera.Flat(255), new(-6, 0), Profile);
        Assert.Equal(MeterVerdict.Adjust, verdict);
        Assert.Equal(-8, next.Exposure);
    }

    [Fact]
    public void ClippedImageDropsGainBeforeExposure()
    {
        var (_, next) = ExposurePlanner.Decide(SyntheticCamera.Flat(255), new(-5, 2), Profile);
        Assert.Equal(new ExposureSetting(-5, 1), next);
    }

    [Fact]
    public void DarkImageLengthensExposureThenRaisesGain()
    {
        Assert.Equal(new ExposureSetting(-7, 0), ExposurePlanner.Decide(SyntheticCamera.Flat(5), new(-9, 0), Profile).Next);
        Assert.Equal(new ExposureSetting(-5, 1), ExposurePlanner.Decide(SyntheticCamera.Flat(15), new(-5, 0), Profile).Next);
    }

    [Fact]
    public void ReportsLimits()
    {
        Assert.Equal(MeterVerdict.TooDark, ExposurePlanner.Decide(SyntheticCamera.Flat(3), new(-5, 2), Profile).Verdict);
        Assert.Equal(MeterVerdict.Saturated, ExposurePlanner.Decide(SyntheticCamera.Flat(255), new(-13, 0), Profile).Verdict);
    }

    [Fact]
    public void GainIsIgnoredWhenUnsupported()
    {
        var noGain = Profile with { GainSupported = false };
        Assert.Equal(MeterVerdict.TooDark, ExposurePlanner.Decide(SyntheticCamera.Flat(3), new(-5, 0), noGain).Verdict);
    }

    [Fact]
    public void PlannerConvergesOnSyntheticCamera()
    {
        var camera = new SyntheticCamera(4, 1.3);
        foreach (var radiance in new[] { 0.5, 5, 50, 500 })
        {
            var scene = SyntheticCamera.Scene(radiance);
            var setting = new ExposureSetting(-6, 0);
            var verdict = MeterVerdict.Adjust;
            for (var i = 0; i < 8 && verdict == MeterVerdict.Adjust; i++)
            {
                var gain = Profile.GainTable[setting.GainIndex].Factor;
                (verdict, var next) = ExposurePlanner.Decide(camera.Capture(scene, setting.Exposure, gain), setting, Profile);
                if (verdict == MeterVerdict.Adjust) setting = next;
            }
            Assert.NotEqual(MeterVerdict.Adjust, verdict);
        }
    }
}
