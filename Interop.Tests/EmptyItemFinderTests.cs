using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class EmptyItemFinderTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void FindsZeroByteFilesAndLeafDirectoriesWithFullPaths()
    {
        ScanResultManaged result = Result(
            [
                Directory(12, None),       // root
                Directory(0, 0),           // empty
                Directory(12, 0),          // populated
                File(0, 2),                // zero.dat
                File(12, 2),               // data.dat
                Directory(0, 2),           // nested-empty
            ],
            ["root", "empty", "populated", "zero.dat", "data.dat", "nested-empty"]);

        IReadOnlyList<EmptyScanItem> items = EmptyItemFinder.Find(
            result, TestContext.Current.CancellationToken);

        Assert.Equal([1u, 3u, 5u], items.Select(item => item.NodeIndex));
        Assert.Equal(
            [Path.Combine("root", "empty"), Path.Combine("root", "populated", "zero.dat"),
                Path.Combine("root", "populated", "nested-empty")],
            items.Select(item => item.RelativePath));
        Assert.Equal(
            [EmptyItemKind.Directory, EmptyItemKind.File, EmptyItemKind.Directory],
            items.Select(item => item.Kind));
    }

    [Fact]
    public void DirectoryContainingOnlyEmptyItemsIsNotEmpty()
    {
        ScanResultManaged result = Result(
            [Directory(0, None), Directory(0, 0), File(0, 1)],
            ["root", "folder", "empty.txt"]);

        IReadOnlyList<EmptyScanItem> items = EmptyItemFinder.Find(
            result, TestContext.Current.CancellationToken);

        EmptyScanItem item = Assert.Single(items);
        Assert.Equal(2u, item.NodeIndex);
        Assert.Equal(EmptyItemKind.File, item.Kind);
    }

    [Fact]
    public void PreservesNodeIndexOrderingWhenParentsFollowChildren()
    {
        ScanResultManaged result = Result(
            [File(0, 3), Directory(0, 3), File(0, 3), Directory(0, None)],
            ["first", "empty", "nested", "root"]);

        IReadOnlyList<EmptyScanItem> items = EmptyItemFinder.Find(
            result, TestContext.Current.CancellationToken);

        Assert.Equal([0u, 1u, 2u], items.Select(item => item.NodeIndex));
        Assert.Equal(Path.Combine("root", "first"), items[0].RelativePath);
        Assert.Equal(Path.Combine("root", "nested"), items[2].RelativePath);
    }

    [Fact]
    public void RejectsOutOfRangeParent()
    {
        ScanResultManaged result = Result([File(0, 7)], ["bad"]);

        Assert.Throws<InvalidDataException>(() =>
            EmptyItemFinder.Find(result, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RejectsParentCycle()
    {
        ScanResultManaged result = Result(
            [Directory(0, 1), Directory(0, 0)],
            ["one", "two"]);

        Assert.Throws<InvalidDataException>(() =>
            EmptyItemFinder.Find(result, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HonorsCancellationForLargeScans()
    {
        const int count = 100_000;
        var nodes = new ScanNode[count];
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = File(0, None);
            names[i] = $"file-{i}";
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            EmptyItemFinder.Find(Result(nodes, names), cancellation.Token));
    }

    static ScanResultManaged Result(ScanNode[] nodes, string[] names) => new()
    {
        Nodes = nodes,
        Names = names,
    };

    static ScanNode Directory(ulong size, uint parent) => new()
    {
        Size = size,
        Parent = parent,
        FirstChild = None,
        NextSibling = None,
        Flags = ScanNodeFlags.Directory,
    };

    static ScanNode File(ulong size, uint parent) => new()
    {
        Size = size,
        Parent = parent,
        FirstChild = None,
        NextSibling = None,
    };
}
