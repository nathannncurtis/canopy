namespace SizeMonitor.Interop;

public enum ConsumerCleanupCategory
{
    TemporaryFile,
    OldLogFile,
    CrashDump,
    BrowserOrThumbnailCache,
    WindowsUpdateDownload,
    RecycleBinContent,
}

public enum CleanupDisposition { SafeToClean, ReviewRequired, UseSystemTool }

public sealed record ConsumerCleanupEvidence(uint? NodeIndex, string Path, bool IsDirectory,
    ulong Size = 0, DateTimeOffset? LastWriteTime = null);

public sealed record ConsumerCleanupOptions
{
    public TimeSpan TemporaryFileAge { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan OldLogAge { get; init; } = TimeSpan.FromDays(30);
}

public sealed record ConsumerCleanupFinding(uint? NodeIndex, ConsumerCleanupCategory Category,
    string Path, ulong Size, string Rationale, CleanupRisk Risk,
    CleanupDisposition Disposition, bool IsAggregationRoot);

public sealed record ConsumerCleanupPreview(IReadOnlyList<ConsumerCleanupFinding> Findings,
    IReadOnlyDictionary<ConsumerCleanupCategory, ulong> CategoryBytes, ulong TotalBytes,
    int SafeToCleanCount, int ReviewRequiredCount, int UseSystemToolCount)
{
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Read-only classification of bounded scan evidence; it exposes no deletion operation.</summary>
public static class ConsumerCleanupClassifier
{
    static readonly HashSet<string> DumpExtensions = new(StringComparer.OrdinalIgnoreCase) { ".dmp", ".mdmp", ".hdmp" };

    public static ConsumerCleanupPreview Classify(IEnumerable<ConsumerCleanupEvidence> evidence,
        IEnumerable<string> allowedRoots, DateTimeOffset now, ConsumerCleanupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence); ArgumentNullException.ThrowIfNull(allowedRoots);
        options ??= new(); Validate(options);
        string[] roots = allowedRoots.Select(Normalize).Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) throw new ArgumentException("At least one bounded root is required.", nameof(allowedRoots));
        var findings = new List<ConsumerCleanupFinding>();
        foreach (ConsumerCleanupEvidence item in evidence)
        {
            cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(item);
            string path = Normalize(item.Path);
            if (!roots.Any(root => IsWithin(path, root)))
                throw new InvalidDataException($"Cleanup evidence is outside the bounded roots: {path}");
            string[] parts = Components(path);
            string leaf = parts.Length == 0 ? string.Empty : parts[^1];
            ConsumerCleanupFinding? finding =
                WindowsUpdate(item, path, parts) ?? RecycleBin(item, path, parts) ??
                CrashDump(item, path, parts, leaf) ?? BrowserCache(item, path, parts, leaf) ??
                Temporary(item, path, parts, leaf, now, options) ?? OldLog(item, path, parts, leaf, now, options);
            if (finding is not null) findings.Add(finding);
        }
        MarkAggregationRoots(findings);
        var totals = new Dictionary<ConsumerCleanupCategory, ulong>(); ulong total = 0;
        foreach (ConsumerCleanupFinding item in findings)
        {
            bool nestedInSameCategory = findings.Any(parent => parent.Category == item.Category &&
                parent.Path.Length < item.Path.Length && IsWithin(item.Path, parent.Path));
            if (!nestedInSameCategory)
            {
                totals.TryGetValue(item.Category, out ulong category); totals[item.Category] = Add(category, item.Size);
            }
            if (item.IsAggregationRoot) total = Add(total, item.Size);
        }
        return new(findings.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(), totals, total,
            findings.Count(item => item.Disposition == CleanupDisposition.SafeToClean),
            findings.Count(item => item.Disposition == CleanupDisposition.ReviewRequired),
            findings.Count(item => item.Disposition == CleanupDisposition.UseSystemTool));
    }

