using System.Text.Json;
using System.Text.Json.Serialization;

namespace SizeMonitor.Interop;

public enum TreemapLabelMode { Name, NameAndSize, NameSizeAndPercent, Hidden }
public enum TreemapColorMode { SiblingOrder, Extension, TopLevelDirectory }
public sealed record TreemapPresentationPreferences
{
    public int Version { get; init; } = 1;
    public TreemapLabelMode Labels { get; init; } = TreemapLabelMode.Name;
    public TreemapColorMode Colors { get; init; } = TreemapColorMode.SiblingOrder;
    public double MinimumLabelArea { get; init; } = 1800;
}

public readonly record struct TreemapViewport(double Scale, double X, double Y)
{
    public static TreemapViewport Fitted => new(1, 0, 0);
    public TreemapViewport Zoom(double factor, double pivotX, double pivotY, double width, double height)
    {
        if (!FinitePositive(width) || !FinitePositive(height) || !double.IsFinite(factor) || factor <= 0 ||
            !double.IsFinite(pivotX) || !double.IsFinite(pivotY)) return Fitted;
        double next = Math.Clamp(Scale * factor, 1, 8);
        double ratio = next / Scale;
        return Clamp(new(next, pivotX - (pivotX - X) * ratio, pivotY - (pivotY - Y) * ratio), width, height);
    }
    public TreemapViewport Pan(double dx, double dy, double width, double height) =>
        !FinitePositive(width) || !FinitePositive(height) || !double.IsFinite(dx) || !double.IsFinite(dy)
            ? Fitted : Clamp(this with { X = X + dx, Y = Y + dy }, width, height);
    static TreemapViewport Clamp(TreemapViewport value, double width, double height) => value with
    {
        X = Math.Clamp(value.X, width * (1 - value.Scale), 0),
        Y = Math.Clamp(value.Y, height * (1 - value.Scale), 0),
    };
    static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
}

public sealed record TreemapLegendEntry(string Key, int Bucket, ulong Bytes, bool IsOther = false);
public sealed record TreemapHierarchyMetrics(IReadOnlyList<(ulong Files, ulong Directories)> Counts, ulong WorkItems)
{
    public static TreemapHierarchyMetrics Calculate(ScanResultManaged result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var counts = new (ulong Files, ulong Directories)[result.Nodes.Length]; ulong work = 0;
        var order = new List<uint>(result.Nodes.Length); var pending = new Stack<uint>();
        for (uint i = 0; i < result.Nodes.Length; i++) if (result.Nodes[i].Parent == uint.MaxValue) pending.Push(i);
        while (pending.TryPop(out uint index))
        {
            order.Add(index); work++;
            for (uint child = result.Nodes[index].FirstChild; child != uint.MaxValue && child < result.Nodes.Length; child = result.Nodes[child].NextSibling)
                pending.Push(child);
        }
        for (int position = order.Count - 1; position >= 0; position--)
        {
            uint index = order[position]; bool directory = (result.Nodes[index].Flags & ScanNodeFlags.Directory) != 0;
            ulong files = directory ? 0UL : 1UL, dirs = directory ? 1UL : 0UL;
            for (uint child = result.Nodes[index].FirstChild; child != uint.MaxValue && child < result.Nodes.Length; child = result.Nodes[child].NextSibling)
            { files += counts[child].Files; dirs += counts[child].Directories; work++; }
            counts[index] = (files, dirs);
        }
        return new(counts, work);
    }
}

public static class TreemapPresentationRules
{
    public static int StableBucket(string key, int bucketCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);
        uint hash = 2166136261;
        foreach (char c in key.ToUpperInvariant()) { hash ^= c; hash *= 16777619; }
        return (int)(hash % (uint)bucketCount);
    }
    public static string ExtensionKey(string name) => Path.GetExtension(name) is { Length: > 0 } ext
        ? ext.ToLowerInvariant() : "(no extension)";
    public static string Label(string name, ulong bytes, double percent, TreemapLabelMode mode) => mode switch
    {
        TreemapLabelMode.Hidden => string.Empty,
        TreemapLabelMode.NameAndSize => $"{name} · {bytes:N0} B",
        TreemapLabelMode.NameSizeAndPercent => $"{name} · {bytes:N0} B · {percent:F1}%",
        _ => name,
    };
    public static IReadOnlyList<TreemapLegendEntry> Legend(IEnumerable<(string Key, ulong Bytes)> groups,
        int bucketCount, int maximumEntries = 7)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        var ordered = groups.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Key: group.Key, Bytes: group.Aggregate(0UL, (sum, item) =>
                ulong.MaxValue - sum < item.Bytes ? ulong.MaxValue : sum + item.Bytes)))
            .OrderByDescending(item => item.Bytes).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = ordered.Take(maximumEntries).Select(item => new TreemapLegendEntry(item.Key,
            StableBucket(item.Key, bucketCount), item.Bytes)).ToList();
        if (ordered.Length > maximumEntries)
            entries.Add(new("Other", -1, ordered.Skip(maximumEntries).Aggregate(0UL, (sum, item) =>
                ulong.MaxValue - sum < item.Bytes ? ulong.MaxValue : sum + item.Bytes), true));
        return entries;
    }
}

public sealed class TreemapPresentationStore(string path)
{
    const int Version = 1;
    const long MaximumBytes = 64 * 1024;
    readonly SemaphoreSlim _saveGate = new(1, 1);
    long _latestSave;
    static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public async Task<TreemapPresentationPreferences> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var info = new FileInfo(path); if (info.Length > MaximumBytes) return new();
            await using FileStream stream = info.OpenRead();
            TreemapPresentationPreferences? value = await JsonSerializer.DeserializeAsync<TreemapPresentationPreferences>(stream, Options, token);
            return IsValid(value) ? value! : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public async Task SaveAsync(TreemapPresentationPreferences value, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValid(value)) throw new ArgumentException("Treemap presentation preferences are invalid.", nameof(value));
        string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        long save = Interlocked.Increment(ref _latestSave);
        await _saveGate.WaitAsync(token);
        string temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (save != Volatile.Read(ref _latestSave)) return;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                await JsonSerializer.SerializeAsync(stream, value with { Version = Version }, Options, token);
            File.Move(temporary, full, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } _saveGate.Release(); }
    }
    static bool IsValid(TreemapPresentationPreferences? value) => value is { Version: Version } &&
        Enum.IsDefined(value.Labels) && Enum.IsDefined(value.Colors) &&
        double.IsFinite(value.MinimumLabelArea) && value.MinimumLabelArea is >= 0 and <= 1_000_000;
}
