using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AutoBrightness.Camera;
using AutoBrightness.Control;

namespace AutoBrightness.Settings;

public enum ControlMode
{
    /// <summary>Camera closed, nothing measured.</summary>
    Off,
    /// <summary>Measures and shows the target brightness, and learns from manual changes, but never writes.</summary>
    Preview,
    /// <summary>Measures and drives brightness through Twinkle Tray.</summary>
    Auto,
}

public sealed record MonitorSettings
{
    public required string Key { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// <summary>Added to the curve's output, in percent, for monitors brighter or dimmer than the reference.</summary>
    public int Offset { get; init; }
    public int Min { get; init; }
    public int Max { get; init; } = 100;

    /// <summary>The limits in order and within 0..100, whatever was saved.</summary>
    public (int Min, int Max) Range => (Math.Clamp(Math.Min(Min, Max), 0, 100), Math.Clamp(Math.Max(Min, Max), 0, 100));
}

public sealed class AppSettings
{
    /// <summary>Bumped when defaults change in a way that should reach existing installs.</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;

    public ControlMode Mode { get; set; } = ControlMode.Preview;
    public string? CameraId { get; set; }
    public double SampleIntervalSeconds { get; set; } = 20;
    public Roi Roi { get; set; } = Roi.Full;
    public List<CurvePoint> Curve { get; set; } = [.. BrightnessCurve.Default];
    public List<MonitorSettings> Monitors { get; set; } = [];
    public double BrightenSeconds { get; set; } = 20;
    public double DimSeconds { get; set; } = 60;
    public WritePolicyOptions WritePolicy { get; set; } = new();
    public TransitionOptions Transitions { get; set; } = new();
    public bool LearnFromManual { get; set; } = true;
    public double ManualPauseMinutes { get; set; } = 20;
    public bool StartWithWindows { get; set; }

    // Persisted state rather than preferences.

    /// <summary>Monitor writes per monitor key, so limits hold across restarts.</summary>
    public Dictionary<string, WriteCounter> WriteCounters { get; set; } = [];
    /// <summary>Single count kept by versions before per-monitor counters; seeds monitors that have none yet.</summary>
    public DateOnly WritesDay { get; set; }
    public int WritesToday { get; set; }

    /// <summary>Replaces missing and out-of-range values (from a hand-edited or older file) with safe ones.</summary>
    internal AppSettings Normalize()
    {
        static double Finite(double v, double fallback, double min, double max) =>
            double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

        Curve = Curve?.Where(p => p is not null && double.IsFinite(p.Ev) && double.IsFinite(p.Brightness)).ToList() ?? [];
        if (Curve.Count == 0) Curve = [.. BrightnessCurve.Default];
        Monitors = Monitors?.Where(m => m?.Key is not null).ToList() ?? [];
        WritePolicy = (WritePolicy ?? new WritePolicyOptions()).Normalized();
        Transitions ??= new TransitionOptions();
        WriteCounters ??= [];
        Roi = Roi.Clamp();
        SampleIntervalSeconds = Finite(SampleIntervalSeconds, 20, 5, 3600);
        BrightenSeconds = Finite(BrightenSeconds, 20, 0, 3600);
        DimSeconds = Finite(DimSeconds, 60, 0, 3600);
        ManualPauseMinutes = Finite(ManualPauseMinutes, 20, 0, 1440);
        if (!Enum.IsDefined(Mode)) Mode = ControlMode.Preview;
        return this;
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in the app data folder.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action? Changed;

    private AppSettings Load()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(_path)) return new AppSettings().Normalize();
                var text = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
                // Files written before the version field existed are version 1.
                if (JsonNode.Parse(text) is JsonObject root && !root.ContainsKey(nameof(AppSettings.Version))) settings.Version = 1;
                return Migrate(settings).Normalize();
            }
            catch (JsonException ex)
            {
                // Keep the unreadable file for inspection and start fresh.
                Log.Write($"Settings file is not valid JSON; starting from defaults and keeping it as {_path}.bad", ex);
                try { File.Copy(_path, _path + ".bad", overwrite: true); }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
                return new AppSettings().Normalize();
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(200); // briefly locked, e.g. by a virus scanner or sync client
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("Settings file could not be read; using defaults", ex);
                return new AppSettings().Normalize();
            }
        }
    }

    internal static AppSettings Migrate(AppSettings s)
    {
        if (s.Version < 2)
        {
            // v2: infrequent large changes applied as a single jump. Only untouched v1 defaults are replaced.
            var v1 = new WritePolicyOptions { MinStep = 3, BigStep = 15, MinInterval = TimeSpan.FromSeconds(60), DailyBudget = 200 };
            if (s.WritePolicy == v1) s.WritePolicy = new WritePolicyOptions();
            s.Transitions = (s.Transitions ?? new TransitionOptions()) with { Enabled = false };
        }
        s.Version = AppSettings.CurrentVersion;
        return s;
    }

    /// <summary>
    /// Applies a change and saves. Replace lists rather than editing them in place: other threads may be
    /// reading the current ones.
    /// </summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            change(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        Changed?.Invoke();
    }
}
