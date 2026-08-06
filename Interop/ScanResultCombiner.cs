namespace SizeMonitor.Interop;

/// <summary>Builds one navigable tree from independently scanned targets.</summary>
public static class ScanResultCombiner
{
    const uint NoNode = uint.MaxValue;

    public static ScanResultManaged CombineTargets(
        IEnumerable<TargetScanResult> targets,
        string rootName = "Combined scan")
    {
        ArgumentNullException.ThrowIfNull(targets);
        TargetScanResult[] targetArray = targets.ToArray();
        ScanResultManaged combined = Combine(targetArray.Select(target => target.Result), rootName);

        int destinationOffset = 1;
        foreach (TargetScanResult target in targetArray)
        {
            string targetPath = Path.GetFullPath(target.Path);
            for (int sourceIndex = 0; sourceIndex < target.Result.Nodes.Length; sourceIndex++)
            {
                if (target.Result.Nodes[sourceIndex].Parent != NoNode) continue;
                int destinationIndex = checked(destinationOffset + sourceIndex);
                combined.Names[destinationIndex] = targetPath;
                ScanNode node = combined.Nodes[destinationIndex];
                node.NameLen = checked((uint)targetPath.Length);
                combined.Nodes[destinationIndex] = node;
            }
            destinationOffset = checked(destinationOffset + target.Result.Nodes.Length);
        }
        return combined;
    }

    public static ScanResultManaged Combine(
        IEnumerable<ScanResultManaged> results,
        string rootName = "Combined scan")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootName);
        ScanResultManaged[] sources = results.ToArray();

        long totalNodeCount = 1;
        foreach (var source in sources)
        {
            if (source.Nodes.Length != source.Names.Length)
                throw new ArgumentException("Every result must have one name per node.", nameof(results));
            totalNodeCount = checked(totalNodeCount + source.Nodes.Length);
        }
        if (totalNodeCount > int.MaxValue)
            throw new ArgumentException("The combined result is too large.", nameof(results));

        var nodes = new ScanNode[(int)totalNodeCount];
        var names = new string[(int)totalNodeCount];
        ulong totalBytes = SumChecked(sources, result => result.TotalBytes);
        nodes[0] = new ScanNode
        {
            Size = totalBytes,
            Parent = NoNode,
            FirstChild = NoNode,
            NextSibling = NoNode,
            Flags = ScanNodeFlags.Directory,
            NameLen = checked((uint)rootName.Length),
        };
        names[0] = rootName;

        var roots = new List<uint>(sources.Length);
        int destinationOffset = 1;
        foreach (var source in sources)
        {
            uint offset = checked((uint)destinationOffset);
            int sourceRootCount = 0;
            for (int sourceIndex = 0; sourceIndex < source.Nodes.Length; sourceIndex++)
            {
                ScanNode node = source.Nodes[sourceIndex];
                bool isRoot = node.Parent == NoNode;
                ValidateIndex(node.Parent, source.Nodes.Length, nameof(ScanNode.Parent));
                ValidateIndex(node.FirstChild, source.Nodes.Length, nameof(ScanNode.FirstChild));
                ValidateIndex(node.NextSibling, source.Nodes.Length, nameof(ScanNode.NextSibling));
                if (isRoot) sourceRootCount++;
                node.Parent = isRoot ? 0u : AddOffset(node.Parent, offset);
                node.FirstChild = AddOffset(node.FirstChild, offset);
                node.NextSibling = AddOffset(node.NextSibling, offset);

                uint destinationIndex = checked(offset + (uint)sourceIndex);
                nodes[destinationIndex] = node;
                names[destinationIndex] = source.Names[sourceIndex];
                if (isRoot) roots.Add(destinationIndex);
            }
            if (source.Nodes.Length > 0 && sourceRootCount == 0)
                throw new ArgumentException("Every non-empty result must contain a root node.", nameof(results));
            destinationOffset += source.Nodes.Length;
        }

        if (roots.Count > 0)
        {
            nodes[0].FirstChild = roots[0];
            for (int i = 0; i < roots.Count; i++)
            {
                uint rootIndex = roots[i];
                ScanNode root = nodes[rootIndex];
                root.NextSibling = i + 1 < roots.Count ? roots[i + 1] : NoNode;
                nodes[rootIndex] = root;
            }
        }

        return new ScanResultManaged
        {
            Nodes = nodes,
            Names = names,
            TotalBytes = totalBytes,
            FileCount = SumChecked(sources, result => result.FileCount),
            DirCount = checked(SumChecked(sources, result => result.DirCount) + 1),
            ElapsedSec = sources.Length == 0 ? 0 : sources.Max(result => result.ElapsedSec),
        };
    }

    static uint AddOffset(uint index, uint offset) =>
        index == NoNode ? NoNode : checked(index + offset);

    static void ValidateIndex(uint index, int nodeCount, string field)
    {
        if (index != NoNode && index >= (uint)nodeCount)
            throw new ArgumentException($"{field} contains an out-of-range node index.");
    }

    static ulong SumChecked(
        IEnumerable<ScanResultManaged> sources,
        Func<ScanResultManaged, ulong> selector)
    {
        ulong total = 0;
        foreach (var source in sources)
            total = checked(total + selector(source));
        return total;
    }
}
