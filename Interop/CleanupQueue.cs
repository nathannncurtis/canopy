using System.Text.Json;
using System.Text.Json.Serialization;

namespace SizeMonitor.Interop;

public sealed record CleanupQueueEntry
{
    public required string Path { get; init; }
    public ulong EstimatedBytes { get; init; }
    public CleanupRisk Risk { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset AddedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CleanupQueueDocument
{
    public int Version { get; init; } = 1;
    public ulong TypedConfirmationThresholdBytes { get; init; } = 1024UL * 1024 * 1024;
    public IReadOnlyList<CleanupQueueEntry> Items { get; init; } = [];
}

public sealed record CleanupSafetyAssessment(bool IsBlocked, bool RequiresTypedConfirmation,
    string? RequiredConfirmation, IReadOnlyList<string> Warnings);

public static class CleanupSafetyPolicy
{
    public static CleanupSafetyAssessment Evaluate(string path, ulong estimatedBytes,
        ulong confirmationThresholdBytes, int itemCount = 1)
    {
        string fullPath = Path.GetFullPath(path);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string systemRoot = Path.GetPathRoot(windows) ?? string.Empty;
        string[] protectedRoots = [windows, system, programFiles, programFilesX86, programData];
        bool blocked = (!string.IsNullOrWhiteSpace(systemRoot) &&
                        Path.TrimEndingDirectorySeparator(fullPath).Equals(Path.TrimEndingDirectorySeparator(systemRoot), StringComparison.OrdinalIgnoreCase)) ||
            protectedRoots.Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => IsAtOrBelow(fullPath, value));
        var warnings = new List<string>();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!blocked && IsAtOrBelow(fullPath, profile))
            warnings.Add("This item is inside the current user profile and may contain personal or application data.");
        if (!blocked && (IsAtOrBelow(fullPath, appData) || IsAtOrBelow(fullPath, localAppData)))
            warnings.Add("This item is application data; removing it may reset settings or make an application unusable.");
        if (blocked) warnings.Add("Protected Windows, program, system-drive, and shared application-data paths cannot be queued.");
        bool requires = !blocked && confirmationThresholdBytes > 0 && estimatedBytes >= confirmationThresholdBytes;
        string? phrase = requires ? $"DELETE {Math.Max(1, itemCount)} ITEMS" : null;
        return new(blocked, requires, phrase, warnings);
    }

    static bool IsAtOrBelow(string candidate, string root)
    {
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Persists cleanup intentions only. It deliberately exposes no delete operation.</summary>
public sealed class CleanupQueueStore(string path)
{
    public const int MaximumItems = 10_000;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    readonly SemaphoreSlim _gate = new(1, 1);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<CleanupQueueDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<CleanupQueueDocument> AddOrUpdateAsync(CleanupQueueEntry entry,
        CancellationToken cancellationToken = default)
    {
        CleanupQueueEntry normalized = Normalize(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupQueueDocument document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            CleanupSafetyAssessment safety = CleanupSafetyPolicy.Evaluate(normalized.Path,
                normalized.EstimatedBytes, document.TypedConfirmationThresholdBytes);
            if (safety.IsBlocked) throw new InvalidOperationException(string.Join(" ", safety.Warnings));
            List<CleanupQueueEntry> items = document.Items.ToList();
            int index = items.FindIndex(value => string.Equals(value.Path, normalized.Path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) items[index] = normalized; else items.Add(normalized);
            if (items.Count > MaximumItems) throw new InvalidDataException($"Cleanup queue exceeds {MaximumItems:N0} items.");
            CleanupQueueDocument updated = document with { Items = items };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<CleanupQueueDocument> RemoveAsync(string itemPath, CancellationToken cancellationToken = default)
    {
        string normalized = Path.GetFullPath(itemPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupQueueDocument document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            CleanupQueueDocument updated = document with { Items = document.Items.Where(value =>
                !string.Equals(value.Path, normalized, StringComparison.OrdinalIgnoreCase)).ToArray() };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<CleanupQueueDocument> SetThresholdAsync(ulong bytes, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CleanupQueueDocument updated = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)) with { TypedConfirmationThresholdBytes = bytes }; await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false); return updated; }
        finally { _gate.Release(); }
    }

    public static CleanupSafetyAssessment AssessPlan(CleanupQueueDocument document)
    {
        ulong total = 0; foreach (CleanupQueueEntry item in document.Items) total = ulong.MaxValue - total < item.EstimatedBytes ? ulong.MaxValue : total + item.EstimatedBytes;
        var warnings = document.Items.SelectMany(item => CleanupSafetyPolicy.Evaluate(item.Path, item.EstimatedBytes, ulong.MaxValue).Warnings).Distinct().ToArray();
        bool requires = document.TypedConfirmationThresholdBytes > 0 && total >= document.TypedConfirmationThresholdBytes;
        return new(false, requires, requires ? $"DELETE {document.Items.Count} ITEMS" : null, warnings);
    }

    async Task<CleanupQueueDocument> LoadCoreAsync(CancellationToken token)
    {
        if (!File.Exists(path)) return new();
        var info = new FileInfo(path); if (info.Length > MaximumFileBytes) throw new InvalidDataException("Cleanup queue file is too large.");
        await using Stream stream = File.OpenRead(path);
        CleanupQueueDocument document = await JsonSerializer.DeserializeAsync<CleanupQueueDocument>(stream, JsonOptions, token).ConfigureAwait(false) ?? throw new InvalidDataException("Cleanup queue is empty.");
        Validate(document); return document with { Items = document.Items.Select(Normalize).ToArray() };
    }

    async Task SaveCoreAsync(CleanupQueueDocument document, CancellationToken token)
    {
        Validate(document); string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await JsonSerializer.SerializeAsync(stream, document, JsonOptions, token); File.Move(temporary, fullPath, true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    static CleanupQueueEntry Normalize(CleanupQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry); string fullPath = Path.GetFullPath(entry.Path);
        if (fullPath.Length > 32767 || entry.Note?.Length > 2048 || entry.Tags.Count > 32) throw new InvalidDataException("Cleanup queue metadata exceeds its limit.");
        string[] tags = entry.Tags.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Any(value => value.Length > 64)) throw new InvalidDataException("Cleanup tags may contain at most 64 characters.");
        return entry with { Path = fullPath, Note = string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note.Trim(), Tags = tags };
    }

    static void Validate(CleanupQueueDocument document)
    {
        if (document.Version != 1 || document.Items is null || document.Items.Count > MaximumItems) throw new InvalidDataException("Cleanup queue document is invalid or unsupported.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (CleanupQueueEntry item in document.Items) if (!paths.Add(Normalize(item).Path)) throw new InvalidDataException("Cleanup queue contains duplicate paths.");
    }
}
