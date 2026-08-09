using System.IO;

namespace SizeMonitor.Helpers;

public static class TourCompletionStore
{
    const string CurrentVersion = "1";
    public static string DefaultPath => AppDataPaths.TourCompletion;

    public static bool IsComplete(string? path = null)
    {
        try { return File.ReadAllText(path ?? DefaultPath).Trim() == CurrentVersion; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static void MarkComplete(string? path = null)
    {
        string target = path ?? DefaultPath;
        string? directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A completion file directory is required.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, CurrentVersion); File.Move(temporary, target, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }
}
