using System.Text.Json;

namespace AutoBrightness.Camera;

/// <summary>
/// Crash safety for camera settings. While a session holds the camera, its original settings are kept on
/// disk; if the process dies before restoring them, the next start puts them back. Otherwise a camera left
/// in manual exposure makes every app wait ~4.5 s longer for its first frame.
/// </summary>
public static class CameraRecovery
{
    internal sealed record Pending(string DeviceId, CameraControls.Snapshot Snapshot);

    private static string PathName => Path.Combine(AppPaths.DataDirectory, "camera-restore.json");

    internal static void Remember(string deviceId, CameraControls.Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(PathName, JsonSerializer.Serialize(new Pending(deviceId, snapshot)));
        }
        catch (IOException) { /* best effort */ }
    }

    internal static void Forget()
    {
        try { File.Delete(PathName); }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>Restores settings left behind by a session that never closed. Returns true if it did.</summary>
    public static async Task<bool> RecoverAsync()
    {
        Pending? pending;
        try
        {
            if (!File.Exists(PathName)) return false;
            pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(PathName));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Forget();
            return false;
        }
        if (pending is null) { Forget(); return false; }

        try
        {
            var device = (await CameraDevice.FindAllAsync()).FirstOrDefault(d => d.Id == pending.DeviceId);
            if (device is null) return false; // unplugged; try again next start

            await using var session = await CameraSession.OpenAsync(device, remember: false);
            session.Controls.Restore(pending.Snapshot);
            session.RestoreOnDispose = false; // its own snapshot is the stale state we are fixing
            Forget();
            return true;
        }
        catch (CameraException)
        {
            return false;
        }
    }
}
