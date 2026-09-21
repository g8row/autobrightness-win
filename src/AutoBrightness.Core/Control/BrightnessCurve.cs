namespace AutoBrightness.Control;

/// <summary>A curve point: light level in stops (camera-relative EV) to brightness percent.</summary>
public sealed record CurvePoint(double Ev, double Brightness, bool Learned = false);

/// <summary>
/// Monotonic piecewise-linear map from light level to brightness. Points added from manual adjustments
/// replace nearby points and evict any that would make the curve decrease.
/// </summary>
public sealed class BrightnessCurve
{
    /// <summary>Learned points closer than this (in stops) replace each other.</summary>
    public const double MergeDistance = 0.75;

    private readonly List<CurvePoint> _points;

    public BrightnessCurve(IEnumerable<CurvePoint> points)
    {
        _points = points.OrderBy(p => p.Ev).ToList();
        if (_points.Count == 0) _points.AddRange(Default);
        EnforceMonotonic();
    }

    /// <summary>
    /// Starting curve for a calibrated camera. EV is camera-relative, not absolute; with the Logitech used during
    /// development a lamp-lit room at night reads about 3. Learning from manual adjustments reshapes it.
    /// </summary>
    public static IReadOnlyList<CurvePoint> Default { get; } =
    [
        new(-4, 5), new(0, 15), new(3, 30), new(6, 60), new(9, 90), new(11, 100),
    ];

    public IReadOnlyList<CurvePoint> Points => _points;

    public double Evaluate(double ev)
    {
        if (ev <= _points[0].Ev) return _points[0].Brightness;
        if (ev >= _points[^1].Ev) return _points[^1].Brightness;
        for (var i = 1; i < _points.Count; i++)
        {
            var (a, b) = (_points[i - 1], _points[i]);
            if (ev <= b.Ev)
            {
                var t = (ev - a.Ev) / (b.Ev - a.Ev);
                return a.Brightness + t * (b.Brightness - a.Brightness);
            }
        }
        return _points[^1].Brightness;
    }

    /// <summary>Records the user's preference at a light level; the new point wins over conflicting ones.</summary>
    public void Learn(double ev, double brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        _points.RemoveAll(p => Math.Abs(p.Ev - ev) < MergeDistance);
        _points.RemoveAll(p => (p.Ev < ev && p.Brightness > brightness) || (p.Ev > ev && p.Brightness < brightness));
        _points.Add(new CurvePoint(ev, brightness, Learned: true));
        _points.Sort((x, y) => x.Ev.CompareTo(y.Ev));
    }

    public void Set(IEnumerable<CurvePoint> points)
    {
        _points.Clear();
        _points.AddRange(points.OrderBy(p => p.Ev));
        if (_points.Count == 0) _points.AddRange(Default);
        EnforceMonotonic();
    }

    private void EnforceMonotonic()
    {
        for (var i = 1; i < _points.Count; i++)
            if (_points[i].Brightness < _points[i - 1].Brightness)
                _points[i] = _points[i] with { Brightness = _points[i - 1].Brightness };
    }
}
