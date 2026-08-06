namespace SizeMonitor.Interop;

public enum ScanChangeKind
{
    Added,
    Removed,
    Resized,
    Moved,
}

public sealed record ScanItemChange(ScanChangeKind Kind, string Path, string? PreviousPath,
    ulong PreviousSize, ulong CurrentSize, uint PreviousNodeIndex, uint CurrentNodeIndex)
{
    public long SizeDelta => ScanResultComparison.SaturatingDelta(CurrentSize, PreviousSize);
}

public sealed record DirectoryGrowth(string Path, ulong PreviousSize, ulong CurrentSize,
    uint PreviousNodeIndex, uint CurrentNodeIndex)
{
    public long SizeDelta => ScanResultComparison.SaturatingDelta(CurrentSize, PreviousSize);
}

public sealed class ScanComparisonResult
{
    internal ScanComparisonResult(IReadOnlyList<ScanItemChange> changes,
        IReadOnlyList<DirectoryGrowth> directoryGrowth, IReadOnlyList<string> ambiguousPaths)
    {
        Changes = changes;
        DirectoryGrowth = directoryGrowth;
        AmbiguousPaths = ambiguousPaths;
    }

    public IReadOnlyList<ScanItemChange> Changes { get; }
    public IReadOnlyList<DirectoryGrowth> DirectoryGrowth { get; }
    public IReadOnlyList<string> AmbiguousPaths { get; }

    public IReadOnlyList<DirectoryGrowth> GetFastestGrowingFolders(int maximum = 10)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        return DirectoryGrowth.Where(x => x.SizeDelta > 0)
            .OrderByDescending(x => x.SizeDelta).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maximum).ToArray();
    }
}

/// <summary>Compares two immutable scan results by normalized, case-insensitive paths.</summary>
public static class ScanResultComparison
{
    const uint MissingNode = uint.MaxValue;
    static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static ScanComparisonResult Compare(ScanResultManaged previous, ScanResultManaged current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var oldIndex = new ScanNavigationIndex(previous);
        var newIndex = new ScanNavigationIndex(current);
        Dictionary<string, Entry> oldEntries = Build(previous, oldIndex, cancellationToken, out string[] oldAmbiguous);
        Dictionary<string, Entry> newEntries = Build(current, newIndex, cancellationToken, out string[] newAmbiguous);
        var changes = new List<ScanItemChange>();
        var removed = new List<Entry>();
        var added = new List<Entry>();

        foreach ((string path, Entry oldEntry) in oldEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!newEntries.TryGetValue(path, out Entry newEntry))
                removed.Add(oldEntry);
            else if (oldEntry.IsDirectory != newEntry.IsDirectory)
            {
                removed.Add(oldEntry);
                added.Add(newEntry);
            }
            else if (!oldEntry.IsDirectory && oldEntry.Size != newEntry.Size)
                changes.Add(new(ScanChangeKind.Resized, newEntry.Path, oldEntry.Path,
                    oldEntry.Size, newEntry.Size, oldEntry.Index, newEntry.Index));
        }
        foreach ((string path, Entry newEntry) in newEntries)
            if (!oldEntries.ContainsKey(path)) added.Add(newEntry);

