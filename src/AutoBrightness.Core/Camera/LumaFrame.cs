namespace AutoBrightness.Camera;

/// <summary>Normalized region of interest (0..1 in both axes).</summary>
public readonly record struct Roi(double X, double Y, double Width, double Height)
{
    public static readonly Roi Full = new(0, 0, 1, 1);

    public Roi Clamp()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        var w = Math.Clamp(Width, 0, 1 - x);
        var h = Math.Clamp(Height, 0, 1 - y);
        return w < 0.02 || h < 0.02 ? Full : new Roi(x, y, w, h);
    }
}

/// <summary>An 8-bit luma image.</summary>
public sealed class LumaFrame(byte[] pixels, int width, int height, int stride, DateTime timestamp)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public DateTime Timestamp { get; } = timestamp;

    public Histogram Histogram(Roi roi)
    {
        roi = roi.Clamp();
        var x0 = (int)(roi.X * Width);
        var y0 = (int)(roi.Y * Height);
        var x1 = Math.Max(x0 + 1, (int)Math.Round((roi.X + roi.Width) * Width));
        var y1 = Math.Max(y0 + 1, (int)Math.Round((roi.Y + roi.Height) * Height));
        var bins = new int[256];
        for (var y = y0; y < y1; y++)
        {
            var row = y * Stride;
            for (var x = x0; x < x1; x++) bins[Pixels[row + x]]++;
        }
        return new Histogram(bins);
    }
}

/// <summary>256-bin luma histogram with the statistics metering needs.</summary>
public sealed class Histogram
{
    private readonly int[] _bins;

    public Histogram(int[] bins)
    {
        if (bins.Length != 256) throw new ArgumentException("histogram needs 256 bins", nameof(bins));
        _bins = bins;
        Count = bins.Sum();
    }

    public int Count { get; }
    public ReadOnlySpan<int> Bins => _bins;

    public double Mean
    {
        get
        {
            long sum = 0;
            for (var i = 0; i < 256; i++) sum += (long)i * _bins[i];
            return Count == 0 ? 0 : (double)sum / Count;
        }
    }

    public int Percentile(double q)
    {
        var target = q * Count;
        var acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += _bins[i];
            if (acc >= target) return i;
        }
        return 255;
    }

    /// <summary>Fraction of pixels at or above <paramref name="level"/>.</summary>
    public double FractionAtOrAbove(int level)
    {
        var n = 0;
        for (var i = level; i < 256; i++) n += _bins[i];
        return Count == 0 ? 0 : (double)n / Count;
    }

    public double Average(Func<int, double> transfer)
    {
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += transfer(i) * _bins[i];
        return Count == 0 ? 0 : sum / Count;
    }
}
