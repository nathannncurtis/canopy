namespace SizeMonitor.Interop;

public enum PersonalStorageCategory
{
    VirtualMachineDisk,
    GameLibraryContent,
    MessagingAttachmentCache,
    OldDownload,
    OldDeviceBackup,
    OrphanedApplicationData,
}

public sealed record PersonalStorageEvidence(uint? NodeIndex, string Path, bool IsDirectory,
    ulong Size = 0, DateTimeOffset? LastWriteTime = null);

public sealed record PersonalStorageContext
{
    public required IReadOnlyList<string> AllowedRoots { get; init; }
    public IReadOnlySet<string> InstalledApplicationIdentifiers { get; init; } = new HashSet<string>();
    public bool InstalledApplicationInventoryComplete { get; init; }
}

public sealed record PersonalStorageOptions
{
    public TimeSpan OldDownloadAge { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan OldDeviceBackupAge { get; init; } = TimeSpan.FromDays(180);
    public TimeSpan OrphanedApplicationDataAge { get; init; } = TimeSpan.FromDays(180);
    public ulong LargeAttachmentMinimumBytes { get; init; } = 100UL * 1024 * 1024;
}

public sealed record PersonalStorageFinding(uint? NodeIndex, PersonalStorageCategory Category,
    string Family, string Path, ulong Size, string Rationale, CleanupRisk Risk,
    CleanupDisposition Disposition, bool IsAggregationRoot);

public sealed record PersonalStoragePreview(IReadOnlyList<PersonalStorageFinding> Findings,
    IReadOnlyDictionary<PersonalStorageCategory, ulong> CategoryBytes, ulong TotalBytes)
{
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Produces preview evidence only. It deliberately exposes no filesystem mutation methods.</summary>
public static class PersonalStorageClassifier
{
    static readonly HashSet<string> VirtualDiskExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow", ".qcow2" };

    public static PersonalStoragePreview Classify(IEnumerable<PersonalStorageEvidence> evidence,
        PersonalStorageContext context, DateTimeOffset now, PersonalStorageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence); ArgumentNullException.ThrowIfNull(context);
        options ??= new(); Validate(options);
        string[] roots = context.AllowedRoots.Select(Canonicalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) throw new ArgumentException("At least one bounded root is required.", nameof(context));
        var installed = context.InstalledApplicationIdentifiers.Select(NormalizeIdentifier)
            .Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var findings = new List<PersonalStorageFinding>();
        foreach (PersonalStorageEvidence item in evidence)
        {
            cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(item);
            string path = Canonicalize(item.Path);
            if (!roots.Any(root => IsWithin(path, root)))
                throw new InvalidDataException($"Personal-storage evidence is outside the bounded roots: {path}");
            string[] parts = Components(path); string leaf = parts[^1];
            PersonalStorageFinding? finding = VirtualDisk(item, path, parts, leaf) ??
                GameLibrary(item, path, parts) ?? AttachmentCache(item, path, parts, options) ??
                DeviceBackup(item, path, parts, now, options) ?? OldDownload(item, path, parts, now, options) ??
                OrphanedAppData(item, path, parts, installed, context.InstalledApplicationInventoryComplete, now, options);
            if (finding is not null) findings.Add(finding);
        }
        MarkAggregationRoots(findings);
        var totals = new Dictionary<PersonalStorageCategory, ulong>(); ulong total = 0;
        foreach (PersonalStorageFinding item in findings)
        {
            bool sameCategoryAncestor = findings.Any(parent => parent.Category == item.Category &&
                parent.Path.Length < item.Path.Length && IsWithin(item.Path, parent.Path));
            if (!sameCategoryAncestor)
            {
                totals.TryGetValue(item.Category, out ulong existing); totals[item.Category] = Add(existing, item.Size);
            }
            if (item.IsAggregationRoot) total = Add(total, item.Size);
        }
        return new(findings.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(), totals, total);
    }

    public static PersonalStoragePreview ClassifyScanResult(ScanResultManaged result, DateTimeOffset now,
        PersonalStorageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var navigation = new ScanNavigationIndex(result); var evidence = new List<PersonalStorageEvidence>();
        var roots = new List<string>();
        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); ScanNode node = result.Nodes[index];
            string path = navigation.GetPath(index);
            evidence.Add(new(index, path, (node.Flags & ScanNodeFlags.Directory) != 0, node.Size));
            if (node.Parent == uint.MaxValue) roots.Add(path);
        }
        PersonalStoragePreview preview = Classify(evidence,
            new PersonalStorageContext { AllowedRoots = roots, InstalledApplicationInventoryComplete = false },
            now, options, cancellationToken);
        return preview with { Limitations =
        [
            "Scan results do not contain timestamps, so old-download and old-backup rules require a metadata-enriched scan.",
            "Installed-application inventory was not supplied, so orphaned application-data detection is disabled.",
        ] };
    }

    static PersonalStorageFinding? VirtualDisk(PersonalStorageEvidence item, string path, string[] parts, string leaf)
    {
        if (item.IsDirectory || !VirtualDiskExtensions.Contains(Path.GetExtension(leaf))) return null;
        string? family = parts.Any(part => part.Equals("VirtualBox VMs", StringComparison.OrdinalIgnoreCase)) ? "VirtualBox" :
            parts.Any(part => part.Equals("Virtual Machines", StringComparison.OrdinalIgnoreCase)) ? "Hyper-V" :
            parts.Any(part => part.Equals("VMware", StringComparison.OrdinalIgnoreCase)) ? "VMware" :
            parts.Any(part => part.Equals("qemu", StringComparison.OrdinalIgnoreCase)) ? "QEMU" : "Virtual disk";
        return Finding(item, path, PersonalStorageCategory.VirtualMachineDisk, family,
            $"{Path.GetExtension(leaf)} is a virtual-machine disk image; verify the VM is retired before cleanup.",
            CleanupRisk.High, CleanupDisposition.ReviewRequired);
    }

    static PersonalStorageFinding? GameLibrary(PersonalStorageEvidence item, string path, string[] parts)
    {
        string? family = HasSequence(parts, "steamapps", "common") ? "Steam" :
            HasSequence(parts, "Epic Games") ? "Epic Games" : HasSequence(parts, "XboxGames") ? "Xbox" :
            HasSequence(parts, "GOG Galaxy", "Games") ? "GOG" : null;
        return family is null ? null : Finding(item, path, PersonalStorageCategory.GameLibraryContent, family,
            $"Path is managed {family} game-library content; uninstall or move it through the launcher.",
            CleanupRisk.High, CleanupDisposition.UseSystemTool);
    }

    static PersonalStorageFinding? AttachmentCache(PersonalStorageEvidence item, string path, string[] parts,
        PersonalStorageOptions options)
    {
        if (item.IsDirectory || item.Size < options.LargeAttachmentMinimumBytes) return null;
        string? family = parts.Contains("Content.Outlook", StringComparer.OrdinalIgnoreCase) ? "Outlook" :
            HasSequence(parts, "Microsoft Teams", "Cache") ? "Teams" :
            HasSequence(parts, "Slack", "Cache") ? "Slack" : HasSequence(parts, "discord", "Cache") ? "Discord" : null;
        return family is null ? null : Finding(item, path, PersonalStorageCategory.MessagingAttachmentCache, family,
            $"File is inside the {family} attachment/cache location and is at least {options.LargeAttachmentMinimumBytes:N0} bytes.", CleanupRisk.Medium,
            CleanupDisposition.ReviewRequired);
    }

    static PersonalStorageFinding? OldDownload(PersonalStorageEvidence item, string path, string[] parts,
        DateTimeOffset now, PersonalStorageOptions options)
    {
        if (item.IsDirectory || !parts.Contains("Downloads", StringComparer.OrdinalIgnoreCase) ||
            !OldEnough(item.LastWriteTime, now, options.OldDownloadAge)) return null;
        return Finding(item, path, PersonalStorageCategory.OldDownload, "Downloads",
            $"File is in Downloads and is at least {options.OldDownloadAge.TotalDays:0} days old.",
            CleanupRisk.Medium, CleanupDisposition.ReviewRequired);
    }

    static PersonalStorageFinding? DeviceBackup(PersonalStorageEvidence item, string path, string[] parts,
        DateTimeOffset now, PersonalStorageOptions options)
    {
        string? family = HasSequence(parts, "Apple Computer", "MobileSync", "Backup") ? "Apple MobileSync" :
            HasSequence(parts, "Android", "backup") ? "Android" : null;
        if (family is null || !OldEnough(item.LastWriteTime, now, options.OldDeviceBackupAge)) return null;
        return Finding(item, path, PersonalStorageCategory.OldDeviceBackup, family,
            $"Device backup is at least {options.OldDeviceBackupAge.TotalDays:0} days old.",
            CleanupRisk.High, CleanupDisposition.ReviewRequired);
    }

    static PersonalStorageFinding? OrphanedAppData(PersonalStorageEvidence item, string path, string[] parts,
        HashSet<string> installed, bool inventoryComplete, DateTimeOffset now, PersonalStorageOptions options)
    {
        if (!inventoryComplete || !item.IsDirectory || !OldEnough(item.LastWriteTime, now, options.OrphanedApplicationDataAge)) return null;
        int marker = FindSequenceEnd(parts, "AppData", "Local");
        if (marker < 0) marker = FindSequenceEnd(parts, "AppData", "Roaming");
        if (marker < 0 && parts.Length >= 2 && parts[0].EndsWith(":", StringComparison.Ordinal) &&
            parts[1].Equals("ProgramData", StringComparison.OrdinalIgnoreCase)) marker = 2;
        if (marker < 0 || marker != parts.Length - 1) return null;
        string identifier = NormalizeIdentifier(parts[marker]);
        if (identifier.Length == 0 || installed.Contains(identifier)) return null;
        return Finding(item, path, PersonalStorageCategory.OrphanedApplicationData, identifier,
            "Top-level application-data directory is old and does not match the supplied complete installed-application inventory.",
            CleanupRisk.High, CleanupDisposition.ReviewRequired);
    }

    static PersonalStorageFinding Finding(PersonalStorageEvidence item, string path, PersonalStorageCategory category,
        string family, string rationale, CleanupRisk risk, CleanupDisposition disposition) =>
        new(item.NodeIndex, category, family, path, item.Size, rationale, risk, disposition, true);

    static void MarkAggregationRoots(List<PersonalStorageFinding> findings)
    {
        findings.Sort((left, right) => left.Path.Length.CompareTo(right.Path.Length));
        for (int index = 0; index < findings.Count; index++)
        {
            PersonalStorageFinding item = findings[index];
            bool nested = findings.Take(index).Any(parent => parent.Path.Length < item.Path.Length && IsWithin(item.Path, parent.Path));
            findings[index] = item with { IsAggregationRoot = !nested };
        }
    }

    static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); string value = path.Replace('/', '\\');
        if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) value = "\\\\" + value[8..];
        else if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (!Path.IsPathFullyQualified(value) || value.Length >= 2 && value[1] == ':' && (value.Length < 3 || value[2] != '\\'))
            throw new ArgumentException("Personal-storage paths must be fully qualified drive or UNC paths.", nameof(path));
        string canonical = Path.GetFullPath(value); string root = Path.GetPathRoot(canonical)!;
        return canonical.Equals(root, StringComparison.OrdinalIgnoreCase) ? root : Path.TrimEndingDirectorySeparator(canonical);
    }
    static string[] Components(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        (Path.EndsInDirectorySeparator(root) ? path.StartsWith(root, StringComparison.OrdinalIgnoreCase) :
        path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '\\');
    static bool HasSequence(string[] parts, params string[] sequence) => FindSequenceEnd(parts, sequence) >= 0;
    static int FindSequenceEnd(string[] parts, params string[] sequence)
    {
        for (int start = 0; start <= parts.Length - sequence.Length; start++)
            if (sequence.Select((value, offset) => parts[start + offset].Equals(value, StringComparison.OrdinalIgnoreCase)).All(match => match))
                return start + sequence.Length;
        return -1;
    }
    static string NormalizeIdentifier(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());
    static bool OldEnough(DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan age) => timestamp is { } value && value <= now - age;
    static ulong Add(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    static void Validate(PersonalStorageOptions options)
    {
        if (options.OldDownloadAge < TimeSpan.Zero || options.OldDeviceBackupAge < TimeSpan.Zero ||
            options.OrphanedApplicationDataAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Personal-storage ages cannot be negative.");
    }
}
