using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SizeMonitor.Interop;

public sealed record DiagnosticBundleInput
{
    public required string ApplicationVersion { get; init; }
    public required string OperatingSystem { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string ProcessArchitecture { get; init; }
    public string LogText { get; init; } = string.Empty;
    public bool IncludeSensitivePaths { get; init; }
}

public static partial class DiagnosticBundle
{
    public const int MaxLogBytes = 8 * 1024 * 1024;
    const int MaxMetadataCharacters = 4096;
    const string Redaction = "[REDACTED_PATH]";
    static readonly DateTimeOffset StableTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task CreateAsync(
        DiagnosticBundleInput input,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        Validate(input);
        cancellationToken.ThrowIfCancellationRequested();

        string logs = input.IncludeSensitivePaths ? input.LogText : RedactPaths(input.LogText);
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true, Utf8WithoutBom);
        await WriteEntryAsync(archive, "manifest.json", CreateManifest(input), cancellationToken)
            .ConfigureAwait(false);
        await WriteEntryAsync(archive, "logs.txt", Utf8WithoutBom.GetBytes(logs), cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task CreateFileAsync(
        DiagnosticBundleInput input,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(input);
        cancellationToken.ThrowIfCancellationRequested();

        string destinationPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The bundle path has no parent directory.");
        string temporaryPath = Path.Combine(
            directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CreateAsync(input, destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string RedactPaths(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string redacted = DoubleQuotedWindowsPath().Replace(text, match =>
            string.Concat(match.Value.AsSpan(0, 1), Redaction, match.Value.AsSpan(match.Value.Length - 1)));
        redacted = SingleQuotedWindowsPath().Replace(redacted, match =>
            string.Concat(match.Value.AsSpan(0, 1), Redaction, match.Value.AsSpan(match.Value.Length - 1)));
        return UnquotedWindowsPath().Replace(redacted, Redaction);
    }

    static void Validate(DiagnosticBundleInput input)
    {
        ValidateMetadata(input.ApplicationVersion, nameof(input.ApplicationVersion));
        ValidateMetadata(input.OperatingSystem, nameof(input.OperatingSystem));
        ValidateMetadata(input.RuntimeVersion, nameof(input.RuntimeVersion));
        ValidateMetadata(input.ProcessArchitecture, nameof(input.ProcessArchitecture));
        if (Utf8WithoutBom.GetByteCount(input.LogText) > MaxLogBytes)
            throw new ArgumentException($"Log text exceeds the {MaxLogBytes}-byte bundle limit.", nameof(input));
    }

    static void ValidateMetadata(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Diagnostic metadata cannot be empty.", name);
        if (value.Length > MaxMetadataCharacters)
            throw new ArgumentException($"Diagnostic metadata cannot exceed {MaxMetadataCharacters} characters.", name);
    }

    static byte[] CreateManifest(DiagnosticBundleInput input)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("applicationVersion", input.ApplicationVersion);
            json.WriteString("operatingSystem", input.OperatingSystem);
            json.WriteString("runtimeVersion", input.RuntimeVersion);
            json.WriteString("processArchitecture", input.ProcessArchitecture);
            json.WriteBoolean("sensitivePathsIncluded", input.IncludeSensitivePaths);
            json.WriteString("logEntry", "logs.txt");
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = StableTimestamp;
        await using Stream stream = entry.Open();
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    // Double-quoted paths may safely contain spaces and apostrophes.
    [GeneratedRegex("(?i)\"(?:[a-z]:\\\\|\\\\\\\\[^\\\\/\\r\\n\"]+\\\\[^\\\\/\\r\\n\"]+)[^\\r\\n\"]*\"", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuotedWindowsPath();

    // Greedy-to-the-last-quote preserves apostrophes that are legal inside path segments.
    [GeneratedRegex("(?i)'(?:[a-z]:\\\\|\\\\\\\\[^\\\\/\\r\\n']+\\\\[^\\\\/\\r\\n']+)[^\\r\\n]*'", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedWindowsPath();

    // Unquoted paths stop at punctuation delimiters. Because those characters are legal
    // in filenames, callers should quote paths containing punctuation for complete redaction.
    // The UNC prefix accepts a share root without requiring a further path component.
    [GeneratedRegex("(?i)(?<![a-z0-9])(?:[a-z]:\\\\|\\\\\\\\[^\\\\/\\r\\n,;()\\[\\]]+\\\\[^\\\\/\\r\\n,;()\\[\\]]+)[^\\r\\n,;()\\[\\]]*", RegexOptions.CultureInvariant)]
    private static partial Regex UnquotedWindowsPath();
}
