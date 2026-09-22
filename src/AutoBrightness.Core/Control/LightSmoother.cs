namespace AutoBrightness.Control;

/// <summary>
/// Smooths light readings in the log (EV) domain: a median of the last three rejects one-off outliers
/// (someone walking past), then a time-aware exponential filter responds faster to brightening than to dimming,
/// as eyes do.
/// </summary>
public sealed class LightSmoother(TimeSpan brightenTime, TimeSpan dimTime)
{
    private readonly Queue<double> _recent = new();
    private DateTime? _lastTime;

    public TimeSpan BrightenTime { get; set; } = brightenTime;
    public TimeSpan DimTime { get; set; } = dimTime;
    public double? Value { get; private set; }

    /// <summary>Readings in the median window since the last reset (up to three).</summary>
    public int Readings => _recent.Count;

    public double Add(double ev, DateTime time)
    {
        _recent.Enqueue(ev);
        if (_recent.Count > 3) _recent.Dequeue();
        var median = _recent.Order().ElementAt(_recent.Count / 2);

        if (Value is null || _lastTime is null)
        {
            Value = median;
        }
        else
        {
            var dt = Math.Max(0, (time - _lastTime.Value).TotalSeconds);
            var tau = (median > Value ? BrightenTime : DimTime).TotalSeconds;
            var alpha = tau <= 0 ? 1 : 1 - Math.Exp(-dt / tau);
            Value += alpha * (median - Value.Value);
        }
        _lastTime = time;
        return Value.Value;
    }

    /// <summary>Jumps straight to <paramref name="ev"/>, e.g. after a long pause when history is stale.</summary>
    public void Reset(double? ev = null)
    {
        _recent.Clear();
        _lastTime = null;
        Value = ev;
    }
}
