using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SizeMonitor.Interop;

public enum DiagnosticLogLevel { Off, Error, Information, Trace }

public sealed record ObservabilityConsent
{
    public bool CrashReports { get; init; }
    public bool PerformanceTelemetry { get; init; }
    public DiagnosticLogLevel LogLevel { get; init; } = DiagnosticLogLevel.Error;
}

public sealed class ObservabilityConsentStore(string directory)
{
    const int Version = 1;
    readonly string _directory = Path.GetFullPath(directory);
    string SettingsPath => Path.Combine(_directory, "observability.json");
    string QueuePath => Path.Combine(_directory, "observability-queue.jsonl");

    public async Task<ObservabilityConsent> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(SettingsPath)) return new ObservabilityConsent();
        await using FileStream stream = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Document? document;
        try { document = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: token); }
        catch (JsonException ex) { throw new InvalidDataException("Observability settings are invalid.", ex); }
        return document is { Version: Version, Consent: not null }
            ? document.Consent
            : throw new InvalidDataException("Observability settings version is unsupported.");
    }

    public async Task SaveAsync(ObservabilityConsent consent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        Directory.CreateDirectory(_directory);
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(Version, consent), cancellationToken: token);
                await stream.FlushAsync(token);
            }
            File.Move(temporary, SettingsPath, true);
        }
        finally { TryDelete(temporary); }
    }

    public Task DeleteLocalDataAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        TryDelete(SettingsPath);
        TryDelete(QueuePath);
        return Task.CompletedTask;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    sealed record Document(int Version, ObservabilityConsent Consent);
}

public sealed record PerformanceTelemetryEvent(
    int SchemaVersion,
    string AppVersion,
    string Scanner,
    string DurationBucket,
    string FileCountBucket,
    string DirectoryCountBucket,
    string ByteCountBucket,
    string Outcome);

public static class PerformanceTelemetry
{
    public static PerformanceTelemetryEvent? Create(ObservabilityConsent consent, string appVersion,
        ScannerKind scanner, TimeSpan duration, ulong files, ulong directories, ulong bytes, string outcome)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (!consent.PerformanceTelemetry) return null;
        return new(1, SafeToken(appVersion), scanner.ToString(), Duration(duration),
            Magnitude(files), Magnitude(directories), Magnitude(bytes), SafeToken(outcome));
    }

    static string Duration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 1 => "lt-1s", < 5 => "1-5s", < 30 => "5-30s", < 120 => "30-120s",
        < 600 => "2-10m", _ => "gte-10m",
    };

    static string Magnitude(ulong value) => value switch
    {
        0 => "0", < 10 => "1-9", < 100 => "10-99", < 1_000 => "100-999",
        < 10_000 => "1k-10k", < 100_000 => "10k-100k", < 1_000_000 => "100k-1m",
        _ => "gte-1m",
    };

    static string SafeToken(string? value) => value is not null &&
        Regex.IsMatch(value, "^[A-Za-z0-9._-]{1,40}$") ? value : "unknown";
}

public sealed record CrashReportPreview(
    int SchemaVersion,
    string AppVersion,
    string ExceptionType,
    int HResult,
    IReadOnlyList<string> ManagedFrames,
    string Runtime,
    string OperatingSystem);

public sealed class CrashReportService(HttpClient httpClient)
{
    const int MaximumFrames = 40;
    readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public static CrashReportPreview CreatePreview(Exception exception, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string[] frames = (exception.StackTrace ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(frame => Regex.Replace(frame, @"\s+in\s+.*?:line\s+\d+", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            .Select(frame => frame.Length > 300 ? frame[..300] : frame)
            .Take(MaximumFrames)
            .ToArray();
        return new(1, SafeVersion(appVersion), exception.GetType().FullName ?? exception.GetType().Name,
            exception.HResult, frames, Environment.Version.ToString(), Environment.OSVersion.VersionString);
    }

    public async Task SubmitAsync(Uri endpoint, CrashReportPreview preview, ObservabilityConsent consent,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(consent);
        if (!consent.CrashReports)
            throw new InvalidOperationException("Crash-report consent is required before submission.");
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Crash reports require an HTTPS endpoint without embedded credentials.", nameof(endpoint));
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(endpoint, preview, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The crash-report service rejected the report.", null, response.StatusCode);
    }

    static string SafeVersion(string? value) => value is not null &&
        Regex.IsMatch(value, "^[0-9A-Za-z._-]{1,40}$") ? value : "unknown";
}
