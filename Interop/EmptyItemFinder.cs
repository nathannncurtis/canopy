namespace SizeMonitor.Interop;

public enum EmptyItemKind
{
    File,
    Directory,
}

public readonly record struct EmptyScanItem(
    uint NodeIndex,
    string RelativePath,
    EmptyItemKind Kind);

/// <summary>Finds empty files and directories in a completed scan result.</summary>
public static class EmptyItemFinder
{
    const uint NoNode = uint.MaxValue;

    /// <summary>
    /// Returns zero-byte files and directories which contain no files or directories.
    /// Results retain node-index order. Paths are relative to the scan's containing
    /// location and therefore include the scanned root's name.
    /// </summary>
    /// <remarks>
    /// A directory is empty only when it has no child nodes; a directory whose only
    /// descendants are zero-byte files or empty directories is not itself empty.
    /// </remarks>
    public static IReadOnlyList<EmptyScanItem> Find(
        ScanResultManaged result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        int count = result.Nodes.Length;
        if (result.Names.Length != count)
            throw new ArgumentException("The result must have one name per node.", nameof(result));

        var hasChildren = new bool[count];
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint parent = result.Nodes[index].Parent;
            if (parent != NoNode)
            {
                if (parent >= count)
                    throw new InvalidDataException("The scan result contains an invalid parent index.");
                hasChildren[parent] = true;
            }
        }

        ValidateParentTopology(result.Nodes, cancellationToken);

        var paths = new string?[count];
        var matches = new List<EmptyScanItem>();
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            bool isDirectory = (node.Flags & ScanNodeFlags.Directory) != 0;
            if ((!isDirectory && node.Size == 0) || (isDirectory && !hasChildren[index]))
            {
                matches.Add(new EmptyScanItem(
                    index,
                    ResolvePath(result, index, paths, cancellationToken),
                    isDirectory ? EmptyItemKind.Directory : EmptyItemKind.File));
            }
        }

        return matches;
    }

    static void ValidateParentTopology(ScanNode[] nodes, CancellationToken cancellationToken)
    {
        // 0 = unseen, 1 = on current parent chain, 2 = known acyclic.
        var states = new byte[nodes.Length];
        var chain = new List<uint>();
        for (uint start = 0; start < nodes.Length; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states[start] == 2) continue;

            chain.Clear();
            uint current = start;
            while (current != NoNode && states[current] != 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (states[current] == 1)
                    throw new InvalidDataException("The scan result contains a parent cycle.");
                states[current] = 1;
                chain.Add(current);
                current = nodes[current].Parent;
            }

            foreach (uint index in chain)
                states[index] = 2;
        }
    }

    static string ResolvePath(
        ScanResultManaged result,
        uint index,
        string?[] paths,
        CancellationToken cancellationToken)
    {
        if (paths[index] is not null) return paths[index]!;

        var chain = new List<uint>();
        uint current = index;
        while (current != NoNode && paths[current] is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chain.Add(current);
            current = result.Nodes[current].Parent;
        }

        string path = current == NoNode ? string.Empty : paths[current]!;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint nodeIndex = chain[i];
            string name = result.Names[nodeIndex]
                ?? throw new InvalidDataException("The scan result contains a null node name.");
            path = path.Length == 0 ? name : Path.Combine(path, name);
            paths[nodeIndex] = path;
        }

        return paths[index]!;
    }
}
