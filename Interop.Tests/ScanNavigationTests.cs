using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanNavigationTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void BuildsNestedWindowsPathsAndBreadcrumbs()
    {
        var index = new ScanNavigationIndex(Result());

        Assert.Equal(@"C:\Users\Alice\report.txt", index.GetPath(3));
        Assert.Equal([0u, 1u, 2u, 3u], index.GetBreadcrumbs(3).Select(x => x.NodeIndex));
        Assert.Equal([@"C:\", "Users", "Alice", "report.txt"], index.GetBreadcrumbs(3).Select(x => x.Name));
    }

    [Theory]
    [InlineData(@"c:\users\alice\REPORT.TXT")]
    [InlineData(@"C:/Users/Alice/report.txt")]
    [InlineData(@" C:\Users\Alice\report.txt\ ")]
    public void FindsPathsCaseInsensitivelyAndAcceptsSeparators(string path)
    {
        var index = new ScanNavigationIndex(Result());

        Assert.True(index.TryFind(path, out uint node));
        Assert.Equal(3u, node);
        Assert.Equal("report.txt", index.GetBreadcrumbs(node)[^1].Name);
    }

    [Fact]
    public void TracksBackForwardAndTruncatesForwardHistory()
    {
        var navigation = new ScanNavigation(new ScanNavigationIndex(Result()));

        Assert.True(navigation.Navigate(1));
        Assert.True(navigation.Navigate(@"C:\Users\Alice"));
        Assert.False(navigation.Navigate(2));
        Assert.True(navigation.GoBack());
        Assert.Equal(1u, navigation.Current);
        Assert.True(navigation.GoForward());
        Assert.True(navigation.GoBack());
        Assert.True(navigation.Navigate(4));
        Assert.False(navigation.CanGoForward);
        Assert.False(navigation.GoForward());
        Assert.Equal(@"C:\Temp", navigation.CurrentPath);
    }

    [Fact]
    public void RootNavigationIsSafe()
    {
        var navigation = new ScanNavigation(new ScanNavigationIndex(Result()));

        Assert.False(navigation.NavigateParent());
        Assert.False(navigation.GoBack());
        Assert.False(navigation.GoForward());
        Assert.Equal(0u, navigation.Current);
    }

    [Fact]
    public void NavigateParentAddsHistory()
    {
        var navigation = new ScanNavigation(new ScanNavigationIndex(Result()), 3);

        Assert.True(navigation.NavigateParent());
        Assert.Equal(2u, navigation.Current);
        Assert.True(navigation.GoBack());
        Assert.Equal(3u, navigation.Current);
    }

    [Fact]
    public void RejectsOutOfRangeParent()
    {
        ScanResultManaged result = Result();
        result.Nodes[1] = Node(99);
        Assert.Throws<InvalidDataException>(() => new ScanNavigationIndex(result));
    }

    [Fact]
    public void InvalidParentDiagnosticIdentifiesTheOwningAncestor()
    {
        var result = new ScanResultManaged
        {
            Nodes = [Node(None), Node(3), Node(None), Node(99)],
            Names = ["root", "child", "other", "broken"],
        };
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new ScanNavigationIndex(result));
        Assert.Contains("Node 3", error.Message);
        Assert.Contains("index 99", error.Message);
    }

    [Fact]
    public void RejectsParentCycles()
    {
        ScanResultManaged result = Result();
        result.Nodes[0] = Node(1);
        Assert.Throws<InvalidDataException>(() => new ScanNavigationIndex(result));
    }

    [Fact]
    public void AmbiguousCombinedPathsRemainBrowsableButCannotBeTyped()
    {
        var result = new ScanResultManaged
        {
            Nodes = [Node(None), Node(0), Node(0)],
            Names = ["Combined scan", "same", "same"],
        };
        var index = new ScanNavigationIndex(result);

        Assert.Equal(@"Combined scan\same", index.GetPath(1));
        Assert.Equal(@"Combined scan\same", index.GetPath(2));
        Assert.False(index.TryFind(@"Combined scan\same", out _));
        Assert.True(index.IsAmbiguous(@"combined scan/same"));
        Assert.False(index.IsAmbiguous(@"Combined scan\missing"));
    }

    static ScanResultManaged Result() => new()
    {
        Nodes = [Node(None), Node(0), Node(1), Node(2), Node(0)],
        Names = [@"C:\", "Users", "Alice", "report.txt", "Temp"],
    };

    static ScanNode Node(uint parent) => new() { Parent = parent };
}
