using System.IO;

namespace SizeMonitor.Helpers;

public static class AppDataPaths
{
    public static bool IsPortable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

    public static string DataDirectory => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Canopy");

    public static string FilterPresets => Path.Combine(DataDirectory, "filter-presets.json");
}
