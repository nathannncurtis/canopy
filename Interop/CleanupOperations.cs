namespace SizeMonitor.Interop;

public enum CleanupMutationMode { Recycle, Recoverable, Permanent }
public enum CleanupTargetType { File, Directory }
public enum CleanupOutcomeKind { Recycled, MovedToAppRecovery, PermanentlyDeleted, Failed, Stale, Cancelled }

public sealed record CleanupIdentity(string Volume, string FileId);
public sealed record CleanupTargetSnapshot(string CanonicalPath, CleanupIdentity Identity,
    CleanupTargetType Type, ulong Size, DateTimeOffset LastWriteUtc, bool IsReparsePoint,
    string? ContentIdentity = null, uint LinkCount = 1);
public sealed record CleanupPlanItem(CleanupTargetSnapshot Snapshot, ulong EstimatedReclaimedBytes,
    IReadOnlyList<CleanupTargetSnapshot> Members);
public sealed record CleanupPlan(Guid Id, DateTimeOffset CreatedUtc, CleanupMutationMode Disposition,
    bool Recursive, IReadOnlyList<CleanupPlanItem> Items, ulong EstimatedReclaimedBytes,
    string RequiredConfirmation);
public sealed record CleanupItemOutcome(string Path, CleanupOutcomeKind Outcome, ulong ReclaimedBytes,
    string? Error = null, string? UndoToken = null);
public sealed record CleanupExecutionReport(Guid PlanId, IReadOnlyList<CleanupItemOutcome> Items,
    ulong EstimatedReclaimedBytes, ulong MeasuredReclaimedBytes, string? UndoToken);
public sealed record CleanupUndoOutcome(string Path, bool Restored, string? Error = null);
public sealed record CleanupUndoReport(IReadOnlyList<CleanupUndoOutcome> Items)
{ public bool Succeeded => Items.Count > 0 && Items.All(item => item.Restored); }

public interface ICleanupFileSystem
{
    CleanupTargetSnapshot Capture(string path);
    IEnumerable<CleanupTargetSnapshot> EnumerateTree(string directory) => [];
}

public interface ICleanupMutationBackend
{
    ValueTask<CleanupMutationResult> MutateAsync(CleanupTargetSnapshot target,
        CleanupMutationMode disposition, bool recursive, CancellationToken cancellationToken);
    ValueTask<bool> UndoAsync(string token, CancellationToken cancellationToken);
}

public sealed record CleanupMutationResult(bool Succeeded, ulong ReclaimedBytes,
    string? Error = null, string? VerifiableUndoToken = null);

