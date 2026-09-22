namespace AutoBrightness.Control;

public sealed record TransitionOptions
{
    /// <summary>Fade between brightness levels instead of jumping; Twinkle Tray applies each value instantly.</summary>
    public bool Enabled { get; init; }
    /// <summary>Largest change per write, in percent.</summary>
    public int MaxStep { get; init; } = 2;
    /// <summary>Delay between writes. Twinkle Tray coalesces writes closer than its update interval (500 ms by default).</summary>
    public TimeSpan StepInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Upper bound on a fade; larger changes take bigger steps rather than longer.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(8);
}

public static class BrightnessRamp
{
    /// <summary>
    /// Intermediate values from <paramref name="from"/> to <paramref name="to"/> (excluding the start, including
    /// the end), in equal steps no larger than the configured step unless that would exceed the maximum duration
    /// or <paramref name="maxCount"/> writes. Every step is a monitor write.
    /// </summary>
    public static IReadOnlyList<int> Steps(int from, int to, TransitionOptions options, int maxCount = int.MaxValue)
    {
        var delta = to - from;
        if (delta == 0) return [];
        if (!options.Enabled || maxCount <= 1) return [to];

        var maxSteps = options.StepInterval <= TimeSpan.Zero
            ? int.MaxValue
            : Math.Max(1, (int)(options.MaxDuration / options.StepInterval));
        var count = Math.Min(Math.Min((int)Math.Ceiling(Math.Abs(delta) / (double)Math.Max(1, options.MaxStep)), maxSteps), maxCount);
        var steps = new List<int>(count);
        for (var i = 1; i <= count; i++) steps.Add(from + (int)Math.Round(delta * (double)i / count));
        return steps;
    }
}
