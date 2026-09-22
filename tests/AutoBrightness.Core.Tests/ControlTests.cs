using AutoBrightness.Control;

namespace AutoBrightness.Tests;

public class BrightnessCurveTests
{
    private static BrightnessCurve Curve() => new([new(0, 10), new(4, 50), new(8, 90)]);

    [Theory]
    [InlineData(-5, 10)]
    [InlineData(0, 10)]
    [InlineData(2, 30)]
    [InlineData(6, 70)]
    [InlineData(12, 90)]
    public void InterpolatesAndClamps(double ev, double expected) => Assert.Equal(expected, Curve().Evaluate(ev), 6);

    [Fact]
    public void LearnedPointReplacesNearbyPoint()
    {
        var c = Curve();
        c.Learn(4.3, 40);

        Assert.Equal(3, c.Points.Count);
        Assert.DoesNotContain(c.Points, p => p.Ev == 4);
        Assert.Equal(40, c.Evaluate(4.3), 6);
        Assert.True(c.Points.Single(p => p.Ev == 4.3).Learned);
    }

    [Fact]
    public void LearningKeepsTheCurveIncreasingWithoutDroppingPoints()
    {
        var c = Curve();
        c.Learn(2, 70); // brighter than the point at EV 4

        Assert.Equal([0, 2, 4, 8], c.Points.Select(p => p.Ev));
        Assert.Equal(70, c.Evaluate(2), 6);
        Assert.True(c.Evaluate(4) >= 70);
        AssertMonotonic(c);
    }

    [Fact]
    public void OneExtremeAdjustmentDoesNotCreateACliff()
    {
        var c = new BrightnessCurve(BrightnessCurve.Default);
        c.Learn(8, 5); // e.g. dimmed for a film in a bright room

        Assert.Equal(5, c.Evaluate(8), 6);
        Assert.Equal(BrightnessCurve.Default.Count + 1, c.Points.Count);
        AssertMonotonic(c);
        var right = c.Points.First(p => p.Ev > 8);
        Assert.True((right.Brightness - 5) / (right.Ev - 8) <= BrightnessCurve.MaxLearnedSlope + 1e-6);
    }

    [Fact]
    public void RepeatedAdjustmentsAtOneLevelDoNotPileUp()
    {
        var c = new BrightnessCurve(BrightnessCurve.Default);
        c.Learn(3, 95);
        c.Learn(3.2, 30);

        Assert.Single(c.Points, p => p.Learned);
        Assert.Equal(30, c.Evaluate(3.2), 6);
        AssertMonotonic(c);
    }

    [Fact]
    public void ShiftMovesEveryPoint()
    {
        var c = Curve();
        c.Shift(1.5);
        Assert.Equal([1.5, 5.5, 9.5], c.Points.Select(p => p.Ev));
        Assert.Equal(50, c.Evaluate(5.5), 6);
    }

    [Fact]
    public void ConstructorEnforcesMonotonicity()
    {
        var c = new BrightnessCurve([new(0, 50), new(2, 30), new(4, 60)]);
        AssertMonotonic(c);
    }

    [Fact]
    public void EmptyCurveUsesDefault() => Assert.Equal(BrightnessCurve.Default.Count, new BrightnessCurve([]).Points.Count);

    [Fact]
    public void DuplicateLevelsDoNotDivideByZero()
    {
        var c = new BrightnessCurve([new(2, 20), new(2, 40), new(4, 60)]);
        Assert.All(new[] { 1.0, 2.0, 3.0, 5.0 }, ev => Assert.True(double.IsFinite(c.Evaluate(ev))));
    }

    private static void AssertMonotonic(BrightnessCurve c)
    {
        for (var i = 1; i < c.Points.Count; i++) Assert.True(c.Points[i].Brightness >= c.Points[i - 1].Brightness);
    }
}

