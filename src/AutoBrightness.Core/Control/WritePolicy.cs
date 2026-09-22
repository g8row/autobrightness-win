namespace AutoBrightness.Control;

public sealed record WritePolicyOptions
{
    /// <summary>Changes smaller than this (percent) are not worth a write.</summary>
    public int MinStep { get; init; } = 10;
    /// <summary>Changes at least this large skip <see cref="MinInterval"/> and the daily budget, but not the hard cap.</summary>
    public int BigStep { get; init; } = 30;
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Shortest gap between any two changes, large ones included.</summary>
    public TimeSpan BigStepMinInterval { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>
    /// Monitor writes per day for ordinary changes. Monitors may store brightness in EEPROM rated for ~100k writes.
    /// Every write counts, including each step of a fade.
    /// </summary>
    public int DailyBudget { get; init; } = 48;

    /// <summary>Writes per day that nothing can exceed, large changes included.</summary>
    public int DailyHardCap => DailyBudget * 2;

    /// <summary>The same options with values that could defeat the limits pulled into range.</summary>
    public WritePolicyOptions Normalized()
    {
        var minStep = Math.Clamp(MinStep, 1, 100);
        return this with
        {
            MinStep = minStep,
            BigStep = Math.Clamp(BigStep, minStep, 101),
            MinInterval = MinInterval < TimeSpan.Zero ? TimeSpan.Zero : MinInterval,
            BigStepMinInterval = BigStepMinInterval < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : BigStepMinInterval,
            DailyBudget = Math.Clamp(DailyBudget, 1, 500),
        };
    }
}

public enum WriteDecision { Write, TooSmall, TooSoon, BudgetExhausted }

/// <summary>A monitor's write count for one day and the time of its last write, persisted across restarts.</summary>
public sealed record WriteCounter(DateOnly Day, int Count, DateTime? LastUtc);

/// <summary>Decides whether a brightness change is worth DDC/CI writes to one monitor.</summary>
public sealed class WritePolicy
{
    private WritePolicyOptions _options = new();
    private DateTime? _lastUtc;
    private DateOnly _day;

    public WritePolicy(WritePolicyOptions options) => Options = options;

    public WritePolicyOptions Options
    {
        get => _options;
        set => _options = value.Normalized();
    }

    public int WritesToday { get; private set; }

    public WriteDecision Decide(int current, int target, DateTime now)
    {
        RollDay(now);
        var delta = Math.Abs(target - current);
        if (delta == 0 || delta < Options.MinStep) return WriteDecision.TooSmall;
        if (WritesToday >= Options.DailyHardCap) return WriteDecision.BudgetExhausted;

        var nowUtc = now.ToUniversalTime();
        if (_lastUtc is { } last && nowUtc < last)
        {
            // The clock went back. Measure the interval from now rather than blocking until it catches up.
            _lastUtc = nowUtc;
            return WriteDecision.TooSoon;
        }
        var since = _lastUtc is { } l ? nowUtc - l : TimeSpan.MaxValue;
        if (delta >= Options.BigStep)
            return since < Options.BigStepMinInterval ? WriteDecision.TooSoon : WriteDecision.Write;
        if (since < Options.MinInterval) return WriteDecision.TooSoon;
        if (WritesToday >= Options.DailyBudget) return WriteDecision.BudgetExhausted;
        return WriteDecision.Write;
    }

    /// <summary>How many writes a change from <paramref name="current"/> to <paramref name="target"/> may use today.</summary>
    public int WritesAllowed(int current, int target, DateTime now)
    {
        RollDay(now);
        var limit = Math.Abs(target - current) >= Options.BigStep ? Options.DailyHardCap : Options.DailyBudget;
        return Math.Max(0, limit - WritesToday);
    }

    public void Record(DateTime now, int writes = 1)
    {
        RollDay(now);
        _lastUtc = now.ToUniversalTime();
        WritesToday += Math.Max(0, writes);
    }

    public WriteCounter State => new(_day, WritesToday, _lastUtc);

    /// <summary>Restores the day's count and the last write after a restart.</summary>
    public void Restore(WriteCounter state)
    {
        _day = state.Day;
        WritesToday = state.Count;
        _lastUtc = state.LastUtc;
    }

    private void RollDay(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (today > _day)
        {
            _day = today;
            WritesToday = 0;
        }
        else if (today < _day)
        {
            // The clock went back a day or more: keep the count rather than handing out a fresh budget.
            _day = today;
        }
    }
}
