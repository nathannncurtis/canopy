using System.Text.Json;

namespace SizeMonitor.Interop;

public sealed record ScanLocation(string Path, string DisplayName, bool IsFavorite,
    DateTimeOffset LastUsedUtc, ulong UseCount);

public sealed class LocationHistoryStore
{
    const int SchemaVersion = 1;
    const int MaxFileBytes = 4 * 1024 * 1024;
    readonly string _path;
    readonly int _recentLimit;
    readonly SemaphoreSlim _gate = new(1, 1);
    List<ScanLocation> _locations = [];

    public LocationHistoryStore(string path, int recentLimit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (recentLimit < 0 || recentLimit > 10_000) throw new ArgumentOutOfRangeException(nameof(recentLimit));
        _path = Path.GetFullPath(path);
        _recentLimit = recentLimit;
    }

    public IReadOnlyList<ScanLocation> Locations
    {
        get
        {
            _gate.Wait();
            try { return Order(_locations).ToArray(); }
            finally { _gate.Release(); }
        }
    }

    public Task TouchAsync(string path, string? displayName = null,
        DateTimeOffset? usedUtc = null, CancellationToken cancellationToken = default) =>
        MutateAsync(locations =>
        {
            string normalized = NormalizeWindowsPath(path);
            int index = Find(locations, normalized);
            DateTimeOffset timestamp = (usedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            if (index >= 0)
            {
                ScanLocation old = locations[index];
                locations[index] = old with
                {
                    DisplayName = CleanDisplayName(displayName) ?? old.DisplayName,
                    LastUsedUtc = timestamp,
                    UseCount = checked(old.UseCount + 1),
                };
            }
            else
            {
                locations.Add(new(normalized, CleanDisplayName(displayName) ?? DefaultName(normalized),
                    false, timestamp, 1));
            }
            Trim(locations);
        }, cancellationToken);

    public Task PinAsync(string path, string? displayName = null,
        CancellationToken cancellationToken = default) => MutateAsync(locations =>
    {
        string normalized = NormalizeWindowsPath(path);
        int index = Find(locations, normalized);
        if (index >= 0)
        {
            ScanLocation old = locations[index];
            locations[index] = old with
            {
                IsFavorite = true,
                DisplayName = CleanDisplayName(displayName) ?? old.DisplayName,
            };
        }
        else
        {
            locations.Add(new(normalized, CleanDisplayName(displayName) ?? DefaultName(normalized),
                true, DateTimeOffset.UtcNow, 0));
        }
        Trim(locations);
    }, cancellationToken);

    public Task UnpinAsync(string path, CancellationToken cancellationToken = default) =>
        MutateAsync(locations =>
        {
            int index = Find(locations, NormalizeWindowsPath(path));
            if (index >= 0) locations[index] = locations[index] with { IsFavorite = false };
            Trim(locations);
        }, cancellationToken);

    public Task RemoveAsync(string path, CancellationToken cancellationToken = default) =>
        MutateAsync(locations =>
        {
            int index = Find(locations, NormalizeWindowsPath(path));
            if (index >= 0) locations.RemoveAt(index);
        }, cancellationToken);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) { _locations = []; return; }
            var info = new FileInfo(_path);
            if (info.Length > MaxFileBytes) throw new InvalidDataException("Location history is too large.");
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            HistoryDocument? document;
            try { document = await JsonSerializer.DeserializeAsync<HistoryDocument>(stream, cancellationToken: cancellationToken); }
            catch (JsonException ex) { throw new InvalidDataException("Location history JSON is invalid.", ex); }
            if (document is null || document.Version != SchemaVersion || document.Locations is null)
                throw new InvalidDataException("Location history schema is unsupported.");
            if (document.Locations.Count > 10_000) throw new InvalidDataException("Location history has too many entries.");
            var loaded = new List<ScanLocation>(document.Locations.Count);
            foreach (ScanLocation item in document.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized;
                try { normalized = NormalizeWindowsPath(item.Path); }
                catch (ArgumentException ex) { throw new InvalidDataException("A saved location path is invalid.", ex); }
                if (Find(loaded, normalized) >= 0 || string.IsNullOrWhiteSpace(item.DisplayName) ||
                    item.LastUsedUtc.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("Location history contains an invalid or duplicate entry.");
                loaded.Add(item with { Path = normalized });
            }
            Trim(loaded);
            _locations = loaded;
        }
        finally { _gate.Release(); }
    }

    async Task MutateAsync(Action<List<ScanLocation>> mutation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var updated = new List<ScanLocation>(_locations);
            mutation(updated);
            await SaveCoreAsync(updated, token);
            _locations = updated;
        }
        finally { _gate.Release(); }
    }

    async Task SaveCoreAsync(List<ScanLocation> locations, CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new HistoryDocument(SchemaVersion, Order(locations).ToList()), cancellationToken: token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            await MoveWithRetryAsync(temporary, token);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    async Task MoveWithRetryAsync(string temporary, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, _path, true);
                return;
            }
            catch (Exception ex) when (attempt < 4 &&
                                       ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 << attempt), token);
            }
        }
    }

    void Trim(List<ScanLocation> locations)
    {
        var evict = locations.Where(x => !x.IsFavorite).OrderByDescending(x => x.LastUsedUtc)
            .Skip(_recentLimit).Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        locations.RemoveAll(x => evict.Contains(x.Path));
    }

    static IEnumerable<ScanLocation> Order(IEnumerable<ScanLocation> locations) =>
        locations.OrderByDescending(x => x.IsFavorite).ThenByDescending(x => x.LastUsedUtc)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase);

    static int Find(List<ScanLocation> locations, string path) =>
        locations.FindIndex(x => StringComparer.OrdinalIgnoreCase.Equals(x.Path, path));

    public static string NormalizeWindowsPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string value = path.Trim().Replace('/', '\\');
        bool unc = value.StartsWith("\\\\", StringComparison.Ordinal);
        string prefix = unc ? "\\\\" : string.Empty;
        string rest = unc ? value[2..] : value;
        while (rest.Contains("\\\\", StringComparison.Ordinal)) rest = rest.Replace("\\\\", "\\", StringComparison.Ordinal);
        value = prefix + rest;
        while (value.Length > 3 && value.EndsWith('\\')) value = value[..^1];
        if (value.Length == 2 && value[1] == ':') value += "\\";
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new ArgumentException("Path contains invalid characters.", nameof(path));
        return value;
    }

    static string? CleanDisplayName(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    static string DefaultName(string path) => path.Length == 3 && path[1] == ':'
        ? path : path[(path.LastIndexOf('\\') + 1)..];

    sealed record HistoryDocument(int Version, List<ScanLocation> Locations);
}
