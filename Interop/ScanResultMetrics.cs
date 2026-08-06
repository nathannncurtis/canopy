namespace SizeMonitor.Interop;

public readonly record struct ScanNodeMetrics(
    ulong FileCount,
    ulong DirectoryCount,
    ulong AverageFileSize,
    uint DescendantDepth,
    double PercentageOfParent,
    double PercentageOfScan);

public sealed class ScanResultMetrics
{
    readonly ScanNodeMetrics[] _nodes;

    ScanResultMetrics(ScanNodeMetrics[] nodes) => _nodes = nodes;

    public int Count => _nodes.Length;

    public ScanNodeMetrics this[uint nodeIndex] =>
        nodeIndex < _nodes.Length
            ? _nodes[nodeIndex]
            : throw new ArgumentOutOfRangeException(nameof(nodeIndex));

    public static ScanResultMetrics Calculate(ScanResultManaged result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Nodes.Length != result.Names.Length)
            throw new InvalidDataException("Node and name arrays must have equal lengths.");

        int count = result.Nodes.Length;
        var files = new ulong[count];
        var directories = new ulong[count];
        var depths = new uint[count];
        for (int i = 0; i < count; i++)
        {
            bool isDirectory = (result.Nodes[i].Flags & ScanNodeFlags.Directory) != 0;
            files[i] = isDirectory ? 0ul : 1ul;
            directories[i] = isDirectory ? 1ul : 0ul;
            uint parent = result.Nodes[i].Parent;
            if (parent != uint.MaxValue && parent >= i)
                throw new InvalidDataException($"Node {i} has an invalid parent index {parent}.");
        }

        for (int i = count - 1; i > 0; i--)
        {
            uint parent = result.Nodes[i].Parent;
            if (parent == uint.MaxValue) continue;
            files[parent] = AddSaturating(files[parent], files[i]);
            directories[parent] = AddSaturating(directories[parent], directories[i]);
            uint childDepth = depths[i] == uint.MaxValue ? uint.MaxValue : depths[i] + 1;
            depths[parent] = Math.Max(depths[parent], childDepth);
        }

        var metrics = new ScanNodeMetrics[count];
        for (int i = 0; i < count; i++)
        {
            ScanNode node = result.Nodes[i];
            double parentPercentage = node.Parent == uint.MaxValue
                ? Percentage(node.Size, result.TotalBytes)
                : Percentage(node.Size, result.Nodes[node.Parent].Size);
            metrics[i] = new(
                files[i],
                directories[i],
                files[i] == 0 ? 0 : node.Size / files[i],
                depths[i],
                parentPercentage,
                Percentage(node.Size, result.TotalBytes));
        }
        return new ScanResultMetrics(metrics);
    }

    static ulong AddSaturating(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    static double Percentage(ulong value, ulong total) =>
        total == 0 ? 0d : 100d * value / total;
}
