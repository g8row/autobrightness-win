using System.Text.Json;
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
    public DateOnly WritesDay { get; set; }
    public int WritesToday { get; set; }
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
        try
        {
            if (File.Exists(_path))
                return Migrate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings());
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start fresh.
            File.Copy(_path, _path + ".bad", overwrite: true);
        }
        return new AppSettings();
    }

    internal static AppSettings Migrate(AppSettings s)
    {
        if (s.Version < 2)
        {
            // v2: infrequent large changes applied as a single jump. Only untouched v1 defaults are replaced.
            var v1 = new WritePolicyOptions { MinStep = 3, BigStep = 15, MinInterval = TimeSpan.FromSeconds(60), DailyBudget = 200 };
            if (s.WritePolicy == v1) s.WritePolicy = new WritePolicyOptions();
            s.Transitions = s.Transitions with { Enabled = false };
        }
        s.Version = AppSettings.CurrentVersion;
        return s;
    }

    /// <summary>Applies a change and saves.</summary>
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
