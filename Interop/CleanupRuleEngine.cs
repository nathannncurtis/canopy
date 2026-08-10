using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SizeMonitor.Interop;

public enum CleanupRisk { Low, Medium, High }
public enum CleanupItemKind { Files, Directories, All }

public sealed record CleanupRuleSet
{
    public int Version { get; init; } = 1;
    public required IReadOnlyList<CleanupRule> Rules { get; init; }
}

public sealed record CleanupRule
{
    public required string Id { get; init; }
    public required string Reason { get; init; }
    public CleanupRisk Risk { get; init; } = CleanupRisk.Medium;
    public CleanupItemKind Kind { get; init; } = CleanupItemKind.Files;
    public string? PathPattern { get; init; }
    public string? NamePattern { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public ulong? MinimumSize { get; init; }
    public ulong? MaximumSize { get; init; }
    public int? MinimumDepth { get; init; }
    public int? MaximumDepth { get; init; }
}

public sealed record CleanupPreviewItem(
    uint NodeIndex,
    string Path,
    ulong ReclaimableBytes,
    CleanupRisk Risk,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> Reasons,
    string EstimateBasis = "Logical size fallback");

public sealed record CleanupPreview(
    IReadOnlyList<CleanupPreviewItem> Items,
    ulong ReclaimableBytes,
    int LowRiskCount,
    int MediumRiskCount,
    int HighRiskCount,
    bool UsesPhysicalAllocation = false,
    bool IsPartialEstimate = true);
public sealed record CleanupRuleCoverage(string RuleId, int MatchCount, ulong EstimatedPotentialBytes);
public sealed record CleanupAuditReport(int Version, DateTimeOffset GeneratedUtc, CleanupPreview Preview,
    IReadOnlyList<CleanupRuleCoverage> Rules, IReadOnlyList<string> Warnings);

/// <summary>Evaluates declarative cleanup rules. This type never modifies the filesystem.</summary>
public static class CleanupRuleEngine
{
    public const int MaximumRuleFileBytes = 1024 * 1024;
    public const int MaximumRules = 256;
    const uint NoNode = uint.MaxValue;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static CleanupRuleSet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumRuleFileBytes)
            throw new InvalidDataException("Cleanup rule JSON exceeds the 1 MiB limit.");
        try
        {
            CleanupRuleSet rules = JsonSerializer.Deserialize<CleanupRuleSet>(json, JsonOptions)
                ?? throw new InvalidDataException("Cleanup rule JSON is empty.");
            ValidateRules(rules);
            return rules;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Cleanup rule JSON is invalid.", ex);
        }
    }

    public static CleanupPreview Preview(
        ScanResultManaged result,
        CleanupRuleSet ruleSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ValidateRules(ruleSet);
        (string[] paths, int[] depths) = BuildPaths(result, cancellationToken);
        CompiledRule[] rules = ruleSet.Rules.Select(rule => Compile(rule)).ToArray();
        var items = new List<CleanupPreviewItem>();
        ulong reclaimable = 0;
        int low = 0, medium = 0, high = 0;

        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            string name = result.Names[index];
            CompiledRule[] matched = rules.Where(rule => rule.Matches(node, name, paths[index], depths[index])).ToArray();
            if (matched.Length == 0) continue;
            CleanupRisk risk = matched.Max(rule => rule.Source.Risk);
            ScanNodeMetadata? metadata = result.GetMetadata(index);
            bool directory = (node.Flags & ScanNodeFlags.Directory) != 0;
            ulong estimate = metadata is null ? node.Size : directory ? metadata.AllocatedBytes : metadata.UniquelyAccountedBytes;
            items.Add(new(index, paths[index], estimate, risk,
                matched.Select(rule => rule.Source.Id).ToArray(),
                matched.Select(rule => rule.Source.Reason).Distinct(StringComparer.Ordinal).ToArray(),
                metadata is null ? "Logical-size fallback; physical allocation metadata unavailable" : directory ? "Unique descendant allocation" : "Uniquely accounted physical allocation"));
            if (risk == CleanupRisk.High) high++; else if (risk == CleanupRisk.Medium) medium++; else low++;
        }
        var matchedIndexes = items.Select(item => item.NodeIndex).ToHashSet();
        foreach (CleanupPreviewItem item in items)
        {
            uint parent = result.Nodes[item.NodeIndex].Parent; bool covered = false;
            while (parent != NoNode) { if (matchedIndexes.Contains(parent)) { covered = true; break; } parent = result.Nodes[parent].Parent; }
            if (!covered) reclaimable = AddSaturating(reclaimable, item.ReclaimableBytes);
        }
        bool physical = items.Count > 0 && items.All(item => result.GetMetadata(item.NodeIndex) is not null);
        bool partial = items.Any(item => result.GetMetadata(item.NodeIndex) is null);
        return new(items, reclaimable, low, medium, high, physical, partial);
    }

    static void ValidateRules(CleanupRuleSet set)
    {
        if (set.Version != 1) throw new InvalidDataException($"Unsupported cleanup rule version {set.Version}.");
        if (set.Rules is null || set.Rules.Count == 0 || set.Rules.Count > MaximumRules)
            throw new InvalidDataException($"Cleanup rules must contain 1 to {MaximumRules} entries.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupRule rule in set.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 128 || !ids.Add(rule.Id))
                throw new InvalidDataException("Cleanup rule IDs must be nonempty, unique, and at most 128 characters.");
            if (string.IsNullOrWhiteSpace(rule.Reason) || rule.Reason.Length > 512)
                throw new InvalidDataException($"Cleanup rule '{rule.Id}' requires a concise reason.");
            if (rule.MinimumSize > rule.MaximumSize || rule.MinimumDepth > rule.MaximumDepth ||
                rule.MinimumDepth < 0 || rule.MaximumDepth < 0)
                throw new InvalidDataException($"Cleanup rule '{rule.Id}' has an invalid range.");
            if (rule.Extensions.Count > 64 || rule.Extensions.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64))
                throw new InvalidDataException($"Cleanup rule '{rule.Id}' has invalid extensions.");
            if (rule.PathPattern?.Length > 512 || rule.NamePattern?.Length > 256)
                throw new InvalidDataException($"Cleanup rule '{rule.Id}' has an oversized pattern.");
            if (rule.PathPattern is string pathPattern && (Path.IsPathRooted(pathPattern) || pathPattern.Split(['/', '\\']).Any(part => part == "..")))
                throw new InvalidDataException($"Cleanup rule '{rule.Id}' path pattern must be scan-relative and cannot escape its root.");
            if (rule.PathPattern is null && rule.NamePattern is null && rule.Extensions.Count == 0 &&
                rule.MinimumSize is null && rule.MaximumSize is null && rule.MinimumDepth is null && rule.MaximumDepth is null)
                throw new InvalidDataException($"Cleanup rule '{rule.Id}' must contain at least one predicate.");
        }
    }

    public static CleanupAuditReport Audit(ScanResultManaged result, CleanupRuleSet rules, DateTimeOffset generatedUtc, CancellationToken token = default)
    {
        CleanupPreview preview = Preview(result, rules, token);
        CleanupRuleCoverage[] coverage = rules.Rules.Select(rule =>
        {
            CleanupPreviewItem[] matches = preview.Items.Where(item => item.RuleIds.Contains(rule.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
            ulong bytes = 0; foreach (CleanupPreviewItem item in matches) bytes = AddSaturating(bytes, item.ReclaimableBytes);
            return new CleanupRuleCoverage(rule.Id, matches.Length, bytes);
        }).OrderBy(item => item.RuleId, StringComparer.Ordinal).ToArray();
        string[] warnings = coverage.Where(item => item.MatchCount == 0).Select(item => $"Rule '{item.RuleId}' matched no items.")
            .Concat(preview.Items.Where(item => item.RuleIds.Count > 1).Select(item => $"'{item.Path}' matched conflicting/overlapping rules: {string.Join(", ", item.RuleIds)}."))
            .Concat(preview.Items.Where(item => result.Nodes[item.NodeIndex].Parent == NoNode).Select(item => $"'{item.Path}' is a scan root and must not be executed as a cleanup target."))
            .Concat(preview.Items.Select(item => { try { return CleanupSafetyPolicy.Evaluate(item.Path, item.ReclaimableBytes, ulong.MaxValue).IsBlocked ? $"'{item.Path}' is protected by cleanup safety policy." : null; } catch (ArgumentException) { return null; } }).OfType<string>())
            .Concat(preview.IsPartialEstimate ? ["Physical allocation metadata was unavailable for one or more matches; estimates are partial/fallback values."] : [])
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new(1, generatedUtc, preview, coverage, warnings);
    }

    static CompiledRule Compile(CleanupRule rule) => new(
        rule,
        Glob(rule.PathPattern),
        Glob(rule.NamePattern),
        rule.Extensions.Select(NormalizeExtension).ToHashSet(StringComparer.OrdinalIgnoreCase));

    static Regex? Glob(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        string expression = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
    }

    static string NormalizeExtension(string extension)
    {
        string value = extension.Trim();
        return value.StartsWith('.') ? value : "." + value;
    }

    static (string[] Paths, int[] Depths) BuildPaths(ScanResultManaged result, CancellationToken token)
    {
        if (result.Nodes is null || result.Names is null || result.Nodes.Length != result.Names.Length)
            throw new InvalidDataException("Node and name arrays must be non-null and have equal lengths.");
        int count = result.Nodes.Length;
        var paths = new string[count];
        var depths = new int[count];
        var states = new byte[count];
        for (int start = 0; start < count; start++)
        {
            token.ThrowIfCancellationRequested();
            if (result.Names[start] is null) throw new InvalidDataException($"Node {start} has a null name.");
            if (states[start] == 2) continue;
            var chain = new List<int>();
            uint current = (uint)start;
            while (current != NoNode)
            {
                if (current >= count) throw new InvalidDataException($"Node {start} has an invalid parent.");
                if (states[current] == 2) break;
                if (states[current] == 1) throw new InvalidDataException("The scan parent topology contains a cycle.");
                states[current] = 1; chain.Add((int)current); current = result.Nodes[current].Parent;
            }
            string path = current == NoNode ? string.Empty : paths[current];
            int depth = current == NoNode ? -1 : depths[current];
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                int index = chain[i];
                path = path.Length == 0 ? result.Names[index] : Path.Combine(path, result.Names[index]);
                paths[index] = path; depths[index] = ++depth; states[index] = 2;
            }
        }
        return (paths, depths);
    }

    static ulong AddSaturating(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    sealed record CompiledRule(CleanupRule Source, Regex? Path, Regex? Name, HashSet<string> Extensions)
    {
        public bool Matches(ScanNode node, string name, string path, int depth)
        {
            bool directory = (node.Flags & ScanNodeFlags.Directory) != 0;
            if (Source.Kind == CleanupItemKind.Files && directory || Source.Kind == CleanupItemKind.Directories && !directory) return false;
            if (Path is not null && !Path.IsMatch(path) || Name is not null && !Name.IsMatch(name)) return false;
            if (Extensions.Count > 0 && (directory || !Extensions.Contains(FileNameFacts.ExtensionOf(name)))) return false;
            return !(Source.MinimumSize is ulong min && node.Size < min) &&
                   !(Source.MaximumSize is ulong max && node.Size > max) &&
                   !(Source.MinimumDepth is int minDepth && depth < minDepth) &&
                   !(Source.MaximumDepth is int maxDepth && depth > maxDepth);
        }
    }
}
