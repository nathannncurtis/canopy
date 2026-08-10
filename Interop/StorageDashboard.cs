using System.Text.Json;
using System.Text.Json.Serialization;

namespace SizeMonitor.Interop;

public sealed record StorageDashboardItem(uint NodeIndex, string Name, ulong Bytes, ulong FileCount);
public sealed record StorageDashboardBucket(string Label, ulong Files, ulong Bytes, double Percentage)
{
    public string AccessibleText => $"{Label}: {Files:N0} files, {Bytes:N0} bytes, {Percentage:F1} percent";
}
public sealed record StorageDashboardResult(ulong TotalBytes, ulong AccountedFileBytes, ulong Files, ulong Directories,
    IReadOnlyList<StorageDashboardItem> LargestFiles, IReadOnlyList<StorageDashboardItem> LargestFolders,
    IReadOnlyList<StorageDashboardItem> MostFiles, IReadOnlyList<StorageDashboardBucket> FileTypes,
    IReadOnlyList<StorageDashboardBucket> FileAges, IReadOnlyList<StorageDashboardBucket> SizeHistogram,
    ulong MissingAgeFiles, ulong WorkItems);

public sealed record StorageDashboardOptions
{
    public int TopCount { get; init; } = 10;
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<uint, DateTimeOffset>? LastWriteUtc { get; init; }
}

public static class StorageDashboard
{
    const int MaximumFileTypeBuckets = 20;
    static readonly (ulong Limit, string Label)[] Sizes = [(1024, "< 1 KiB"), (1UL << 20, "1 KiB–1 MiB"),
        (1UL << 30, "1 MiB–1 GiB"), (ulong.MaxValue, "≥ 1 GiB")];
    static readonly (TimeSpan Limit, string Label)[] Ages = [(TimeSpan.FromDays(1), "Today"),
        (TimeSpan.FromDays(30), "1–30 days"), (TimeSpan.FromDays(365), "1–12 months"),
        (TimeSpan.MaxValue, "Over 1 year")];

