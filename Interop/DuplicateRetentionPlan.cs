namespace SizeMonitor.Interop;

public sealed record DuplicateRetentionGroup(
    string Sha256,
    string RetainedPath,
    IReadOnlyList<string> RemovablePaths,
    ulong ReclaimableBytes);

public sealed record DuplicateRetentionPlan(IReadOnlyList<DuplicateRetentionGroup> Groups)
{
    public ulong ReclaimableBytes => Groups.Aggregate(0UL, static (total, group) =>
        ulong.MaxValue - total < group.ReclaimableBytes ? ulong.MaxValue : total + group.ReclaimableBytes);
}

/// <summary>Builds a non-destructive plan with exactly one explicit survivor per duplicate group.</summary>
public static class DuplicateRetentionPlanner
{
    public static DuplicateRetentionPlan Create(IReadOnlyList<DuplicateFileGroup> groups,
        IReadOnlyDictionary<string, string>? retainedByHash = null)
    {
        ArgumentNullException.ThrowIfNull(groups);
        retainedByHash ??= new Dictionary<string, string>();
        var plans = new List<DuplicateRetentionGroup>(groups.Count);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (DuplicateFileGroup group in groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            if (group.Paths.Count < 2) throw new InvalidDataException("A duplicate group must contain at least two paths.");
            if (!seenHashes.Add(group.Sha256)) throw new InvalidDataException("Duplicate hashes must identify one group each.");
            string[] paths = group.Paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length < 2) throw new InvalidDataException("A duplicate group must contain at least two distinct paths.");
            string retained = retainedByHash.TryGetValue(group.Sha256, out string? selected)
                ? paths.FirstOrDefault(path => path.Equals(selected, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("The retained path must belong to its duplicate group.", nameof(retainedByHash))
                : paths.Order(StringComparer.OrdinalIgnoreCase).First();
            string[] removable = paths.Where(path => !path.Equals(retained, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            ulong reclaimable = group.Size <= 0 ? 0 : MultiplySaturating((ulong)group.Size, (ulong)removable.Length);
            plans.Add(new(group.Sha256, retained, removable, reclaimable));
        }
        return new(plans);
    }

    static ulong MultiplySaturating(ulong left, ulong right) =>
        left != 0 && right > ulong.MaxValue / left ? ulong.MaxValue : left * right;
}
