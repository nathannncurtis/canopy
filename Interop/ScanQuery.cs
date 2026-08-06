namespace SizeMonitor.Interop;

[Flags]
public enum ScanItemKinds
{
    None = 0,
    Files = 1,
    Directories = 2,
    All = Files | Directories,
}

public enum ScanSortField
{
    Name,
    Path,
    Size,
    Depth,
    PercentOfParent,
    PercentOfTotal,
}

public sealed record ScanSortTerm(ScanSortField Field, bool Descending = false);

public sealed record ScanQuery
{
    public string? Text { get; init; }
    public string? RegexPattern { get; init; }
    public IReadOnlyCollection<string> Extensions { get; init; } = [];
    public ulong? MinimumSize { get; init; }
    public ulong? MaximumSize { get; init; }
    public ScanItemKinds Kinds { get; init; } = ScanItemKinds.All;
    public uint RequiredFlags { get; init; }
    public uint ExcludedFlags { get; init; }
    public IReadOnlyList<ScanSortTerm> Sort { get; init; } =
        [new(ScanSortField.Size, Descending: true)];
    /// <summary>
    /// Bounds peak materialization by retaining the first matching nodes in traversal
    /// order, then applying <see cref="Sort"/> within that retained set. This is not a
    /// global top-N operation; use <see cref="ScanResultQuery.SearchPage"/> to observe
    /// whether additional matches were clipped.
    /// </summary>
    public int? ResultLimit { get; init; }
}

public sealed class ScanQueryRegexException : FormatException
{
    public ScanQueryRegexException(string pattern, Exception innerException)
        : base($"The regular expression is invalid: {innerException.Message}", innerException) =>
        Pattern = pattern;

    public string Pattern { get; }
}

public sealed record ScanSearchResult(
    uint NodeIndex,
    string Name,
    string RelativePath,
    ulong Size,
    int Depth,
    double PercentOfParent,
    double PercentOfTotal,
    uint Flags);

public sealed record ScanSearchPage(
    IReadOnlyList<ScanSearchResult> Items,
    int TotalMatches)
{
    public bool IsTruncated => Items.Count < TotalMatches;
}
