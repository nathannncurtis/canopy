namespace SizeMonitor.Interop;

public readonly record struct StorageDistributionBucket(
    string Key,
    ulong FileCount,
    ulong Bytes,
    double Percentage);

public static class StorageDistribution
{
    public const string NoExtension = "(no extension)";

    static readonly IReadOnlyDictionary<string, string> Categories = BuildCategories();

    public static IReadOnlyList<StorageDistributionBucket> ByExtension(
        ScanResultManaged result,
        CancellationToken cancellationToken = default) =>
        Aggregate(result, static name => NormalizeExtension(name), cancellationToken);

    public static IReadOnlyList<StorageDistributionBucket> ByCategory(
        ScanResultManaged result,
        CancellationToken cancellationToken = default) =>
        Aggregate(result, static name => Categorize(NormalizeExtension(name)), cancellationToken);

    static IReadOnlyList<StorageDistributionBucket> Aggregate(
        ScanResultManaged result,
        Func<string, string> keySelector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Nodes.Length != result.Names.Length)
            throw new ArgumentException("The result must have one name per node.", nameof(result));

        var aggregates = new Dictionary<string, AggregateValue>(StringComparer.Ordinal);
        ulong totalBytes = 0;
        for (int index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            if ((node.Flags & ScanNodeFlags.Directory) != 0) continue;

            string name = result.Names[index]
                ?? throw new InvalidDataException("The scan result contains a null node name.");
            string key = keySelector(name);
            aggregates.TryGetValue(key, out AggregateValue value);
            aggregates[key] = new AggregateValue(
                SaturatingAdd(value.Count, 1),
                SaturatingAdd(value.Bytes, node.Size));
            totalBytes = SaturatingAdd(totalBytes, node.Size);
        }

        return aggregates
            .Select(pair => new StorageDistributionBucket(
                pair.Key,
                pair.Value.Count,
                pair.Value.Bytes,
                totalBytes == 0 ? 0 : 100d * pair.Value.Bytes / totalBytes))
            .OrderByDescending(bucket => bucket.Bytes)
            .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
            .ToArray();
    }

    static string NormalizeExtension(string name)
    {
        string extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) || extension == "."
            ? NoExtension
            : extension.ToLowerInvariant();
    }

    static string Categorize(string extension) =>
        extension == NoExtension
            ? "other"
            : Categories.TryGetValue(extension, out string? category) ? category : "other";

    static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    static IReadOnlyDictionary<string, string> BuildCategories()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(result, "documents", ".txt", ".rtf", ".pdf", ".doc", ".docx", ".odt", ".xls",
            ".xlsx", ".ods", ".ppt", ".pptx", ".odp", ".csv", ".epub", ".md");
        Add(result, "images", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff",
            ".webp", ".svg", ".ico", ".heic", ".raw");
        Add(result, "video", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v",
            ".mpeg", ".mpg");
        Add(result, "audio", ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma",
            ".opus");
        Add(result, "archives", ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz",
            ".cab", ".iso");
        Add(result, "code", ".cs", ".cpp", ".c", ".h", ".hpp", ".java", ".js", ".ts",
            ".py", ".rs", ".go", ".rb", ".php", ".html", ".css", ".xaml", ".xml", ".json",
            ".yml", ".yaml", ".sql", ".sh", ".ps1");
        Add(result, "apps", ".exe", ".msi", ".appx", ".msix", ".apk", ".app", ".com");
        Add(result, "system", ".dll", ".sys", ".drv", ".ini", ".cfg", ".reg", ".dat", ".log");
        return result;
    }

    static void Add(Dictionary<string, string> target, string category, params string[] extensions)
    {
        foreach (string extension in extensions)
            target.Add(extension, category);
    }

    readonly record struct AggregateValue(ulong Count, ulong Bytes);
}
