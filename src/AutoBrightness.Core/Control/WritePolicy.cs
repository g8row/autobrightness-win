namespace AutoBrightness.Control;

public sealed record WritePolicyOptions
{
    /// <summary>Changes smaller than this (percent) are not worth a write.</summary>
    public int MinStep { get; init; } = 3;
    /// <summary>Changes at least this large bypass the interval and the daily budget.</summary>
    public int BigStep { get; init; } = 15;
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Monitors may store brightness in EEPROM rated for ~100k writes; this caps wear per day.</summary>
    public int DailyBudget { get; init; } = 200;
}

public enum WriteDecision { Write, TooSmall, TooSoon, BudgetExhausted }

/// <summary>Decides whether a brightness change is worth a DDC/CI write.</summary>
public sealed class WritePolicy(WritePolicyOptions options)
{
    private DateTime? _lastWrite;
    private DateOnly _day;

    public WritePolicyOptions Options { get; set; } = options;
    public int WritesToday { get; private set; }

    public WriteDecision Decide(int current, int target, DateTime now)
    {
        RollDay(now);
        var delta = Math.Abs(target - current);
        if (delta < Options.MinStep || delta == 0) return WriteDecision.TooSmall;
        if (delta >= Options.BigStep) return WriteDecision.Write;
        if (_lastWrite is { } last && now - last < Options.MinInterval) return WriteDecision.TooSoon;
        if (WritesToday >= Options.DailyBudget) return WriteDecision.BudgetExhausted;
        return WriteDecision.Write;
    }

    public void Record(DateTime now)
    {
        RollDay(now);
        _lastWrite = now;
        WritesToday++;
    }

    /// <summary>Restores the day's count after a restart.</summary>
    public void Restore(DateOnly day, int writes)
    {
        _day = day;
        WritesToday = writes;
    }

    public DateOnly Day => _day;

    private void RollDay(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (today != _day)
        {
            _day = today;
            WritesToday = 0;
        }
    }
}
