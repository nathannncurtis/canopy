using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanAnomalyFinderTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void FindsDeepLongAndTroublesomeNamesInStableOrder()
    {
        ScanResultManaged result = Result(
            [Node(None), Node(0), Node(1), Node(2), Node(0), Node(0)],
            ["root", "one", "two", "very-long-name.txt", "CON.txt", "bad?.txt"]);
        var options = new ScanAnomalyOptions
        {
            DeepHierarchyThreshold = 2,
            LongNameThreshold = 10,
            LongPathThreshold = 14,
        };

        IReadOnlyList<ScanAnomaly> anomalies = ScanAnomalyFinder.Find(
            result, options, TestContext.Current.CancellationToken);

        Assert.Equal(anomalies.OrderBy(item => item.NodeIndex), anomalies);
        Assert.Contains(anomalies, item => item.NodeIndex == 3 && item.Kind == ScanAnomalyKind.DeepHierarchy);
        Assert.Contains(anomalies, item => item.NodeIndex == 3 && item.Kind == ScanAnomalyKind.LongName);
        Assert.Contains(anomalies, item => item.NodeIndex == 3 && item.Kind == ScanAnomalyKind.LongPath);
        Assert.Contains(anomalies, item => item.NodeIndex == 4 && item.Kind == ScanAnomalyKind.TroublesomeWindowsName);
        Assert.Contains(anomalies, item => item.NodeIndex == 5 && item.Kind == ScanAnomalyKind.TroublesomeWindowsName);
        Assert.Contains(anomalies, item => item.NodeIndex == 3 &&
            item.Path == Path.Combine("root", "one", "two", "very-long-name.txt"));
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("com1.log")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("control\u0001")]
    public void RecognizesWindowsTroublesomeNames(string name)
    {
        IReadOnlyList<ScanAnomaly> anomalies = ScanAnomalyFinder.Find(
            Result([Node(None)], [name]), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(anomalies, item => item.Kind == ScanAnomalyKind.TroublesomeWindowsName);
    }

    [Fact]
    public void AllowsBoundaryValues()
    {
        var options = new ScanAnomalyOptions
        {
            DeepHierarchyThreshold = 0,
            LongNameThreshold = 4,
            LongPathThreshold = 4,
        };

        IReadOnlyList<ScanAnomaly> anomalies = ScanAnomalyFinder.Find(
            Result([Node(None)], ["root"]), options, TestContext.Current.CancellationToken);

        Assert.Empty(anomalies);
    }

    [Fact]
    public void RejectsInvalidParentsAndCycles()
    {
        Assert.Throws<InvalidDataException>(() => ScanAnomalyFinder.Find(
            Result([Node(4)], ["bad"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => ScanAnomalyFinder.Find(
            Result([Node(1), Node(0)], ["a", "b"]),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ScanAnomalyFinder.Find(
            Result([Node(None)], ["root"]), cancellationToken: cancellation.Token));
    }

    static ScanResultManaged Result(ScanNode[] nodes, string[] names) => new()
    {
        Nodes = nodes,
        Names = names,
    };

    static ScanNode Node(uint parent) => new() { Parent = parent };
}