    public static StorageDashboardResult Generate(ScanResultManaged result, StorageDashboardOptions? options = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(result); options ??= new();
        if (options.TopCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options));
        TreemapHierarchyMetrics hierarchy = TreemapHierarchyMetrics.Calculate(result);
        var files = new List<StorageDashboardItem>(); var folders = new List<StorageDashboardItem>(); var most = new List<StorageDashboardItem>();
        var types = new Dictionary<string, (ulong Files, ulong Bytes)>(StringComparer.OrdinalIgnoreCase);
        var sizeCounts = new (ulong Files, ulong Bytes)[Sizes.Length]; var ageCounts = new (ulong Files, ulong Bytes)[Ages.Length];
        ulong accounted = 0, fileCount = 0, directoryCount = 0, missingAge = 0, work = hierarchy.WorkItems;
        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            if ((index & 0x3fff) == 0) token.ThrowIfCancellationRequested(); work++;
            ScanNode node = result.Nodes[index]; bool directory = (node.Flags & ScanNodeFlags.Directory) != 0;
            if (directory)
            {
                directoryCount = Sat(directoryCount, 1); var count = hierarchy.Counts[(int)index].Files;
                var item = new StorageDashboardItem(index, result.Names[index], node.Size, count);
                if (node.Parent != uint.MaxValue)
                {
                    AddTop(folders, item, options.TopCount, static value => value.Bytes);
                    AddTop(most, item, options.TopCount, static value => value.FileCount);
                }
            }
            else
            {
                fileCount = Sat(fileCount, 1); accounted = Sat(accounted, node.Size);
                AddTop(files, new(index, result.Names[index], node.Size, 1), options.TopCount, static item => item.Bytes);
                string type = FileNameFacts.ExtensionOf(result.Names[index]); if (type.Length == 0) type = "(no extension)";
                (ulong count, ulong bytes) = types.GetValueOrDefault(type); types[type] = (Sat(count, 1), Sat(bytes, node.Size));
                int size = Array.FindIndex(Sizes, bucket => node.Size < bucket.Limit); if (size < 0) size = Sizes.Length - 1;
                sizeCounts[size] = (Sat(sizeCounts[size].Files, 1), Sat(sizeCounts[size].Bytes, node.Size));
                DateTimeOffset? stamp = null;
                if (options.LastWriteUtc?.TryGetValue(index, out DateTimeOffset injected) == true)
                    stamp = injected;
                else if (options.LastWriteUtc is null && index < result.Metadata.Length)
                    stamp = result.Metadata[index]?.LastWriteTimeUtc;
                if (stamp is not null)
                {
                    TimeSpan age = options.CapturedUtc - stamp.Value; if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                    int bucket = Array.FindIndex(Ages, item => age <= item.Limit); if (bucket < 0) bucket = Ages.Length - 1;
                    ageCounts[bucket] = (Sat(ageCounts[bucket].Files, 1), Sat(ageCounts[bucket].Bytes, node.Size));
                }
                else missingAge = Sat(missingAge, 1);
            }
        }
        return new(result.TotalBytes, accounted, fileCount, directoryCount, files, folders, most,
            TypeBuckets(types, accounted),
            Buckets(Ages.Select((item, i) => (item.Label, ageCounts[i].Files, ageCounts[i].Bytes)), accounted),
            Buckets(Sizes.Select((item, i) => (item.Label, sizeCounts[i].Files, sizeCounts[i].Bytes)), accounted), missingAge, work);
    }
    static void AddTop(List<StorageDashboardItem> values, StorageDashboardItem item, int count,
        Func<StorageDashboardItem, ulong> key)
    {
        values.Add(item); values.Sort((a, b) => { int order = key(b).CompareTo(key(a)); return order != 0 ? order : a.NodeIndex.CompareTo(b.NodeIndex); });
        if (values.Count > count) values.RemoveAt(values.Count - 1);
    }
    static IReadOnlyList<StorageDashboardBucket> Buckets(IEnumerable<(string Label, ulong Files, ulong Bytes)> values, ulong total) =>
        values.Where(item => item.Files > 0).OrderByDescending(item => item.Bytes).ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(item => new StorageDashboardBucket(item.Label, item.Files, item.Bytes, total == 0 ? 0 : item.Bytes * 100d / total)).ToArray();
    static IReadOnlyList<StorageDashboardBucket> TypeBuckets(
        IReadOnlyDictionary<string, (ulong Files, ulong Bytes)> values, ulong total)
    {
        var ordered = values.Select(pair => (Label: pair.Key, pair.Value.Files, pair.Value.Bytes))
            .OrderByDescending(item => item.Bytes).ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visible = ordered.Take(MaximumFileTypeBuckets).ToList();
        if (ordered.Length > MaximumFileTypeBuckets)
        {
            ulong otherFiles = 0, otherBytes = 0;
            foreach (var item in ordered.Skip(MaximumFileTypeBuckets))
            { otherFiles = Sat(otherFiles, item.Files); otherBytes = Sat(otherBytes, item.Bytes); }
            visible.Add(("Other", otherFiles, otherBytes));
        }
        return visible.Select(item => new StorageDashboardBucket(item.Label, item.Files, item.Bytes,
            total == 0 ? 0 : item.Bytes * 100d / total)).ToArray();
    }
    static ulong Sat(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}

public sealed record StorageDashboardPreferences
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<string> EnabledWidgets { get; init; } = ["overview", "largest-files", "largest-folders", "most-files", "types", "ages", "sizes"];
    public IReadOnlyList<string> QuickLocations { get; init; } = DefaultQuickLocations();
    static string[] DefaultQuickLocations() => DefaultLocations.Build();
}

public sealed class StorageDashboardPreferenceStore(string path)
{
    const int Version = 1; const long MaximumBytes = 64 * 1024;
    readonly SemaphoreSlim _gate = new(1, 1);
    long _latestSave;
    static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public async Task<StorageDashboardPreferences> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return new();
            await using FileStream stream = File.OpenRead(path);
            StorageDashboardPreferences? value = await JsonSerializer.DeserializeAsync<StorageDashboardPreferences>(stream, Json, token);
            return Normalize(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return new(); }
    }
    public async Task SaveAsync(StorageDashboardPreferences value, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = Normalize(value); string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        long save = Interlocked.Increment(ref _latestSave);
        await _gate.WaitAsync(token); string temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try { if (save != Volatile.Read(ref _latestSave)) return;
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, value, Json, token); File.Move(temporary, full, true); }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } _gate.Release(); }
    }
    static StorageDashboardPreferences Normalize(StorageDashboardPreferences? value)
    {
        if (value is not { Version: Version }) return new();
        string[] widgets = (value.EnabledWidgets ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        string[] paths = (value.QuickLocations ?? []).Select(TryFullPath).Where(item => item is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
        return value with { EnabledWidgets = widgets, QuickLocations = paths };
    }
    static string? TryFullPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

public static class DefaultLocations
{
    public static string[] Build()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = string.IsNullOrWhiteSpace(profile) ? string.Empty : Path.Combine(profile, "Downloads");
        return new[] { profile, Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), downloads }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
