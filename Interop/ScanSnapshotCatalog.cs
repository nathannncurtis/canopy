using System.Text.Json;

namespace SizeMonitor.Interop;

public sealed record SnapshotRetentionPolicy(int MaximumPerTarget = 30,
    TimeSpan? MaximumAge = null, long? MaximumBytesPerTarget = null)
{
    public void Validate()
    {
        if (MaximumPerTarget < 1) throw new ArgumentOutOfRangeException(nameof(MaximumPerTarget));
        if (MaximumAge is { } age && age <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        if (MaximumBytesPerTarget is <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumBytesPerTarget));
    }
}

public sealed record ScanSnapshotMetadata(Guid Id, string Target, string SnapshotFile,
    DateTimeOffset CapturedUtc, ulong TotalBytes, ulong FileCount, ulong DirectoryCount,
    long SnapshotBytes);

public sealed record ScanTrendSample(DateTimeOffset CapturedUtc, ulong TotalBytes);

public sealed record CapacityForecast(bool HasSufficientData, DateTimeOffset? EstimatedFullUtc,
    double DailyGrowthBytes, double Confidence, string? Reason);

/// <summary>Owns a versioned snapshot catalog and retention policy inside one configured directory.</summary>
public sealed class ScanSnapshotCatalog
{
    const int SchemaVersion = 1;
    const int MaximumCatalogBytes = 16 * 1024 * 1024;
    readonly string _directory;
    readonly string _directoryPrefix;
    readonly string _catalogPath;
    readonly SnapshotRetentionPolicy _policy;
    readonly SemaphoreSlim _gate = new(1, 1);
    List<ScanSnapshotMetadata> _entries = [];

    public ScanSnapshotCatalog(string directory, SnapshotRetentionPolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _directoryPrefix = Path.TrimEndingDirectorySeparator(_directory) + Path.DirectorySeparatorChar;
        _catalogPath = Path.Combine(_directory, "catalog.json");
        _policy = policy ?? new();
        _policy.Validate();
    }

    public IReadOnlyList<ScanSnapshotMetadata> Entries
    {
        get { _gate.Wait(); try { return Ordered(_entries).ToArray(); } finally { _gate.Release(); } }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            if (!File.Exists(_catalogPath)) { _entries = []; return; }
            if (new FileInfo(_catalogPath).Length > MaximumCatalogBytes)
                throw new InvalidDataException("Snapshot catalog is too large.");
            await using var stream = new FileStream(_catalogPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            CatalogDocument? document;
            try { document = await JsonSerializer.DeserializeAsync<CatalogDocument>(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false); }
            catch (JsonException ex) { throw new InvalidDataException("Snapshot catalog JSON is invalid.", ex); }
            if (document is null || document.Version != SchemaVersion || document.Entries is null)
                throw new InvalidDataException("Snapshot catalog schema is unsupported.");
            if (document.Entries.Count > 100_000) throw new InvalidDataException("Snapshot catalog has too many entries.");
            var ids = new HashSet<Guid>();
            foreach (ScanSnapshotMetadata entry in document.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateEntry(entry);
                if (!ids.Add(entry.Id)) throw new InvalidDataException("Snapshot catalog contains duplicate IDs.");
            }
            _entries = Ordered(document.Entries).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<ScanSnapshotMetadata> CaptureAsync(string target, ScanResultManaged result,
        DateTimeOffset? capturedUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(result);
        string normalizedTarget = LocationHistoryStore.NormalizeWindowsPath(target);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            Guid id = Guid.NewGuid();
            string fileName = $"{id:N}.canopy";
            string snapshotPath = ResolveOwnedFile(fileName);
            await ScanSnapshotStore.SaveAsync(snapshotPath, result, cancellationToken).ConfigureAwait(false);
            var metadata = new ScanSnapshotMetadata(id, normalizedTarget, fileName,
                (capturedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), result.TotalBytes,
                result.FileCount, result.DirCount, new FileInfo(snapshotPath).Length);
            var updated = new List<ScanSnapshotMetadata>(_entries) { metadata };
            List<ScanSnapshotMetadata> removed = SelectPruned(updated, metadata.CapturedUtc);
            updated.RemoveAll(item => removed.Any(old => old.Id == item.Id));
            await SaveCatalogAsync(updated, cancellationToken).ConfigureAwait(false);
            _entries = Ordered(updated).ToList();
            foreach (ScanSnapshotMetadata old in removed) DeleteOwnedFile(old.SnapshotFile);
            return metadata;
        }
        finally { _gate.Release(); }
    }

    public Task<ScanResultManaged> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ScanSnapshotMetadata entry;
        _gate.Wait(cancellationToken);
        try { entry = _entries.SingleOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException($"Snapshot {id} is not in the catalog."); }
        finally { _gate.Release(); }
        return ScanSnapshotStore.LoadAsync(ResolveOwnedFile(entry.SnapshotFile), cancellationToken);
    }

