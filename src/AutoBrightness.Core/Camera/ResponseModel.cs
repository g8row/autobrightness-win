namespace AutoBrightness.Camera;

/// <summary>
/// Maps 8-bit luma back to relative linear light: ((v - black) / (255 - black)) ^ gamma.
/// The black pedestal and gamma are fitted per camera during calibration.
/// </summary>
public sealed class ResponseModel
{
    public static readonly ResponseModel Default = new(0, 2.2);

    private readonly double[] _lut = new double[256];

    public ResponseModel(double black, double gamma)
    {
        Black = Math.Clamp(black, 0, 64);
        Gamma = Math.Clamp(gamma, 0.3, 4.0);
        for (var v = 0; v < 256; v++)
        {
            var x = (v - Black) / (255 - Black);
            _lut[v] = x <= 0 ? 0 : Math.Pow(x, Gamma);
        }
    }

    public double Black { get; }
    public double Gamma { get; }

    public double Linear(int v) => _lut[Math.Clamp(v, 0, 255)];

    /// <summary>Average linear light over the histogram (averaging in linear space, not in gamma space).</summary>
    public double MeanLinear(Histogram h) => h.Average(Linear);

    /// <summary>
    /// Relative scene luminance in stops: log2(linear) minus the log2 of the light the camera integrated.
    /// Independent of exposure and gain once the model is calibrated.
    /// </summary>
    public double Ev(Histogram h, int exposureLog2, double gainFactor)
    {
        var lin = Math.Max(MeanLinear(h), 1e-6);
        return Math.Log2(lin) - exposureLog2 - Math.Log2(Math.Max(gainFactor, 1e-6));
    }
}
