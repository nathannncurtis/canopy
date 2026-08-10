namespace SizeMonitor.Interop;

public static class PhysicalStorageContext
{
    public static IReadOnlyList<string> AllVolumeRoots(IEnumerable<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Select(path => Path.GetPathRoot(Path.GetFullPath(path)) ?? path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? SingleLocalVolumeRoot(IEnumerable<string> targets)
    {
        string[] roots = PhysicalStorageContext.AllVolumeRoots(targets).ToArray();
        return roots.Length == 1 && !roots[0].StartsWith(@"\\", StringComparison.Ordinal)
            ? roots[0] : null;
    }
}

public sealed record PhysicalStorageAccounting(
    ulong LogicalBytes,
    ulong AllocatedBytes,
    ulong CompressionSavingsBytes,
    double CompressionSavingsPercent,
    ulong SparseSavingsBytes,
    ulong AllocationOverheadBytes,
    ulong? UnaccountedUsedBytes,
    ulong? ReservedBytes,
    ulong? SystemManagedUnavailableBytes,
    bool IsPartial)
{
    public static PhysicalStorageAccounting Calculate(ScanResultManaged result,
        VolumeStorageInfo? volume = null, bool isPartial = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        ulong logical = 0, allocated = 0, compressedLogical = 0, compressionSavings = 0,
            sparseSavings = 0;
        bool missingMetadata = false;
        for (int index = 0; index < result.Nodes.Length; index++)
        {
            if ((result.Nodes[index].Flags & ScanNodeFlags.Directory) != 0) continue;
            ScanNodeMetadata? metadata = result.GetMetadata((uint)index);
            missingMetadata |= metadata is null;
            if (metadata is not null &&
                (metadata.Flags & ScanNodeMetadataFlags.UniqueAllocation) == 0) continue;
            ulong itemLogical = metadata?.LogicalBytes ?? result.Nodes[index].Size;
            ulong itemAllocated = metadata?.UniquelyAccountedBytes ?? result.Nodes[index].Size;
            logical = SaturatingAdd(logical, itemLogical);
            allocated = SaturatingAdd(allocated, itemAllocated);
            ulong saved = itemLogical > itemAllocated ? itemLogical - itemAllocated : 0;
            if ((metadata?.Flags & ScanNodeMetadataFlags.Compressed) != 0)
            {
                compressedLogical = SaturatingAdd(compressedLogical, itemLogical);
                compressionSavings = SaturatingAdd(compressionSavings, saved);
            }
            else if ((metadata?.Flags & ScanNodeMetadataFlags.Sparse) != 0)
                sparseSavings = SaturatingAdd(sparseSavings, saved);
        }
        ulong overhead = allocated > logical ? allocated - logical : 0;
        ulong? unaccounted = volume is null ? null : volume.UsedBytes > allocated
            ? volume.UsedBytes - allocated : 0;
        return new(logical, allocated, compressionSavings,
            compressedLogical == 0 ? 0 : compressionSavings * 100d / compressedLogical,
            sparseSavings, overhead, unaccounted, volume?.ReservedBytes, volume?.SystemManagedUnavailableBytes,
            isPartial || missingMetadata);
    }

    static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}

public static class AllocationOverheadColors
{
    // Seven diverging buckets: compressed/sparse (blue), neutral, allocation overhead (orange/red).
    public static readonly uint[] Palette =
        [0xff2166acu, 0xff67a9cfu, 0xffd1e5f0u, 0xffbdbdbdu, 0xfffdd49eu, 0xfffc8d59u, 0xffb30000u];

    public static int Bucket(ulong logicalBytes, ulong allocatedBytes)
    {
        if (logicalBytes == allocatedBytes || (logicalBytes == 0 && allocatedBytes == 0)) return 3;
        double divergence = logicalBytes == 0 ? 1 : ((double)allocatedBytes - logicalBytes) / logicalBytes;
        return divergence switch
        {
            <= -0.50 => 0,
            <= -0.10 => 1,
            < 0 => 2,
            <= 0.10 => 4,
            <= 0.50 => 5,
            _ => 6,
        };
    }

    public static IReadOnlyList<TreemapLegendEntry> Legend() =>
    [
        new("≥50% saved", 0, 0, ColorArgb: Palette[0]), new("10–50% saved", 1, 0, ColorArgb: Palette[1]),
        new("<10% saved", 2, 0, ColorArgb: Palette[2]), new("logical = allocated", 3, 0, ColorArgb: Palette[3]),
        new("<10% overhead", 4, 0, ColorArgb: Palette[4]), new("10–50% overhead", 5, 0, ColorArgb: Palette[5]),
        new(">50% overhead", 6, 0, ColorArgb: Palette[6]),
    ];
}

internal static class PhysicalStorageMetadata
{
    internal static void RollUpDirectories(ScanResultManaged result)
    {
        if (result.Metadata.Length != result.Nodes.Length) return;
        var order = new List<uint>(result.Nodes.Length);
        var pending = new Stack<uint>();
        for (uint index = 0; index < result.Nodes.Length; index++)
            if (result.Nodes[index].Parent == uint.MaxValue) pending.Push(index);
        while (pending.TryPop(out uint index))
        {
            order.Add(index);
            for (uint child = result.Nodes[index].FirstChild;
                 child != uint.MaxValue && child < result.Nodes.Length;
                 child = result.Nodes[child].NextSibling) pending.Push(child);
        }
        for (int position = order.Count - 1; position >= 0; position--)
        {
            uint index = order[position];
            if ((result.Nodes[index].Flags & ScanNodeFlags.Directory) == 0) continue;
            ulong logical = 0, allocated = 0;
            for (uint child = result.Nodes[index].FirstChild;
                 child != uint.MaxValue && child < result.Nodes.Length;
                 child = result.Nodes[child].NextSibling)
            {
                ScanNodeMetadata? item = result.Metadata[child];
                logical = Add(logical, item?.LogicalBytes ?? result.Nodes[child].Size);
                allocated = Add(allocated, item?.UniquelyAccountedBytes ?? result.Nodes[child].Size);
            }
            ScanNodeMetadata previous = result.Metadata[index] ?? new(ScanNodeMetadataFlags.None,
                1, 0, 0, 0, 0, 0);
            result.Metadata[index] = previous with
            {
                Flags = previous.Flags | ScanNodeMetadataFlags.UniqueAllocation,
                LogicalBytes = logical,
                AllocatedBytes = allocated,
                UniquelyAccountedBytes = allocated,
            };
        }
    }

    static ulong Add(ulong left, ulong right) => ulong.MaxValue - left < right
        ? ulong.MaxValue : left + right;
}