public class LightSmootherTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void FirstReadingIsTakenAsIs() =>
        Assert.Equal(3.0, new LightSmoother(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)).Add(3, T0));

    [Fact]
    public void SingleOutlierIsRejected()
    {
        var s = new LightSmoother(TimeSpan.Zero, TimeSpan.Zero); // no lag, isolates the median filter
        s.Add(3, T0);
        s.Add(3, T0.AddSeconds(20));
        Assert.Equal(3.0, s.Add(9, T0.AddSeconds(40)));
    }

    [Fact]
    public void BrightensFasterThanItDims()
    {
        var up = new LightSmoother(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
        var down = new LightSmoother(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
        up.Add(0, T0); up.Add(0, T0.AddSeconds(1));
        down.Add(4, T0); down.Add(4, T0.AddSeconds(1));

        double u = 0, d = 0;
        for (var i = 1; i <= 3; i++)
        {
            u = up.Add(4, T0.AddSeconds(1 + 20 * i));
            d = down.Add(0, T0.AddSeconds(1 + 20 * i));
        }

        Assert.True(u - 0 > 4 - d, $"brightened by {u:F2}, dimmed by {4 - d:F2}");
    }

    [Fact]
    public void CountsReadingsSinceReset()
    {
        var s = new LightSmoother(TimeSpan.Zero, TimeSpan.Zero);
        s.Add(1, T0);
        s.Add(1, T0.AddSeconds(20));
        Assert.Equal(2, s.Readings);
        s.Reset();
        Assert.Equal(0, s.Readings);
    }
}

public class WritePolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    private static readonly WritePolicyOptions Options = new() { MinStep = 3, BigStep = 15, MinInterval = TimeSpan.FromSeconds(60), DailyBudget = 2 };

    [Fact]
    public void SmallChangesAreSkipped() => Assert.Equal(WriteDecision.TooSmall, new WritePolicy(Options).Decide(50, 52, T0));

    [Fact]
    public void RespectsMinimumInterval()
    {
        var p = new WritePolicy(Options);
        Assert.Equal(WriteDecision.Write, p.Decide(50, 55, T0));
        p.Record(T0);
        Assert.Equal(WriteDecision.TooSoon, p.Decide(55, 60, T0.AddSeconds(30)));
        Assert.Equal(WriteDecision.Write, p.Decide(55, 60, T0.AddSeconds(61)));
    }

    [Fact]
    public void BudgetOnlyBlocksSmallChangesAndResetsDaily()
    {
        var p = new WritePolicy(Options);
        p.Record(T0);
        p.Record(T0.AddMinutes(2));
        Assert.Equal(WriteDecision.BudgetExhausted, p.Decide(50, 55, T0.AddMinutes(5)));
        Assert.Equal(WriteDecision.Write, p.Decide(50, 70, T0.AddMinutes(5)));
        Assert.Equal(WriteDecision.Write, p.Decide(50, 55, T0.AddDays(1)));
        Assert.Equal(0, p.WritesToday);
    }

    [Fact]
    public void HardCapStopsLargeChangesToo()
    {
        var p = new WritePolicy(Options); // hard cap = 2 x budget = 4
        p.Record(T0, 4);
        Assert.Equal(WriteDecision.BudgetExhausted, p.Decide(10, 90, T0.AddHours(1)));
    }

    [Fact]
    public void LargeChangesStillNeedAGap()
    {
        var p = new WritePolicy(Options);
        p.Record(T0);
        Assert.Equal(WriteDecision.TooSoon, p.Decide(10, 90, T0.AddSeconds(30)));
        Assert.Equal(WriteDecision.Write, p.Decide(10, 90, T0.AddMinutes(3)));
    }

    [Fact]
    public void ADayOfLargeSwingsStaysUnderTheHardCap()
    {
        var p = new WritePolicy(new WritePolicyOptions());
        var current = 20;
        for (var i = 0; i < 24 * 180; i++) // a reading every 20 s
        {
            var now = new DateTime(2026, 1, 1).AddSeconds(20 * i);
            var target = i % 2 == 0 ? 60 : 20;
            if (p.Decide(current, target, now) != WriteDecision.Write) continue;
            p.Record(now);
            current = target;
        }
        Assert.InRange(p.WritesToday, 1, p.Options.DailyHardCap);
    }

    [Fact]
    public void LargeStepSmallerThanMinimumStepCannotDisableTheLimits()
    {
        var p = new WritePolicy(new WritePolicyOptions { MinStep = 10, BigStep = 5 });
        Assert.Equal(10, p.Options.BigStep);
        p.Record(T0);
        Assert.Equal(WriteDecision.TooSoon, p.Decide(20, 32, T0.AddSeconds(20)));
    }

    [Fact]
    public void ClockGoingBackDoesNotRefreshTheBudget()
    {
        var p = new WritePolicy(Options);
        p.Record(T0, 2);
        Assert.Equal(WriteDecision.TooSoon, p.Decide(50, 55, T0.AddDays(-1)));
        Assert.Equal(2, p.WritesToday);
        Assert.Equal(WriteDecision.BudgetExhausted, p.Decide(50, 55, T0.AddDays(-1).AddMinutes(5)));
    }

    [Fact]
    public void IntervalSurvivesARestart()
    {
        var p = new WritePolicy(Options);
        p.Record(T0);
        var restarted = new WritePolicy(Options);
        restarted.Restore(p.State);
        Assert.Equal(WriteDecision.TooSoon, restarted.Decide(50, 55, T0.AddSeconds(30)));
        Assert.Equal(1, restarted.WritesToday);
    }

    [Fact]
    public void AllowanceDependsOnTheSizeOfTheChange()
    {
        var p = new WritePolicy(Options);
        p.Record(T0);
        Assert.Equal(1, p.WritesAllowed(50, 55, T0)); // budget 2
        Assert.Equal(3, p.WritesAllowed(10, 90, T0)); // hard cap 4
    }
}

