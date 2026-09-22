namespace AutoBrightness.Control;

/// <summary>A curve point: light level in stops (camera-relative EV) to brightness percent.</summary>
public sealed record CurvePoint(double Ev, double Brightness, bool Learned = false);

/// <summary>
/// Monotonic piecewise-linear map from light level to brightness. A manual adjustment becomes a point on the
/// curve and bends its neighbours towards it, rather than deleting them, so no single adjustment can wipe out
/// the rest of the curve.
/// </summary>
public sealed class BrightnessCurve
{
    /// <summary>Learned points closer than this (in stops) replace each other.</summary>
    public const double MergeDistance = 0.75;

    /// <summary>How far (in stops) an adjustment bends the curve around it.</summary>
    public const double LearnReach = 2.0;

    /// <summary>
    /// Steepest slope (percent per stop) that learning creates next to a learned point. Steeper curves turn small
    /// changes in the light reading into large brightness changes.
    /// </summary>
    public const double MaxLearnedSlope = 25;

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

    /// <summary>
    /// Records the user's preference at a light level. Nearby points move with it (less the further away they
    /// are), the curve stays non-decreasing, and its slope next to the new point is limited.
    /// </summary>
    public void Learn(double ev, double brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);
        var change = brightness - Evaluate(ev);

        _points.RemoveAll(p => Math.Abs(p.Ev - ev) < MergeDistance);
        for (var i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            var weight = Math.Exp(-Math.Pow((p.Ev - ev) / LearnReach, 2));
            _points[i] = p with { Brightness = Math.Clamp(Math.Round(p.Brightness + change * weight, 1), 0, 100) };
        }

        var index = _points.FindIndex(p => p.Ev > ev);
        if (index < 0) index = _points.Count;
        _points.Insert(index, new CurvePoint(ev, brightness, Learned: true));

        // Outwards from the new point: never decreasing, and no steeper than the slope limit.
        for (var i = index + 1; i < _points.Count; i++)
        {
            var prev = _points[i - 1];
            var max = prev.Brightness + MaxLearnedSlope * (_points[i].Ev - prev.Ev);
            _points[i] = _points[i] with { Brightness = Math.Clamp(_points[i].Brightness, prev.Brightness, Math.Min(100, max)) };
        }
        for (var i = index - 1; i >= 0; i--)
        {
            var next = _points[i + 1];
            var min = next.Brightness - MaxLearnedSlope * (next.Ev - _points[i].Ev);
            _points[i] = _points[i] with { Brightness = Math.Clamp(_points[i].Brightness, Math.Max(0, min), next.Brightness) };
        }
    }

    /// <summary>Moves every point by <paramref name="stops"/>, when the light scale itself has moved (new calibration or measurement area).</summary>
    public void Shift(double stops)
    {
        for (var i = 0; i < _points.Count; i++) _points[i] = _points[i] with { Ev = Math.Round(_points[i].Ev + stops, 2) };
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
