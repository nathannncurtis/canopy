using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanResultQueryTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void FiltersByTextExtensionSizeAndKind()
    {
        var query = new ScanQuery
        {
            Text = "report",
            Extensions = ["txt"],
            MinimumSize = 50,
            MaximumSize = 70,
            Kinds = ScanItemKinds.Files,
        };

        ScanSearchResult match = Assert.Single(ScanResultQuery.Search(
            Result(), query, TestContext.Current.CancellationToken));

        Assert.Equal("report.txt", match.Name);
        Assert.Equal(Path.Combine("root", "docs", "report.txt"), match.RelativePath);
        Assert.Equal(2, match.Depth);
        Assert.Equal(100, match.PercentOfParent);
        Assert.Equal(60, match.PercentOfTotal);
    }

    [Fact]
    public void FiltersByRegexAndFlags()
    {
        var query = new ScanQuery
        {
            RegexPattern = "^docs$",
            Kinds = ScanItemKinds.Directories,
            RequiredFlags = ScanNodeFlags.Directory,
            ExcludedFlags = ScanNodeFlags.Reparse,
        };

        Assert.Equal("docs", Assert.Single(ScanResultQuery.Search(
            Result(), query, TestContext.Current.CancellationToken)).Name);
    }

    [Fact]
    public void SingleDotfilesFollowNoExtensionProductConvention()
    {
        var result = new ScanResultManaged
        {
            Nodes = [Node(1, None, None, None, 0), Node(1, None, None, None, 0)],
            Names = [".gitignore", ".config.json"],
            TotalBytes = 2,
        };

        Assert.Empty(ScanResultQuery.Search(result,
            new ScanQuery { Extensions = ["gitignore"] }, TestContext.Current.CancellationToken));
        Assert.Equal(".config.json", Assert.Single(ScanResultQuery.Search(result,
            new ScanQuery { Extensions = ["json"] }, TestContext.Current.CancellationToken)).Name);
    }

    [Fact]
    public void AppliesStableMultiColumnSort()
    {
        var query = new ScanQuery
        {
            Kinds = ScanItemKinds.Files,
            Sort =
            [
                new(ScanSortField.Size, Descending: true),
                new(ScanSortField.Name),
            ],
        };

        Assert.Equal(["report.txt", "cache.log"],
            ScanResultQuery.Search(Result(), query, TestContext.Current.CancellationToken)
                .Select(item => item.Name));
    }

    [Fact]
    public void RejectsInvalidSizeRange()
    {
        var query = new ScanQuery { MinimumSize = 2, MaximumSize = 1 };

        Assert.Throws<ArgumentException>(() => ScanResultQuery.Search(
            Result(), query, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReportsMalformedRegexDistinctly()
    {
        const string pattern = "[";
        ScanQueryRegexException exception = Assert.Throws<ScanQueryRegexException>(() =>
            ScanResultQuery.Search(Result(), new ScanQuery { RegexPattern = pattern },
                TestContext.Current.CancellationToken));

        Assert.Equal(pattern, exception.Pattern);
    }

    [Fact]
    public void FallsBackForValidRegexFeaturesUnsupportedBySafeEngine()
    {
        IReadOnlyList<ScanSearchResult> matches = ScanResultQuery.Search(Result(),
            new ScanQuery { RegexPattern = @"(.)\1" }, TestContext.Current.CancellationToken);

        Assert.Contains(matches, item => item.Name == "root");
    }

    [Fact]
    public void RelativePathJoinCannotDiscardItsStructuralPrefix()
    {
        ScanResultManaged result = Result();
        result.Names[1] = @"C:\";

        ScanSearchResult match = Assert.Single(ScanResultQuery.Search(result,
            new ScanQuery { Text = "report" }, TestContext.Current.CancellationToken));

        Assert.StartsWith($"root{Path.DirectorySeparatorChar}", match.RelativePath);
        Assert.Contains(@"C:\", match.RelativePath);
    }

    [Fact]
    public void CapsMaterializedResultsBeforeBroadQueriesGrowUnbounded()
    {
        IReadOnlyList<ScanSearchResult> matches = ScanResultQuery.Search(
            Result(), new ScanQuery { Kinds = ScanItemKinds.All, ResultLimit = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void SearchPageReportsWhetherTraversalWasClipped()
    {
        ScanSearchPage clipped = ScanResultQuery.SearchPage(Result(),
            new ScanQuery { ResultLimit = 2 }, TestContext.Current.CancellationToken);
        ScanSearchPage complete = ScanResultQuery.SearchPage(Result(),
            new ScanQuery { ResultLimit = 4 }, TestContext.Current.CancellationToken);

        Assert.True(clipped.IsTruncated);
        Assert.False(complete.IsTruncated);
        Assert.Equal(4, clipped.TotalMatches);
        Assert.Equal(4, complete.TotalMatches);
    }

    [Fact]
    public void ResultLimitKeepsGloballyLargestMatches()
    {
        var result = new ScanResultManaged
        {
            Nodes =
            [
                Node(1, None, None, None, 0),
                Node(2, None, None, None, 0),
                Node(1000, None, None, None, 0),
            ],
            Names = ["early-small", "middle", "late-largest"],
            TotalBytes = 1003,
        };

        ScanSearchPage page = ScanResultQuery.SearchPage(result,
            new ScanQuery { Kinds = ScanItemKinds.Files, ResultLimit = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, page.TotalMatches);
        Assert.True(page.IsTruncated);
        Assert.Equal(["late-largest", "middle"], page.Items.Select(item => item.Name));
    }

    [Fact]
    public void ExactResultLimitIsNotTruncated()
    {
        ScanSearchPage page = ScanResultQuery.SearchPage(Result(),
            new ScanQuery { Kinds = ScanItemKinds.Files, ResultLimit = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, page.TotalMatches);
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public void DriveRootDoesNotGainADuplicateSeparator()
    {
        var result = new ScanResultManaged
        {
            Nodes =
            [
                Node(1, None, 1, None, ScanNodeFlags.Directory),
                Node(1, 0, None, None, 0),
            ],
            Names = [@"C:\", "Users"],
            TotalBytes = 1,
        };

        ScanSearchResult match = Assert.Single(ScanResultQuery.Search(result,
            new ScanQuery { Text = "Users" }, TestContext.Current.CancellationToken));

        Assert.Equal(@"C:\Users", match.RelativePath);
    }

    [Fact]
    public void CombinedTopLevelUsesScanTotalWhenSyntheticRootHasNoSize()
    {
        ScanResultManaged result = Result();
        ScanNode root = result.Nodes[0];
        root.Size = 0;
        result.Nodes[0] = root;

        ScanSearchResult docs = Assert.Single(ScanResultQuery.Search(result,
            new ScanQuery { Text = "docs" }, TestContext.Current.CancellationToken));

        Assert.Equal(60d, docs.PercentOfParent);
    }

    [Fact]
    public void DetectsParentCycles()
    {
        ScanResultManaged result = Result();
        ScanNode root = result.Nodes[0];
        root.Parent = 1;
        result.Nodes[0] = root;

        Assert.Throws<InvalidDataException>(() => ScanResultQuery.Search(
            result, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ScanResultQuery.Search(Result(), cancellationToken: cancellation.Token));
    }

    static ScanResultManaged Result() => new()
    {
        Nodes =
        [
            Node(100, None, 1, None, ScanNodeFlags.Directory),
            Node(60, 0, 2, 3, ScanNodeFlags.Directory),
            Node(60, 1, None, None, 0),
            Node(40, 0, None, None, 0),
        ],
        Names = ["root", "docs", "report.txt", "cache.log"],
        TotalBytes = 100,
        FileCount = 2,
        DirCount = 2,
    };

    static ScanNode Node(ulong size, uint parent, uint child, uint sibling, uint flags) => new()
    {
        Size = size,
        Parent = parent,
        FirstChild = child,
        NextSibling = sibling,
        Flags = flags,
    };
}
