using System.IO;
using SizeMonitor.Interop;

namespace SizeMonitor.Helpers;

internal static class Logger
{
    static readonly string _path = Path.Combine(AppDataPaths.DataDirectory, "canopy.log");
    static readonly BoundedDiagnosticLogger _logger =
        new(_path, DiagnosticLogLevel.Information);

    internal static string LogPath => _path;

    internal static DiagnosticLogLevel Level
    {
        get => _logger.Level;
        set => _logger.Level = value;
    }

    internal static void Error(string context, Exception ex)
    {
        try { _logger.Write(DiagnosticLogLevel.Error, context, ex); }
        catch { /* log failure must never cascade */ }
    }

    internal static void Info(string message)
    {
        try { _logger.Write(DiagnosticLogLevel.Information, message); }
        catch { }
    }

    internal static void Trace(string message)
    {
        try { _logger.Write(DiagnosticLogLevel.Trace, message); }
        catch { }
    }
}
