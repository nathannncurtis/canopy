namespace SizeMonitor.Interop;

public sealed record DiskUsageSummaryOptions
{
    public int LargestFolderCount { get; init; } = 5;
    public int LargestCategoryCount { get; init; } = 5;
    public ulong? CapacityBytes { get; init; }
    public IReadOnlyList<string> ScanLimitations { get; init; } = [];
}

public readonly record struct DiskUsageSummaryItem(uint NodeIndex, string Name, ulong Bytes);

public sealed record DiskUsageSummaryResult(
    ulong TotalBytes,
    ulong AccountedFileBytes,
    ulong? CapacityBytes,
    double? CapacityPercentage,
    IReadOnlyList<DiskUsageSummaryItem> LargestFolders,
    IReadOnlyList<StorageDistributionBucket> LargestCategories,
    int AnomalyCount,
    IReadOnlyList<string> Limitations)
{
    public bool IsComplete => Limitations.Count == 0;
    public bool TotalsReconcile => TotalBytes == AccountedFileBytes;
}

/// <summary>Builds the deterministic, first-look summary shown for a completed scan.</summary>
public static class DiskUsageSummary
{
    const uint NoNode = uint.MaxValue;

    public static DiskUsageSummaryResult Generate(
        ScanResultManaged result,
        DiskUsageSummaryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new DiskUsageSummaryOptions();
        if (options.LargestFolderCount < 0 || options.LargestCategoryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Summary limits cannot be negative.");
        if (result.Nodes is null || result.Names is null || result.Nodes.Length != result.Names.Length)
            throw new InvalidDataException("Node and name arrays must be non-null and have equal lengths.");

        ulong fileBytes = 0;
        var folders = new List<DiskUsageSummaryItem>();
        for (int index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            string name = result.Names[index]
                ?? throw new InvalidDataException($"Node {index} has a null name.");
            if ((node.Flags & ScanNodeFlags.Directory) == 0)
                fileBytes = SaturatingAdd(fileBytes, node.Size);
            else if (node.Parent != NoNode)
                folders.Add(new DiskUsageSummaryItem((uint)index, name, node.Size));
        }

        StorageDistributionBucket[] categories = StorageDistribution.ByCategory(result, cancellationToken)
            .Take(options.LargestCategoryCount)
            .ToArray();
        DiskUsageSummaryItem[] largestFolders = folders
            .OrderByDescending(item => item.Bytes)
            .ThenBy(item => item.NodeIndex)
            .Take(options.LargestFolderCount)
            .ToArray();
        int anomalyCount = ScanAnomalyFinder.Find(result, cancellationToken: cancellationToken).Count;

        string[] limitations = options.ScanLimitations
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        double? capacityPercentage = options.CapacityBytes is > 0
            ? Math.Min(100d, 100d * result.TotalBytes / options.CapacityBytes.Value)
            : null;

        return new DiskUsageSummaryResult(result.TotalBytes, fileBytes, options.CapacityBytes,
            capacityPercentage, largestFolders, categories, anomalyCount, limitations);
    }

    static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}
