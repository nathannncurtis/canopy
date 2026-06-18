using System.IO;

namespace SizeMonitor.Helpers;

internal static class Logger
{
    static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Canopy", "canopy.log");

    internal static string LogPath => _path;

    internal static void Error(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}" +
                $"  {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}" +
                $"{ex.StackTrace}{Environment.NewLine}" +
                $"---{Environment.NewLine}");
        }
        catch { /* log failure must never cascade */ }
    }

    internal static void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
