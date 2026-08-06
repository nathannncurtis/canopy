using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class DiskUsageSummaryTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void ReconcilesTotalsAndRanksFoldersAndCategoriesDeterministically()
    {
        ScanResultManaged result = Result();

        DiskUsageSummaryResult summary = DiskUsageSummary.Generate(result,
            new DiskUsageSummaryOptions { CapacityBytes = 400, LargestFolderCount = 2 },
            TestContext.Current.CancellationToken);

        Assert.True(summary.IsComplete);
        Assert.True(summary.TotalsReconcile);
        Assert.Equal(50, summary.CapacityPercentage);
        Assert.Equal(["big", "small"], summary.LargestFolders.Select(item => item.Name));
        Assert.Equal(200UL, summary.LargestCategories.Aggregate(0UL, (total, item) => total + item.Bytes));
    }

    [Fact]
    public void NormalizesLimitationsAndReportsIncompleteScan()
    {
        DiskUsageSummaryResult summary = DiskUsageSummary.Generate(Result(), new DiskUsageSummaryOptions
        {
            ScanLimitations = ["Access denied", "  Access denied ", "Changed during scan", ""],
        }, TestContext.Current.CancellationToken);

        Assert.False(summary.IsComplete);
        Assert.Equal(["Access denied", "Changed during scan"], summary.Limitations);
    }

    [Fact]
    public void HonorsCancellationAndRejectsInvalidLimits()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DiskUsageSummary.Generate(Result(), cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskUsageSummary.Generate(Result(),
            new DiskUsageSummaryOptions { LargestCategoryCount = -1 },
            TestContext.Current.CancellationToken));
    }

    static ScanResultManaged Result() => new()
    {
        Nodes =
        [
            new ScanNode { Parent = None, Size = 200, Flags = ScanNodeFlags.Directory },
            new ScanNode { Parent = 0, Size = 150, Flags = ScanNodeFlags.Directory },
            new ScanNode { Parent = 1, Size = 150 },
            new ScanNode { Parent = 0, Size = 50, Flags = ScanNodeFlags.Directory },
            new ScanNode { Parent = 3, Size = 50 },
        ],
        Names = ["root", "big", "movie.mp4", "small", "notes.txt"],
        TotalBytes = 200,
        FileCount = 2,
        DirCount = 3,
    };
}
