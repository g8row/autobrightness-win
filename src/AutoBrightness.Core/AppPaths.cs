namespace AutoBrightness;

public static class AppPaths
{
    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoBrightness");
}
