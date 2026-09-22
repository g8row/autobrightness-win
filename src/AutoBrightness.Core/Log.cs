namespace AutoBrightness;

/// <summary>
/// A small rolling text log in the data folder: brightness writes, learned adjustments and unexpected errors,
/// so write rates and failures can be checked after the fact. Never contains camera images.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();

    public static string PathName => Path.Combine(AppPaths.DataDirectory, "autobrightness.log");

    public static void Write(string message, Exception? error = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var file = new FileInfo(PathName);
                if (file.Exists && file.Length > MaxBytes) File.Move(PathName, PathName + ".1", overwrite: true);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
                if (error is not null) line += Environment.NewLine + error;
                File.AppendAllText(PathName, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }
}
