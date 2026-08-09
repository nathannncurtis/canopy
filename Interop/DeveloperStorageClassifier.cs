namespace SizeMonitor.Interop;

public enum DeveloperStorageCategory
{
    OldInstaller,
    PackageManagerCache,
    IdeOrCompilerCache,
    StaleBuildOutput,
    NodeModules,
    PythonEnvironmentOrCache,
    LanguagePackageCache,
    ContainerStorage,
}

public sealed record StoragePathEvidence(
    string Path,
    bool IsDirectory,
    ulong Size = 0,
    DateTimeOffset? LastWriteTime = null);

public sealed record DeveloperStorageOptions
{
    public TimeSpan OldInstallerAge { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan StaleBuildOutputAge { get; init; } = TimeSpan.FromDays(30);
}

public sealed record DeveloperStorageFinding(
    DeveloperStorageCategory Category,
    string Family,
    string Path,
    ulong Size,
    string Evidence,
    CleanupRisk Risk,
    bool IsAggregationRoot);

public sealed record DeveloperStoragePreview(
    IReadOnlyList<DeveloperStorageFinding> Findings,
    IReadOnlyDictionary<DeveloperStorageCategory, ulong> CategoryBytes,
    ulong TotalBytes);

/// <summary>
/// Classifies paths that are commonly safe cleanup candidates. Classification is
/// deliberately read-only: callers must preview and validate a finding before deletion.
/// </summary>
public static class DeveloperStorageClassifier
{
    static readonly HashSet<string> InstallerExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };
    static readonly HashSet<string> BuildDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "target", "build", "dist", "out" };

    public static DeveloperStoragePreview Classify(
        IEnumerable<StoragePathEvidence> items,
        DateTimeOffset now,
        DeveloperStorageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        options ??= new DeveloperStorageOptions();
        ValidateOptions(options);
        var findings = new List<DeveloperStorageFinding>();

        foreach (StoragePathEvidence item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (string.IsNullOrWhiteSpace(item.Path))
                throw new ArgumentException("Candidate paths cannot be empty.", nameof(items));

            string path = Normalize(item.Path);
            string[] parts = Components(path);
            string leaf = parts.Length == 0 ? string.Empty : parts[^1];
            DeveloperStorageFinding? finding =
                OldInstaller(item, path, leaf, now, options) ??
                ContainerStorage(item, path, parts) ??
                NodeModules(item, path, parts) ??
                PythonStorage(item, path, parts) ??
                LanguageCache(item, path, parts) ??
                PackageCache(item, path, parts) ??
                IdeCache(item, path, parts) ??
                BuildOutput(item, path, leaf, now, options);
            if (finding is not null) findings.Add(finding);
        }

        findings.Sort((left, right) =>
        {
            int length = left.Path.Length.CompareTo(right.Path.Length);
            return length != 0 ? length : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        });
        for (int index = 0; index < findings.Count; index++)
        {
            DeveloperStorageFinding finding = findings[index];
            bool nested = findings.Take(index).Any(parent => IsDescendant(finding.Path, parent.Path));
            findings[index] = finding with { IsAggregationRoot = !nested };
        }

        var totals = new Dictionary<DeveloperStorageCategory, ulong>();
        ulong total = 0;
        foreach (DeveloperStorageFinding finding in findings.Where(item => item.IsAggregationRoot))
        {
            totals.TryGetValue(finding.Category, out ulong categoryTotal);
            totals[finding.Category] = AddSaturating(categoryTotal, finding.Size);
            total = AddSaturating(total, finding.Size);
        }
        return new(findings, totals, total);
    }

    static DeveloperStorageFinding? OldInstaller(StoragePathEvidence item, string path, string leaf,
        DateTimeOffset now, DeveloperStorageOptions options)
    {
        if (item.IsDirectory || !InstallerExtensions.Contains(Path.GetExtension(leaf)) ||
            !OldEnough(item.LastWriteTime, now, options.OldInstallerAge)) return null;
        return Finding(DeveloperStorageCategory.OldInstaller, "Windows installer", item, path,
            $"Installer package is at least {options.OldInstallerAge.TotalDays:0} days old.", CleanupRisk.Medium);
    }

    static DeveloperStorageFinding? PackageCache(StoragePathEvidence item, string path, string[] parts)
    {
        string? manager = MatchSequence(parts, ["npm-cache"], ["yarn", "cache"], ["pnpm", "store"],
            ["pip", "cache"], ["chocolatey", "cache"], ["scoop", "cache"]);
        return manager is null ? null : Finding(DeveloperStorageCategory.PackageManagerCache, manager, item, path,
            $"Path is inside the {manager} package cache.", CleanupRisk.Low);
    }

    static DeveloperStorageFinding? IdeCache(StoragePathEvidence item, string path, string[] parts)
    {
        string? marker = MatchSequence(parts, [".vs"], [".idea", "system"], ["visualstudio", "cache"],
            ["vscode", "cacheddata"], ["microsoft", "vscode-cpptools"]);
        return marker is null ? null : Finding(DeveloperStorageCategory.IdeOrCompilerCache, marker, item, path,
            $"Path is inside the {marker} IDE or compiler cache.", CleanupRisk.Low);
    }

    static DeveloperStorageFinding? BuildOutput(StoragePathEvidence item, string path, string leaf,
        DateTimeOffset now, DeveloperStorageOptions options)
    {
        if (!item.IsDirectory || !BuildDirectories.Contains(leaf) ||
            !OldEnough(item.LastWriteTime, now, options.StaleBuildOutputAge)) return null;
        return Finding(DeveloperStorageCategory.StaleBuildOutput, leaf, item, path,
            $"Build-output directory is at least {options.StaleBuildOutputAge.TotalDays:0} days old.", CleanupRisk.Medium);
    }

    static DeveloperStorageFinding? NodeModules(StoragePathEvidence item, string path, string[] parts) =>
        Contains(parts, "node_modules")
            ? Finding(DeveloperStorageCategory.NodeModules, "npm/node_modules", item, path,
                "Path is inside a node_modules dependency tree.", CleanupRisk.Medium)
            : null;

    static DeveloperStorageFinding? PythonStorage(StoragePathEvidence item, string path, string[] parts)
    {
        string? marker = MatchSequence(parts, ["__pycache__"], [".venv"], ["venv"], [".tox"], [".pytest_cache"], [".mypy_cache"]);
        return marker is null ? null : Finding(DeveloperStorageCategory.PythonEnvironmentOrCache, marker, item, path,
            $"Path is inside Python-generated storage ({marker}).", CleanupRisk.Medium);
    }

    static DeveloperStorageFinding? LanguageCache(StoragePathEvidence item, string path, string[] parts)
    {
        string? marker = MatchSequence(parts, [".cargo", "registry"], [".cargo", "git"], [".nuget", "packages"],
            [".m2", "repository"], [".gradle", "caches"], ["go", "pkg", "mod"]);
        return marker is null ? null : Finding(DeveloperStorageCategory.LanguagePackageCache, marker, item, path,
            $"Path is inside a language package cache ({marker}).", CleanupRisk.Low);
    }

    static DeveloperStorageFinding? ContainerStorage(StoragePathEvidence item, string path, string[] parts)
    {
        string? marker = MatchSequence(parts, ["docker", "windowsfilter"], ["docker", "overlay2"],
            ["docker", "buildkit"], ["containers", "storage"], ["containerd", "root"]);
        return marker is null ? null : Finding(DeveloperStorageCategory.ContainerStorage, marker, item, path,
            $"Path is managed container storage ({marker}); clean it through the container runtime.", CleanupRisk.High);
    }

    static DeveloperStorageFinding Finding(DeveloperStorageCategory category, string family,
        StoragePathEvidence item, string path, string evidence, CleanupRisk risk) =>
        new(category, family, path, item.Size, evidence, risk, true);

    static bool OldEnough(DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan age) =>
        timestamp is { } value && value <= now - age;

    static string Normalize(string path)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (normalized.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            normalized = "\\\\" + normalized[8..];
        else if (normalized.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        return normalized;
    }

    static string[] Components(string path) =>
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool Contains(string[] parts, string value) =>
        parts.Contains(value, StringComparer.OrdinalIgnoreCase);

    static bool IsDescendant(string path, string parent) =>
        path.Length > parent.Length && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
        path[parent.Length] == '\\';

    static ulong AddSaturating(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    static string? MatchSequence(string[] parts, params string[][] candidates)
    {
        foreach (string[] candidate in candidates)
        {
            for (int start = 0; start <= parts.Length - candidate.Length; start++)
            {
                bool match = true;
                for (int offset = 0; offset < candidate.Length; offset++)
                    match &= string.Equals(parts[start + offset], candidate[offset], StringComparison.OrdinalIgnoreCase);
                if (match) return string.Join("/", candidate);
            }
        }
        return null;
    }

    static void ValidateOptions(DeveloperStorageOptions options)
    {
        if (options.OldInstallerAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Installer age cannot be negative.");
        if (options.StaleBuildOutputAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Build-output age cannot be negative.");
    }
}
