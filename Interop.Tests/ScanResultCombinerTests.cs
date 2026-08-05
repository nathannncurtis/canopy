using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanResultCombinerTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void EmptyInputProducesSyntheticRoot()
    {
        ScanResultManaged combined = ScanResultCombiner.Combine([], "All targets");

        Assert.Single(combined.Nodes);
        Assert.Equal("All targets", combined.Names[0]);
        Assert.Equal(None, combined.Nodes[0].FirstChild);
        Assert.Equal(1ul, combined.DirCount);
    }

    [Fact]
    public void RemapsIndicesAndLinksRoots()
    {
        ScanResultManaged first = Result("one", "child", 10);
        ScanResultManaged second = Result("two", "leaf", 20);

        ScanResultManaged combined = ScanResultCombiner.Combine([first, second]);

        Assert.Equal(["Combined scan", "one", "child", "two", "leaf"], combined.Names);
        Assert.Equal(1u, combined.Nodes[0].FirstChild);
        Assert.Equal(3u, combined.Nodes[1].NextSibling);
        Assert.Equal(None, combined.Nodes[3].NextSibling);
        Assert.Equal(2u, combined.Nodes[1].FirstChild);
        Assert.Equal(4u, combined.Nodes[3].FirstChild);
        Assert.Equal(1u, combined.Nodes[2].Parent);
        Assert.Equal(3u, combined.Nodes[4].Parent);
        Assert.Equal(30ul, combined.TotalBytes);
        Assert.Equal(2ul, combined.FileCount);
        Assert.Equal(3ul, combined.DirCount);
    }

    [Fact]
    public void RejectsMismatchedNodeAndNameArrays()
    {
        var invalid = new ScanResultManaged
        {
            Nodes = [new ScanNode()],
            Names = [],
        };

        Assert.Throws<ArgumentException>(() => ScanResultCombiner.Combine([invalid]));
    }

    [Fact]
    public void RejectsOutOfRangeLinks()
    {
        ScanResultManaged invalid = Result("root", "child", 1);
        ScanNode child = invalid.Nodes[1];
        child.Parent = 99;
        invalid.Nodes[1] = child;

        Assert.Throws<ArgumentException>(() => ScanResultCombiner.Combine([invalid]));
    }

    static ScanResultManaged Result(string root, string child, ulong bytes) => new()
    {
        Nodes =
        [
            new ScanNode
            {
                Parent = None,
                FirstChild = 1,
                NextSibling = None,
                Flags = ScanNodeFlags.Directory,
            },
            new ScanNode
            {
                Size = bytes,
                Parent = 0,
                FirstChild = None,
                NextSibling = None,
            },
        ],
        Names = [root, child],
        TotalBytes = bytes,
        FileCount = 1,
        DirCount = 1,
        ElapsedSec = 0.25,
    };
}
