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
    public static string ShortcutOverrides => Path.Combine(DataDirectory, "shortcuts.json");
    public static string ObservabilityDirectory => Path.Combine(DataDirectory, "observability");
    public static string LocationHistory => Path.Combine(DataDirectory, "locations.json");
    public static string TourCompletion => Path.Combine(DataDirectory, "tour-completed-v1");
    public static string DriveHistory => Path.Combine(DataDirectory, "drive-history.json");
    public static string CleanupQueue => Path.Combine(DataDirectory, "cleanup-queue.json");
    public static string WorkspaceLayout => Path.Combine(DataDirectory, "workspace-layout.json");
    public static string AppearancePreferences => Path.Combine(DataDirectory, "appearance.json");
    public static string TreemapPresentation => Path.Combine(DataDirectory, "treemap-presentation.json");
}