public class BrightnessRampTests
{
    private static readonly TransitionOptions Options = new() { Enabled = true, MaxStep = 2, StepInterval = TimeSpan.FromMilliseconds(500), MaxDuration = TimeSpan.FromSeconds(8) };

    [Fact]
    public void FadesInSmallEqualSteps() => Assert.Equal([32, 34, 36, 38, 40], BrightnessRamp.Steps(30, 40, Options));

    [Fact]
    public void FadesDown() => Assert.Equal([48, 47, 45], BrightnessRamp.Steps(50, 45, Options));

    [Fact]
    public void LongFadesTakeBiggerStepsInsteadOfLonger()
    {
        var steps = BrightnessRamp.Steps(0, 100, Options);
        Assert.Equal(16, steps.Count); // 8 s / 0.5 s
        Assert.Equal(100, steps[^1]);
    }

    [Fact]
    public void FadeFitsTheRemainingWrites()
    {
        var steps = BrightnessRamp.Steps(20, 50, Options, maxCount: 3);
        Assert.Equal([30, 40, 50], steps);
    }

    [Fact]
    public void NoIntervalStillBoundsTheSteps() =>
        Assert.Single(BrightnessRamp.Steps(0, 100, Options with { StepInterval = TimeSpan.Zero, MaxStep = 1 }, maxCount: 1));

    [Fact]
    public void DisabledJumpsStraightToTarget() => Assert.Equal([70], BrightnessRamp.Steps(30, 70, Options with { Enabled = false }));

    [Fact]
    public void NoChangeNoSteps() => Assert.Empty(BrightnessRamp.Steps(40, 40, Options));
}

public class SettingsMigrationTests
{
    [Fact]
    public void Version1DefaultsMoveToInfrequentLargeJumps()
    {
        var v1 = new Settings.AppSettings
        {
            Version = 1,
            WritePolicy = new WritePolicyOptions { MinStep = 3, BigStep = 15, MinInterval = TimeSpan.FromSeconds(60), DailyBudget = 200 },
            Transitions = new TransitionOptions { Enabled = true },
        };
        var s = Settings.SettingsStore.Migrate(v1);

        Assert.Equal(new WritePolicyOptions(), s.WritePolicy);
        Assert.Equal(10, s.WritePolicy.MinStep);
        Assert.Equal(TimeSpan.FromMinutes(15), s.WritePolicy.MinInterval);
        Assert.False(s.Transitions.Enabled);
        Assert.Equal(Settings.AppSettings.CurrentVersion, s.Version);
    }

    [Fact]
    public void CustomisedLimitsAreKept()
    {
        var custom = new WritePolicyOptions { MinStep = 5, MinInterval = TimeSpan.FromMinutes(2) };
        var s = Settings.SettingsStore.Migrate(new Settings.AppSettings { Version = 1, WritePolicy = custom });
        Assert.Equal(custom, s.WritePolicy);
    }

    [Fact]
    public void FileWithoutVersionIsMigrated()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "WritePolicy": { "MinStep": 3, "BigStep": 15, "MinInterval": "00:01:00", "DailyBudget": 200 } }""");

        var s = new Settings.SettingsStore(path).Current;
        Assert.Equal(10, s.WritePolicy.MinStep);
    }

    [Fact]
    public void NullsAndOutOfRangeValuesAreRepaired()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """
            { "Version": 2, "Curve": null, "Monitors": null, "WritePolicy": null, "Transitions": null,
              "SampleIntervalSeconds": 0, "WriteCounters": null }
            """);

        var s = new Settings.SettingsStore(path).Current;
        Assert.Equal(BrightnessCurve.Default.Count, s.Curve.Count);
        Assert.NotNull(s.Monitors);
        Assert.NotNull(s.WriteCounters);
        Assert.Equal(new WritePolicyOptions(), s.WritePolicy);
        Assert.True(s.SampleIntervalSeconds >= 5);
    }

    [Fact]
    public void UnreadableJsonIsKeptAsideAndDefaultsAreUsed()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ not json");

        var s = new Settings.SettingsStore(path).Current;
        Assert.Equal(Settings.ControlMode.Preview, s.Mode);
        Assert.True(File.Exists(path + ".bad"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ab-settings-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