    public IReadOnlyList<ScanTrendSample> GetTrend(string target)
    {
        string normalized = LocationHistoryStore.NormalizeWindowsPath(target);
        return Entries.Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.Target, normalized))
            .OrderBy(x => x.CapturedUtc).Select(x => new ScanTrendSample(x.CapturedUtc, x.TotalBytes)).ToArray();
    }

    public CapacityForecast ForecastFull(string target, ulong capacityBytes)
    {
        ScanTrendSample[] samples = GetTrend(target).ToArray();
        if (samples.Length < 3) return new(false, null, 0, 0, "At least three samples are required.");
        double origin = samples[0].CapturedUtc.UtcTicks;
        double[] x = samples.Select(s => (s.CapturedUtc.UtcTicks - origin) / TimeSpan.TicksPerDay).ToArray();
        double[] y = samples.Select(s => (double)s.TotalBytes).ToArray();
        double meanX = x.Average(), meanY = y.Average();
        double denominator = x.Sum(v => (v - meanX) * (v - meanX));
        if (denominator <= 0) return new(false, null, 0, 0, "Samples must span more than one instant.");
        double slope = x.Zip(y).Sum(pair => (pair.First - meanX) * (pair.Second - meanY)) / denominator;
        double intercept = meanY - slope * meanX;
        double totalVariance = y.Sum(v => (v - meanY) * (v - meanY));
        double residual = x.Zip(y).Sum(pair => Math.Pow(pair.Second - (intercept + slope * pair.First), 2));
        double confidence = totalVariance <= 0 ? 0 : Math.Clamp(1 - residual / totalVariance, 0, 1);
        if (slope <= 0) return new(false, null, slope, confidence, "No positive growth trend was detected.");
        double fullDay = (capacityBytes - intercept) / slope;
        DateTimeOffset estimate = samples[0].CapturedUtc.AddDays(fullDay);
        if (estimate <= samples[^1].CapturedUtc) return new(true, samples[^1].CapturedUtc, slope, confidence, null);
        return new(true, estimate, slope, confidence, null);
    }

    List<ScanSnapshotMetadata> SelectPruned(List<ScanSnapshotMetadata> entries, DateTimeOffset now)
    {
        var removed = new List<ScanSnapshotMetadata>();
        foreach (IGrouping<string, ScanSnapshotMetadata> group in entries.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase))
        {
            ScanSnapshotMetadata[] newest = group.OrderByDescending(x => x.CapturedUtc).ThenBy(x => x.Id).ToArray();
            long retainedBytes = 0;
            for (int i = 0; i < newest.Length; i++)
            {
                ScanSnapshotMetadata item = newest[i];
                bool expired = _policy.MaximumAge is { } age && now - item.CapturedUtc > age;
                bool overCount = i >= _policy.MaximumPerTarget;
                bool overBytes = _policy.MaximumBytesPerTarget is { } maximum && retainedBytes + item.SnapshotBytes > maximum;
                if (expired || overCount || overBytes) removed.Add(item);
                else retainedBytes = checked(retainedBytes + item.SnapshotBytes);
            }
        }
        return removed;
    }

    async Task SaveCatalogAsync(List<ScanSnapshotMetadata> entries, CancellationToken token)
    {
        string temporary = _catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new CatalogDocument(SchemaVersion, Ordered(entries).ToList()), cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, _catalogPath, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    void ValidateEntry(ScanSnapshotMetadata entry)
    {
        if (entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Target) || entry.SnapshotBytes < 0 ||
            entry.CapturedUtc.Offset != TimeSpan.Zero) throw new InvalidDataException("Snapshot catalog entry is invalid.");
        _ = ResolveOwnedFile(entry.SnapshotFile);
    }
    string ResolveOwnedFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) || fileName != Path.GetFileName(fileName) ||
            !fileName.EndsWith(".canopy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snapshot catalog contains an unsafe file path.");
        string full = Path.GetFullPath(Path.Combine(_directory, fileName));
        if (!full.StartsWith(_directoryPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snapshot path escapes the configured catalog directory.");
        return full;
    }
    void DeleteOwnedFile(string fileName) { string full = ResolveOwnedFile(fileName); if (File.Exists(full)) File.Delete(full); }
    static IEnumerable<ScanSnapshotMetadata> Ordered(IEnumerable<ScanSnapshotMetadata> entries) =>
        entries.OrderBy(x => x.Target, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.CapturedUtc).ThenBy(x => x.Id);
    sealed record CatalogDocument(int Version, List<ScanSnapshotMetadata> Entries);
}
