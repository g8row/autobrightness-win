using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoBrightness.Camera;

/// <summary>Relative sensitivity at a gain setting, measured against gain at its minimum.</summary>
public sealed record GainStep(int Gain, double Factor);

/// <summary>Per-camera metering parameters, produced by <see cref="Calibrator"/> and stored as JSON.</summary>
public sealed record CameraProfile
{
    /// <summary>Hardware key such as "VID_046D&amp;PID_0819", shared by every unit of the same model.</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }

    public int ExposureMin { get; init; } = -13;
    /// <summary>Longest exposure that still fits one frame period; longer ones drop the frame rate or hang some cameras.</summary>
    public int ExposureMax { get; init; } = -5;
    public int ExposureStart { get; init; } = -6;

    public bool GainSupported { get; init; }
    public List<GainStep> GainTable { get; init; } = [new(0, 1.0)];

    public double Black { get; init; }
    public double Gamma { get; init; } = 2.2;

    /// <summary>Frames to discard after changing exposure before the image reflects it.</summary>
    public int SettleFrames { get; init; } = 3;
    /// <summary>Frame-to-frame standard deviation of the mean at a mid exposure, in 8-bit levels.</summary>
    public double NoiseLevels { get; init; }

    public DateTime? CalibratedAt { get; init; }
    public List<string> Notes { get; init; } = [];

    [JsonIgnore] public bool IsCalibrated => CalibratedAt is not null;
    [JsonIgnore] public ResponseModel Response => new(Black, Gamma);

    public static CameraProfile Uncalibrated(string key, string name) => new() { Key = key, Name = name };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DirectoryPath => Path.Combine(AppPaths.DataDirectory, "profiles");

    private static string PathFor(string key) =>
        Path.Combine(DirectoryPath, string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".json");

    public static CameraProfile? Load(string key)
    {
        try
        {
            var path = PathFor(key);
            return File.Exists(path) ? JsonSerializer.Deserialize<CameraProfile>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException) { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(PathFor(Key), JsonSerializer.Serialize(this, Json));
    }
}
