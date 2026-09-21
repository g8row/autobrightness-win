using AutoBrightness.Camera;

namespace AutoBrightness.Tests;

/// <summary>
/// Renders histograms the way a real camera would: scene radiance times exposure time, encoded with
/// a power-law tone curve on top of a black pedestal, quantized and clipped to 8 bits.
/// </summary>
internal sealed class SyntheticCamera(double black, double gamma)
{
    /// <summary>A scene spread over about five stops, with mean radiance <paramref name="meanRadiance"/>.</summary>
    public static double[] Scene(double meanRadiance, int pixels = 4000)
    {
        var rng = new Random(42);
        var raw = Enumerable.Range(0, pixels).Select(_ => Math.Pow(2, rng.NextDouble() * 5 - 2.5)).ToArray();
        var scale = meanRadiance / raw.Average();
        return raw.Select(r => r * scale).ToArray();
    }

    public Histogram Capture(double[] scene, int exposureLog2, double gainFactor = 1.0)
    {
        var bins = new int[256];
        var t = Math.Pow(2, exposureLog2);
        foreach (var l in scene)
        {
            var signal = Math.Min(1.0, l * t * gainFactor);
            var v = (int)Math.Round(black + (255 - black) * Math.Pow(signal, 1 / gamma));
            bins[Math.Clamp(v, 0, 255)]++;
        }
        return new Histogram(bins);
    }

    /// <summary>A histogram with every pixel at one level.</summary>
    public static Histogram Flat(int level, int pixels = 1000)
    {
        var bins = new int[256];
        bins[level] = pixels;
        return new Histogram(bins);
    }
}