public sealed class CleanupOperationService(ICleanupFileSystem fileSystem, ICleanupMutationBackend backend)
{
    public CleanupPlan Preview(IEnumerable<string> paths, CleanupMutationMode disposition = CleanupMutationMode.Recycle,
        bool recursive = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        CleanupTargetSnapshot[] snapshots = paths.Select(fileSystem.Capture).ToArray();
        if (snapshots.Length == 0) throw new ArgumentException("At least one target is required.", nameof(paths));
        for (int i = 0; i < snapshots.Length; i++) for (int j = i + 1; j < snapshots.Length; j++)
            if (IsDescendant(snapshots[i].CanonicalPath, snapshots[j].CanonicalPath) || IsDescendant(snapshots[j].CanonicalPath, snapshots[i].CanonicalPath))
                throw new InvalidOperationException("Cleanup targets cannot overlap (a directory and its descendant were both selected).");
        foreach (CleanupTargetSnapshot target in snapshots)
        {
            CleanupSafetyAssessment safety = CleanupSafetyPolicy.Evaluate(target.CanonicalPath, target.Size, ulong.MaxValue);
            if (safety.IsBlocked) throw new InvalidOperationException(string.Join(" ", safety.Warnings));
            if (target.Type == CleanupTargetType.Directory && target.IsReparsePoint && recursive)
                throw new InvalidOperationException("Recursive cleanup never follows reparse-point directories.");
        }

        var memberSets = snapshots.Select(snapshot => (Snapshot: snapshot, Members: (recursive && snapshot.Type == CleanupTargetType.Directory
            ? new[] { snapshot }.Concat(fileSystem.EnumerateTree(snapshot.CanonicalPath)) : [snapshot]).OrderBy(x => x.CanonicalPath, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        var expanded = memberSets.SelectMany(x => x.Members).ToArray();
        var selectedCounts = expanded.GroupBy(x => x.Identity).ToDictionary(x => x.Key, x => (uint)x.Count());
        var identities = new HashSet<CleanupIdentity>();
        ulong estimated = 0;
        CleanupPlanItem[] items = memberSets.Select(entry =>
        {
            ulong bytes = 0;
            foreach (CleanupTargetSnapshot member in entry.Members)
                if (identities.Add(member.Identity) && selectedCounts[member.Identity] >= member.LinkCount) bytes = SaturatingAdd(bytes, member.Size);
            estimated = SaturatingAdd(estimated, bytes);
            return new CleanupPlanItem(entry.Snapshot, bytes, entry.Members);
        }).ToArray();
        string verb = disposition switch { CleanupMutationMode.Permanent => "PERMANENTLY DELETE", CleanupMutationMode.Recoverable => "MOVE TO RECOVERY", _ => "RECYCLE" };
        string confirmation = recursive ? $"{verb} {items.Length} ITEMS RECURSIVELY" : $"{verb} {items.Length} ITEMS";
        return new(Guid.NewGuid(), DateTimeOffset.UtcNow, disposition, recursive, items, estimated, confirmation);
    }

    public async Task<CleanupExecutionReport> ExecuteAsync(CleanupPlan plan, string typedConfirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(typedConfirmation, plan.RequiredConfirmation, StringComparison.Ordinal))
            throw new InvalidOperationException($"Type exactly: {plan.RequiredConfirmation}");
        var outcomes = new List<CleanupItemOutcome>(plan.Items.Count);
        var undoTokens = new HashSet<string>(StringComparer.Ordinal);
        ulong measured = 0;
        foreach (CleanupPlanItem item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            { outcomes.Add(new(item.Snapshot.CanonicalPath, CleanupOutcomeKind.Cancelled, 0)); continue; }
            CleanupTargetSnapshot current;
            try { current = fileSystem.Capture(item.Snapshot.CanonicalPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { outcomes.Add(new(item.Snapshot.CanonicalPath, CleanupOutcomeKind.Failed, 0, ex.Message)); continue; }
            if (current != item.Snapshot)
            { outcomes.Add(new(item.Snapshot.CanonicalPath, CleanupOutcomeKind.Stale, 0, "Target identity or metadata changed after preview.")); continue; }
            if (plan.Recursive && current.Type == CleanupTargetType.Directory)
            {
                CleanupTargetSnapshot[] members = new[] { current }.Concat(fileSystem.EnumerateTree(current.CanonicalPath)).OrderBy(x => x.CanonicalPath, StringComparer.OrdinalIgnoreCase).ToArray();
                if (!members.SequenceEqual(item.Members))
                { outcomes.Add(new(item.Snapshot.CanonicalPath, CleanupOutcomeKind.Stale, 0, "Directory contents changed after preview.")); continue; }
            }
            try
            {
                CleanupMutationResult result = await backend.MutateAsync(current, plan.Disposition, plan.Recursive, cancellationToken);
                if (!result.Succeeded) { outcomes.Add(new(current.CanonicalPath, CleanupOutcomeKind.Failed, 0, result.Error)); continue; }
                measured = SaturatingAdd(measured, result.ReclaimedBytes);
                if (result.VerifiableUndoToken is not null) undoTokens.Add(result.VerifiableUndoToken);
                CleanupOutcomeKind kind = plan.Disposition switch { CleanupMutationMode.Recycle => CleanupOutcomeKind.Recycled, CleanupMutationMode.Recoverable => CleanupOutcomeKind.MovedToAppRecovery, _ => CleanupOutcomeKind.PermanentlyDeleted };
                outcomes.Add(new(current.CanonicalPath, kind, result.ReclaimedBytes, UndoToken: result.VerifiableUndoToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { outcomes.Add(new(current.CanonicalPath, CleanupOutcomeKind.Cancelled, 0)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { outcomes.Add(new(current.CanonicalPath, CleanupOutcomeKind.Failed, 0, ex.Message)); }
        }
        string? undo = plan.Disposition == CleanupMutationMode.Recoverable && undoTokens.Count == 1
            ? undoTokens.Single() : null;
        return new(plan.Id, outcomes, plan.EstimatedReclaimedBytes, measured, undo);
    }

    public ValueTask<bool> UndoAsync(CleanupExecutionReport report, CancellationToken token = default) =>
        report.UndoToken is null ? ValueTask.FromResult(false) : backend.UndoAsync(report.UndoToken, token);

    public async Task<CleanupUndoReport> UndoAllAsync(CleanupExecutionReport report, CancellationToken token = default)
    {
        var results = new List<CleanupUndoOutcome>();
        foreach (CleanupItemOutcome item in report.Items.Reverse().Where(item => item.UndoToken is not null))
        {
            try { results.Add(new(item.Path, await backend.UndoAsync(item.UndoToken!, token))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { results.Add(new(item.Path, false, ex.Message)); }
        }
        return new(results);
    }

    static ulong SaturatingAdd(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    static bool IsDescendant(string candidate, string parent) => Path.GetFullPath(candidate).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