    public static ConsumerCleanupPreview ClassifyScanResult(ScanResultManaged result, DateTimeOffset now,
        ConsumerCleanupOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var navigation = new ScanNavigationIndex(result);
        var evidence = new ConsumerCleanupEvidence[result.Nodes.Length];
        var roots = new List<string>();
        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index]; string path = navigation.GetPath(index);
            evidence[index] = new(index, path, (node.Flags & ScanNodeFlags.Directory) != 0, node.Size);
            if (node.Parent == uint.MaxValue) roots.Add(path);
        }
        ConsumerCleanupPreview preview = Classify(evidence, roots, now, options, cancellationToken);
        return preview with
        {
            Limitations = ["Scan results do not contain file timestamps. Age-gated generic temporary-file and old-log rules are excluded; explicit .tmp files and path-based categories remain available."],
        };
    }

    static ConsumerCleanupFinding? Temporary(ConsumerCleanupEvidence item, string path, string[] parts,
        string leaf, DateTimeOffset now, ConsumerCleanupOptions options)
    {
        bool inTemp = HasSequence(parts, "appdata", "local", "temp") || HasSequence(parts, "windows", "temp") ||
            parts.Contains("temp", StringComparer.OrdinalIgnoreCase) || parts.Contains("tmp", StringComparer.OrdinalIgnoreCase);
        if (!inTemp || item.IsDirectory || (!leaf.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
            !OldEnough(item.LastWriteTime, now, options.TemporaryFileAge))) return null;
        return Finding(item, path, ConsumerCleanupCategory.TemporaryFile,
            "File is inside a recognized temporary directory and is either explicitly temporary or old enough.",
            CleanupRisk.Low, CleanupDisposition.SafeToClean);
    }

    static ConsumerCleanupFinding? OldLog(ConsumerCleanupEvidence item, string path, string[] parts,
        string leaf, DateTimeOffset now, ConsumerCleanupOptions options)
    {
        if (item.IsDirectory || !leaf.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            !parts.Any(part => part.Equals("logs", StringComparison.OrdinalIgnoreCase) || part.Equals("log", StringComparison.OrdinalIgnoreCase)) ||
            !OldEnough(item.LastWriteTime, now, options.OldLogAge)) return null;
        return Finding(item, path, ConsumerCleanupCategory.OldLogFile,
            $"Log is inside a log directory and is at least {options.OldLogAge.TotalDays:0} days old.",
            CleanupRisk.Medium, CleanupDisposition.ReviewRequired);
    }

    static ConsumerCleanupFinding? CrashDump(ConsumerCleanupEvidence item, string path, string[] parts, string leaf)
    {
        if (item.IsDirectory || !DumpExtensions.Contains(Path.GetExtension(leaf)) ||
            !(parts.Contains("crashdumps", StringComparer.OrdinalIgnoreCase) || parts.Contains("minidump", StringComparer.OrdinalIgnoreCase) ||
              HasSequence(parts, "wer", "reportarchive") || HasSequence(parts, "wer", "reportqueue"))) return null;
        return Finding(item, path, ConsumerCleanupCategory.CrashDump,
            "File is a crash dump in a Windows or application crash-report directory.", CleanupRisk.Low,
            CleanupDisposition.SafeToClean);
    }

    static ConsumerCleanupFinding? BrowserCache(ConsumerCleanupEvidence item, string path, string[] parts, string leaf)
    {
        bool browser = HasSequence(parts, "chrome", "user data") && ContainsCache(parts) ||
            HasSequence(parts, "edge", "user data") && ContainsCache(parts) ||
            parts.Contains("cache2", StringComparer.OrdinalIgnoreCase) && parts.Contains("profiles", StringComparer.OrdinalIgnoreCase);
        bool thumbnails = leaf.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
            leaf.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && HasSequence(parts, "microsoft", "windows", "explorer");
        if (!browser && !thumbnails) return null;
        return Finding(item, path, ConsumerCleanupCategory.BrowserOrThumbnailCache,
            thumbnails ? "File is a Windows Explorer thumbnail cache database." : "Path is inside a recognized browser cache.",
            CleanupRisk.Low, CleanupDisposition.SafeToClean);
    }

    static ConsumerCleanupFinding? WindowsUpdate(ConsumerCleanupEvidence item, string path, string[] parts) =>
        HasSequence(parts, "windows", "softwaredistribution", "download")
            ? Finding(item, path, ConsumerCleanupCategory.WindowsUpdateDownload,
                "Path is inside Windows Update's download cache; use Windows cleanup tooling while update services are idle.",
                CleanupRisk.High, CleanupDisposition.UseSystemTool) : null;

    static ConsumerCleanupFinding? RecycleBin(ConsumerCleanupEvidence item, string path, string[] parts) =>
        parts.Contains("$recycle.bin", StringComparer.OrdinalIgnoreCase)
            ? Finding(item, path, ConsumerCleanupCategory.RecycleBinContent,
                "Path is inside this volume's Recycle Bin; empty it through the Windows shell to preserve semantics.",
                CleanupRisk.Medium, CleanupDisposition.UseSystemTool) : null;

    static ConsumerCleanupFinding Finding(ConsumerCleanupEvidence item, string path,
        ConsumerCleanupCategory category, string rationale, CleanupRisk risk, CleanupDisposition disposition) =>
        new(item.NodeIndex, category, path, item.Size, rationale, risk, disposition, true);

    static void MarkAggregationRoots(List<ConsumerCleanupFinding> findings)
    {
        findings.Sort((left, right) => left.Path.Length.CompareTo(right.Path.Length));
        for (int index = 0; index < findings.Count; index++)
        {
            ConsumerCleanupFinding item = findings[index];
            bool nested = findings.Take(index).Any(parent => IsWithin(item.Path, parent.Path) &&
                !string.Equals(item.Path, parent.Path, StringComparison.OrdinalIgnoreCase));
            findings[index] = item with { IsAggregationRoot = !nested };
        }
    }

    static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string value = path.Replace('/', '\\');
        if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) value = "\\\\" + value[8..];
        else if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (!Path.IsPathFullyQualified(value) || value.Length >= 2 && value[1] == ':' &&
            (value.Length < 3 || value[2] != '\\'))
            throw new ArgumentException("Cleanup paths must be fully qualified drive or UNC paths.", nameof(path));
        string canonical = Path.GetFullPath(value);
        string? root = Path.GetPathRoot(canonical);
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Cleanup path has no filesystem root.", nameof(path));
        return string.Equals(canonical, root, StringComparison.OrdinalIgnoreCase)
            ? root : Path.TrimEndingDirectorySeparator(canonical);
    }
    static string[] Components(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        (Path.EndsInDirectorySeparator(root)
            ? path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            : path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '\\');
    static bool HasSequence(string[] parts, params string[] sequence)
    {
        for (int start = 0; start <= parts.Length - sequence.Length; start++)
            if (sequence.Select((value, offset) => parts[start + offset].Equals(value, StringComparison.OrdinalIgnoreCase)).All(match => match)) return true;
        return false;
    }
    static bool ContainsCache(string[] parts) => parts.Any(part => part.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("code cache", StringComparison.OrdinalIgnoreCase) || part.Equals("gpucache", StringComparison.OrdinalIgnoreCase));
    static bool OldEnough(DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan age) => timestamp is { } value && value <= now - age;
    static ulong Add(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    static void Validate(ConsumerCleanupOptions options)
    {
        if (options.TemporaryFileAge < TimeSpan.Zero || options.OldLogAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Cleanup ages cannot be negative.");
    }
}
