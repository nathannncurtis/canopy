using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanResultMetricsTests
{
    [Fact]
    public void CalculatesCountsDepthAveragesAndPercentages()
    {
        ScanResultManaged result = CreateResult();

        ScanResultMetrics metrics = ScanResultMetrics.Calculate(result);

        Assert.Equal(4, metrics.Count);
        Assert.Equal(2ul, metrics[0].FileCount);
        Assert.Equal(2ul, metrics[0].DirectoryCount);
        Assert.Equal(50ul, metrics[0].AverageFileSize);
        Assert.Equal(2u, metrics[0].DescendantDepth);
        Assert.Equal(100d, metrics[0].PercentageOfParent);
        Assert.Equal(100d, metrics[0].PercentageOfScan);
        Assert.Equal(1ul, metrics[1].FileCount);
        Assert.Equal(1ul, metrics[1].DirectoryCount);
        Assert.Equal(60ul, metrics[1].AverageFileSize);
        Assert.Equal(1u, metrics[1].DescendantDepth);
        Assert.Equal(60d, metrics[1].PercentageOfParent);
        Assert.Equal(100d, metrics[2].PercentageOfParent);
        Assert.Equal(40d, metrics[3].PercentageOfScan);
    }

    [Fact]
    public void HandlesZeroSizedTreesWithoutNonFinitePercentages()
    {
        var result = new ScanResultManaged
        {
            Nodes = [Directory(0, uint.MaxValue)],
            Names = ["root"],
            TotalBytes = 0,
        };

        ScanNodeMetrics root = ScanResultMetrics.Calculate(result)[0];

        Assert.Equal(100d, root.PercentageOfParent);
        Assert.Equal(0d, root.PercentageOfScan);
        Assert.Equal(0ul, root.AverageFileSize);
    }

    [Fact]
    public void RejectsForwardParentReferences()
    {
        var result = new ScanResultManaged
        {
            Nodes = [Directory(1, 1), Directory(1, uint.MaxValue)],
            Names = ["bad", "root"],
            TotalBytes = 1,
        };

        Assert.Throws<InvalidDataException>(() => ScanResultMetrics.Calculate(result));
    }

    static ScanResultManaged CreateResult() => new()
    {
        Nodes =
        [
            Directory(100, uint.MaxValue),
            Directory(60, 0),
            File(60, 1),
            File(40, 0),
        ],
        Names = ["root", "folder", "large.bin", "small.bin"],
        TotalBytes = 100,
        FileCount = 2,
        DirCount = 2,
    };

    static ScanNode Directory(ulong size, uint parent) => new()
    {
        Size = size,
        Parent = parent,
        FirstChild = uint.MaxValue,
        NextSibling = uint.MaxValue,
        Flags = ScanNodeFlags.Directory,
    };

    static ScanNode File(ulong size, uint parent) => new()
    {
        Size = size,
        Parent = parent,
        FirstChild = uint.MaxValue,
        NextSibling = uint.MaxValue,
    };
}
