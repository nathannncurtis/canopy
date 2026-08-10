using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class StorageDashboardTests
{
    [Fact]
    public void MixedTreeProducesStableBoundedRankingsAndReconciledBuckets()
    {
        ScanResultManaged result = Result();
        StorageDashboardResult dashboard = StorageDashboard.Generate(result,
            new StorageDashboardOptions { TopCount = 2, CapturedUtc = DateTimeOffset.Parse("2026-01-10T00:00:00Z"),
                LastWriteUtc = new Dictionary<uint, DateTimeOffset> { [2] = DateTimeOffset.Parse("2026-01-09T12:00:00Z") } },
            TestContext.Current.CancellationToken);
        Assert.Equal(["large.bin", "small.txt"], dashboard.LargestFiles.Select(item => item.Name));
        Assert.Equal("docs", dashboard.LargestFolders[0].Name); Assert.Equal("docs", dashboard.MostFiles[0].Name);
        Assert.Equal(110UL, dashboard.FileTypes.Aggregate(0UL, (sum, item) => sum + item.Bytes));
        Assert.Equal(110UL, dashboard.SizeHistogram.Aggregate(0UL, (sum, item) => sum + item.Bytes));
        Assert.Equal(1UL, dashboard.MissingAgeFiles); Assert.Single(dashboard.FileAges);
    }

    [Fact]
    public void EmptyAndOverflowInputsRemainFiniteAndSaturate()
    {
        StorageDashboardResult empty = StorageDashboard.Generate(new() { Nodes = [], Names = [] }, token: TestContext.Current.CancellationToken);
        Assert.Empty(empty.LargestFiles); Assert.Empty(empty.FileTypes); Assert.Equal(0UL, empty.TotalBytes);
        var result = new ScanResultManaged { Nodes = [
            Node(0, uint.MaxValue, 1, uint.MaxValue, ScanNodeFlags.Directory),
            Node(ulong.MaxValue, 0, uint.MaxValue, 2, 0), Node(10, 0, uint.MaxValue, uint.MaxValue, 0)],
            Names = ["root", "huge.bin", "more.bin"], TotalBytes = ulong.MaxValue };
        StorageDashboardResult dashboard = StorageDashboard.Generate(result, token: TestContext.Current.CancellationToken);
        Assert.Equal(ulong.MaxValue, dashboard.AccountedFileBytes);
        Assert.All(dashboard.FileTypes, item => Assert.True(double.IsFinite(item.Percentage)));
    }

    [Fact]
    public void LargeWideTreeUsesLinearWorkAndKeepsTopListsBounded()
    {
        const int count = 50_001; var nodes = new ScanNode[count]; var names = new string[count];
        nodes[0] = Node(0, uint.MaxValue, 1, uint.MaxValue, ScanNodeFlags.Directory); names[0] = "root";
        for (int i = 1; i < count; i++) { nodes[i] = Node((ulong)i, 0, uint.MaxValue,
            i + 1 < count ? (uint)(i + 1) : uint.MaxValue, 0); names[i] = $"{i}.dat"; }
        StorageDashboardResult dashboard = StorageDashboard.Generate(new() { Nodes = nodes, Names = names },
            new StorageDashboardOptions { TopCount = 7 }, TestContext.Current.CancellationToken);
        Assert.Equal(7, dashboard.LargestFiles.Count); Assert.InRange(dashboard.WorkItems, (ulong)count, (ulong)(count * 4));
        Assert.Equal("50000.dat", dashboard.LargestFiles[0].Name);
    }

    [Fact]
    public void UsesResultMetadataForAgesWithoutTimestampDictionary()
    {
        ScanResultManaged result = Result();
        result.Metadata = new ScanNodeMetadata?[result.Nodes.Length];
        result.Metadata[2] = new(ScanNodeMetadataFlags.None, 1, 0, 0, 10, 10, 10,
            unchecked((ulong)DateTimeOffset.Parse("2026-01-09T12:00:00Z").ToFileTime()));
        StorageDashboardResult dashboard = StorageDashboard.Generate(result,
            new StorageDashboardOptions { CapturedUtc = DateTimeOffset.Parse("2026-01-10T00:00:00Z") },
            TestContext.Current.CancellationToken);
        Assert.Single(dashboard.FileAges);
        Assert.Equal(1UL, dashboard.MissingAgeFiles);
    }

    [Fact]
    public void FileTypesAreDeterministicallyBoundedWithExactOtherBucket()
    {
        const int typeCount = 100;
        var nodes = new ScanNode[typeCount + 1]; var names = new string[typeCount + 1];
        nodes[0] = Node(0, uint.MaxValue, 1, uint.MaxValue, ScanNodeFlags.Directory); names[0] = "root";
        ulong total = 0;
        for (int i = 1; i <= typeCount; i++)
        {
            ulong bytes = (ulong)((i + 1) / 2); // pairs exercise deterministic label tie-breaking
            total += bytes;
            nodes[i] = Node(bytes, 0, uint.MaxValue, i == typeCount ? uint.MaxValue : (uint)(i + 1), 0);
            names[i] = $"file.{i:D3}";
        }
        StorageDashboardResult dashboard = StorageDashboard.Generate(new()
            { Nodes = nodes, Names = names, TotalBytes = total }, token: TestContext.Current.CancellationToken);

        Assert.Equal(21, dashboard.FileTypes.Count);
        Assert.Equal([".099", ".100", ".097", ".098"],
            dashboard.FileTypes.Take(4).Select(item => item.Label));
        StorageDashboardBucket other = dashboard.FileTypes[^1];
        Assert.Equal("Other", other.Label); Assert.Equal(80UL, other.Files);
        Assert.Equal(total, dashboard.FileTypes.Aggregate(0UL, (sum, item) => sum + item.Bytes));
        Assert.Contains("files", other.AccessibleText); Assert.Contains("percent", other.AccessibleText);
    }

    [Fact]
    public async Task PreferencesPersistWidgetsAndDeduplicateUnicodeUncAndMissingPaths()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canopy-dashboard-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "dashboard.json");
        try
        {
            var store = new StorageDashboardPreferenceStore(path);
            await store.SaveAsync(new() { EnabledWidgets = ["overview", "OVERVIEW", "types"],
                QuickLocations = [@"C:\資料", @"c:\資料", @"\\server\share\missing"] }, TestContext.Current.CancellationToken);
            StorageDashboardPreferences loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, loaded.EnabledWidgets.Count); Assert.Equal(2, loaded.QuickLocations.Count);
            Assert.Contains(loaded.QuickLocations, item => item.StartsWith(@"\\server\share", StringComparison.OrdinalIgnoreCase));
            await File.WriteAllTextAsync(path,
                "{\"Version\":1,\"EnabledWidgets\":[\"overview\"],\"QuickLocations\":[\"C:\\\\good\",\"bad\\u0000path\"]}",
                TestContext.Current.CancellationToken);
            loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Single(loaded.QuickLocations);
            Assert.EndsWith(@"C:\good", loaded.QuickLocations[0], StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(path, "{\"Version\":1,\"Unknown\":true}", TestContext.Current.CancellationToken);
            Assert.Contains("overview", (await store.LoadAsync(TestContext.Current.CancellationToken)).EnabledWidgets);
            await File.WriteAllBytesAsync(path, new byte[65 * 1024], TestContext.Current.CancellationToken);
            Assert.Contains("overview", (await store.LoadAsync(TestContext.Current.CancellationToken)).EnabledWidgets);
            Task first = store.SaveAsync(new() { EnabledWidgets = ["overview"] }, TestContext.Current.CancellationToken);
            Task last = store.SaveAsync(new() { EnabledWidgets = ["sizes"] }, TestContext.Current.CancellationToken);
            await Task.WhenAll(first, last);
            Assert.Equal(["sizes"], (await store.LoadAsync(TestContext.Current.CancellationToken)).EnabledWidgets);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static ScanResultManaged Result() => new() { Nodes = [
        Node(110, uint.MaxValue, 1, uint.MaxValue, ScanNodeFlags.Directory),
        Node(110, 0, 2, uint.MaxValue, ScanNodeFlags.Directory),
        Node(10, 1, uint.MaxValue, 3, 0), Node(100, 1, uint.MaxValue, uint.MaxValue, 0)],
        Names = ["root", "docs", "small.txt", "large.bin"], TotalBytes = 110 };
    static ScanNode Node(ulong size, uint parent, uint child, uint sibling, uint flags) => new()
        { Size = size, Parent = parent, FirstChild = child, NextSibling = sibling, Flags = flags };
}
