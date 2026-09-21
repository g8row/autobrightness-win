namespace AutoBrightness.Camera;

/// <summary>Where the camera should go next to bring the metered region into its useful range.</summary>
public readonly record struct ExposureSetting(int Exposure, int GainIndex);

public enum MeterVerdict { Good, Adjust, Saturated, TooDark }

/// <summary>
/// Chooses exposure and gain so the region's histogram sits in the camera's usable range: not clipped
/// (clipping loses information) and not near the black pedestal (quantization and noise dominate).
/// Exposure moves first because it is noise-free; gain is only used once exposure hits its frame-period cap.
/// </summary>
public static class ExposurePlanner
{
    public const int ClipLevel = 250;
    public const double MaxClipped = 0.02;
    public const double DarkMean = 25;
    public const double BrightMean = 180;

    public static (MeterVerdict Verdict, ExposureSetting Next) Decide(Histogram h, ExposureSetting current, CameraProfile profile)
    {
        var gainSteps = profile.GainSupported ? profile.GainTable.Count : 1;
        var clipped = h.FractionAtOrAbove(ClipLevel);
        var mean = h.Mean;

        if (clipped > MaxClipped || mean > BrightMean)
        {
            if (current.GainIndex > 0) return (MeterVerdict.Adjust, current with { GainIndex = current.GainIndex - 1 });
            if (current.Exposure > profile.ExposureMin)
            {
                var step = clipped > 0.25 ? 2 : 1;
                return (MeterVerdict.Adjust, current with { Exposure = Math.Max(profile.ExposureMin, current.Exposure - step) });
            }
            return (MeterVerdict.Saturated, current);
        }

        if (mean < DarkMean)
        {
            if (current.Exposure < profile.ExposureMax)
            {
                // Each stop roughly doubles linear light but less than doubles gamma-encoded luma, so jump two
                // stops only when the image is near black.
                var step = mean < 8 ? 2 : 1;
                return (MeterVerdict.Adjust, current with { Exposure = Math.Min(profile.ExposureMax, current.Exposure + step) });
            }
            if (current.GainIndex < gainSteps - 1) return (MeterVerdict.Adjust, current with { GainIndex = current.GainIndex + 1 });
            return (MeterVerdict.TooDark, current);
        }

        return (MeterVerdict.Good, current);
    }
}
