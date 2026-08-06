using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanResultComparisonTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void FindsAddedRemovedResizedAndUniqueMoves()
    {
        ScanComparisonResult result = ScanResultComparison.Compare(
            Result(("root", None, 100, true), ("old", 0, 10, true), ("move.bin", 1, 5, false),
                ("gone.txt", 0, 3, false), ("resize.txt", 0, 2, false)),
            Result(("root", None, 140, true), ("new", 0, 30, true), ("move.bin", 1, 5, false),
                ("added.txt", 0, 4, false), ("resize.txt", 0, 8, false)),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Changes, x => x.Kind == ScanChangeKind.Moved && x.PreviousPath == @"root\old\move.bin" && x.Path == @"root\new\move.bin");
        Assert.Contains(result.Changes, x => x.Kind == ScanChangeKind.Removed && x.Path.EndsWith("gone.txt"));
        Assert.Contains(result.Changes, x => x.Kind == ScanChangeKind.Added && x.Path.EndsWith("added.txt"));
        Assert.Contains(result.Changes, x => x.Kind == ScanChangeKind.Resized && x.SizeDelta == 6);
    }

    [Fact]
    public void AmbiguousFingerprintsAreNotReportedAsMoves()
    {
        ScanComparisonResult result = ScanResultComparison.Compare(
            Result(("root", None, 2, true), ("a", 0, 1, true), ("same.bin", 1, 1, false),
                ("b", 0, 1, true), ("same.bin", 3, 1, false)),
            Result(("root", None, 2, true), ("c", 0, 1, true), ("same.bin", 1, 1, false),
                ("d", 0, 1, true), ("same.bin", 3, 1, false)),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Changes, x => x.Kind == ScanChangeKind.Moved);
        Assert.Equal(4, result.Changes.Count(x => x.Kind is ScanChangeKind.Added or ScanChangeKind.Removed && x.Path.EndsWith("same.bin")));
    }

    [Fact]
    public void ReportsDirectoryGrowthAndRanksFastest()
    {
        ScanComparisonResult result = ScanResultComparison.Compare(
            Result(("root", None, 100, true), ("small", 0, 10, true), ("fast", 0, 20, true)),
            Result(("root", None, 180, true), ("small", 0, 15, true), ("fast", 0, 80, true)),
            TestContext.Current.CancellationToken);
        Assert.Equal([@"root", @"root\fast"], result.GetFastestGrowingFolders(2).Select(x => x.Path));
        Assert.Equal(60, result.DirectoryGrowth.Single(x => x.Path.EndsWith("fast")).SizeDelta);
    }

    [Fact]
    public void HonorsCancellationAndRejectsMalformedTopology()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ScanResultComparison.Compare(Result(("root", None, 0, true)), Result(("root", None, 0, true)), cancellation.Token));
        Assert.Throws<InvalidDataException>(() => ScanResultComparison.Compare(
            Result(("root", 3, 0, true)), Result(("root", None, 0, true)),
            TestContext.Current.CancellationToken));
    }

    static ScanResultManaged Result(params (string Name, uint Parent, ulong Size, bool Directory)[] items) => new()
    {
        Nodes = items.Select(x => new ScanNode { Parent = x.Parent, FirstChild = None, NextSibling = None,
            Size = x.Size, Flags = x.Directory ? ScanNodeFlags.Directory : 0 }).ToArray(),
        Names = items.Select(x => x.Name).ToArray(),
        TotalBytes = items.Length == 0 ? 0 : items[0].Size,
    };
}