        // A move is inferred only for files when name, size, and relevant type flags form a
        // fingerprint that occurs exactly once among both unmatched sets.
        var oldFingerprints = removed.Where(x => !x.IsDirectory).GroupBy(FingerprintOf).ToDictionary(x => x.Key, x => x.ToArray());
        var newFingerprints = added.Where(x => !x.IsDirectory).GroupBy(FingerprintOf).ToDictionary(x => x.Key, x => x.ToArray());
        var movedOld = new HashSet<uint>();
        var movedNew = new HashSet<uint>();
        foreach ((Fingerprint fingerprint, Entry[] oldMatches) in oldFingerprints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (oldMatches.Length != 1 || !newFingerprints.TryGetValue(fingerprint, out Entry[]? newMatches) || newMatches.Length != 1)
                continue;
            Entry from = oldMatches[0], to = newMatches[0];
            movedOld.Add(from.Index); movedNew.Add(to.Index);
            changes.Add(new(ScanChangeKind.Moved, to.Path, from.Path, from.Size, to.Size, from.Index, to.Index));
        }
        changes.AddRange(removed.Where(x => !movedOld.Contains(x.Index)).Select(x =>
            new ScanItemChange(ScanChangeKind.Removed, x.Path, x.Path, x.Size, 0, x.Index, MissingNode)));
        changes.AddRange(added.Where(x => !movedNew.Contains(x.Index)).Select(x =>
            new ScanItemChange(ScanChangeKind.Added, x.Path, null, 0, x.Size, MissingNode, x.Index)));

        var growth = BuildDirectoryGrowth(oldEntries, newEntries, cancellationToken);
        string[] ambiguous = oldAmbiguous.Concat(newAmbiguous).Distinct(PathComparer)
            .OrderBy(path => path, PathComparer).ToArray();
        return new(changes.OrderBy(x => x.Path, PathComparer).ThenBy(x => x.Kind).ToArray(),
            growth, ambiguous);
    }

    static IReadOnlyList<DirectoryGrowth> BuildDirectoryGrowth(Dictionary<string, Entry> previous,
        Dictionary<string, Entry> current, CancellationToken token)
    {
        var paths = previous.Values.Where(x => x.IsDirectory).Select(x => x.Path)
            .Concat(current.Values.Where(x => x.IsDirectory).Select(x => x.Path))
            .Distinct(PathComparer).OrderBy(x => x, PathComparer);
        var growth = new List<DirectoryGrowth>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            bool hadOld = previous.TryGetValue(path, out Entry oldEntry) && oldEntry.IsDirectory;
            bool hasNew = current.TryGetValue(path, out Entry newEntry) && newEntry.IsDirectory;
            ulong oldSize = hadOld ? oldEntry.Size : 0;
            ulong newSize = hasNew ? newEntry.Size : 0;
            if (oldSize != newSize)
                growth.Add(new(path, oldSize, newSize, hadOld ? oldEntry.Index : MissingNode,
                    hasNew ? newEntry.Index : MissingNode));
        }
        return growth;
    }

    static Dictionary<string, Entry> Build(ScanResultManaged result, ScanNavigationIndex index,
        CancellationToken token, out string[] ambiguousPaths)
    {
        var entries = new Dictionary<string, Entry>(PathComparer);
        var ambiguous = new HashSet<string>(PathComparer);
        for (uint i = 0; i < result.Nodes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[i];
            string path = index.GetPath(i);
            if (ambiguous.Contains(path)) continue;
            if (!entries.TryAdd(path, new(i, path, result.Names[i], node.Size,
                    (node.Flags & ScanNodeFlags.Directory) != 0, node.Flags)))
            {
                entries.Remove(path);
                ambiguous.Add(path);
            }
        }
        ambiguousPaths = ambiguous.OrderBy(path => path, PathComparer).ToArray();
        return entries;
    }

    static Fingerprint FingerprintOf(Entry entry) => new(entry.Name, entry.Size,
        entry.Flags & (ScanNodeFlags.Symlink | ScanNodeFlags.Reparse));

    public static long SaturatingDelta(ulong current, ulong previous)
    {
        if (current >= previous) return current - previous > long.MaxValue ? long.MaxValue : (long)(current - previous);
        return previous - current > (ulong)long.MaxValue ? long.MinValue : -(long)(previous - current);
    }

    readonly record struct Entry(uint Index, string Path, string Name, ulong Size, bool IsDirectory, uint Flags);
    readonly record struct Fingerprint(string Name, ulong Size, uint TypeFlags)
    {
        public bool Equals(Fingerprint other) => Size == other.Size && TypeFlags == other.TypeFlags &&
            StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);
        public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name), Size, TypeFlags);
    }
}
