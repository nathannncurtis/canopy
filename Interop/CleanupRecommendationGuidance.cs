using System.Globalization;

namespace SizeMonitor.Interop;

public sealed record CleanupRecommendationExplanation(
    uint NodeIndex,
    string Title,
    string Summary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Limitations,
    CleanupRisk Risk,
    ulong EstimatedReclaimableBytes);

/// <summary>Explains preview-only cleanup recommendations without authorizing an action.</summary>
public static class CleanupRecommendationExplainer
{
    public static CleanupRecommendationExplanation Explain(
        CleanupPreviewItem item,
        bool includeSensitivePath = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        string identity = includeSensitivePath ? Sanitize(item.Path) : $"scan item #{item.NodeIndex}";
        string risk = item.Risk switch
        {
            CleanupRisk.Low => "low-risk candidate",
            CleanupRisk.Medium => "candidate requiring review",
            _ => "high-risk candidate requiring careful review",
        };
        string summary = $"{identity} is a {risk}. The scan estimates {Bytes(item.ReclaimableBytes)} reclaimable.";
        string[] reasons = item.Reasons.Count == 0
            ? ["No rule reason was supplied; do not act without independent review."]
            : item.Reasons.Select(reason => Sanitize(reason)).ToArray();
        string ruleList = item.RuleIds.Count == 0 ? "none" : string.Join(", ", item.RuleIds.Select(Sanitize));
        reasons = [.. reasons, $"Matched declarative rule IDs: {ruleList}."];

        return new(item.NodeIndex, $"Cleanup recommendation for {identity}", summary, reasons,
        [
            "This is a non-destructive preview; no file has been deleted or modified.",
            "The estimate comes from a scan snapshot and may be stale if the filesystem changed.",
            "Rule matches indicate configured predicates, not proof that data is unneeded.",
            "Review ownership, backups, retention requirements, links, and active applications before any future action.",
        ], item.Risk, item.ReclaimableBytes);
    }

    static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? bytes.ToString("N0", CultureInfo.InvariantCulture) + " B"
            : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>Provides deterministic largest-first navigation through cleanup preview findings.</summary>
public sealed class CleanupFindingNavigator
{
    readonly CleanupPreviewItem[] _findings;
    int _position = -1;

    public CleanupFindingNavigator(CleanupPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _findings = preview.Items
            .OrderByDescending(item => item.ReclaimableBytes)
            .ThenByDescending(item => item.Risk)
            .ThenBy(item => item.NodeIndex)
            .ToArray();
    }

    public int Count => _findings.Length;
    public int RevealedCount => _position + 1;
    public int RemainingCount => Math.Max(0, Count - RevealedCount);
    public CleanupPreviewItem? Current => _position >= 0 && _position < Count ? _findings[_position] : null;
    public bool CanMovePrevious => _position > 0;
    public bool CanRevealNext => _position + 1 < Count;

    public CleanupPreviewItem? RevealNextLargest()
    {
        if (!CanRevealNext) return null;
        return _findings[++_position];
    }

    public CleanupPreviewItem? MovePrevious()
    {
        if (!CanMovePrevious) return Current;
        return _findings[--_position];
    }

    public CleanupPreviewItem? MoveNext() => RevealNextLargest();

    public void Reset() => _position = -1;
}
