using System.Globalization;
using System.Text;

namespace SizeMonitor.Interop;

public sealed class BoundedDiagnosticLogger
{
    readonly object _gate = new();
    readonly string _path;
    readonly long _maximumBytes;
    readonly int _retainedFiles;

    public BoundedDiagnosticLogger(string path, DiagnosticLogLevel level = DiagnosticLogLevel.Error,
        long maximumBytes = 2 * 1024 * 1024, int retainedFiles = 3)
    {
        _path = System.IO.Path.GetFullPath(path);
        if (maximumBytes is < 64 * 1024 or > 32 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (retainedFiles is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
        Level = level;
    }

    public DiagnosticLogLevel Level { get; set; }
    public string Path => _path;

    public void Write(DiagnosticLogLevel level, string message, Exception? exception = null)
    {
        if (level == DiagnosticLogLevel.Off || Level == DiagnosticLogLevel.Off || level > Level) return;
        string safeMessage = Sanitize(message);
        string exceptionText = exception is null ? string.Empty :
            $" | {exception.GetType().FullName} (0x{exception.HResult:X8}): {Sanitize(exception.Message)}";
        string line = $"[{DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}] {level}: {safeMessage}{exceptionText}{Environment.NewLine}";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        lock (_gate)
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (directory is not null) Directory.CreateDirectory(directory);
            if (File.Exists(_path) && new FileInfo(_path).Length + bytes.Length > _maximumBytes) Rotate();
            using FileStream stream = new(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.WriteThrough);
            stream.Write(bytes);
        }
    }

    void Rotate()
    {
        for (int index = _retainedFiles; index >= 1; index--)
        {
            string source = index == 1 ? _path : _path + "." + (index - 1).ToString(CultureInfo.InvariantCulture);
            string destination = _path + "." + index.ToString(CultureInfo.InvariantCulture);
            if (!File.Exists(source)) continue;
            if (index == _retainedFiles) File.Delete(destination);
            File.Move(source, destination, true);
        }
    }

    static string Sanitize(string? value) => (value ?? string.Empty)
        .Replace('\r', ' ').Replace('\n', ' ');
}
