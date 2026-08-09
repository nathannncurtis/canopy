using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class TreemapPresentationTests
{
    [Fact]
    public void ViewportZoomPanAndResetAreBounded()
    {
        TreemapViewport view = TreemapViewport.Fitted.Zoom(100, 50, 25, 100, 50);
        Assert.Equal(8, view.Scale); Assert.InRange(view.X, -700, 0); Assert.InRange(view.Y, -350, 0);
        view = view.Pan(10_000, -10_000, 100, 50);
        Assert.Equal(0, view.X); Assert.Equal(-350, view.Y);
        Assert.Equal(new TreemapViewport(1, 0, 0), TreemapViewport.Fitted);
        Assert.Equal(TreemapViewport.Fitted, view.Zoom(2, 0, 0, 0, double.NaN));
        Assert.Equal(TreemapViewport.Fitted, view.Pan(double.PositiveInfinity, 1, 100, 50));
    }

    [Fact]
    public void StableColorBucketsIgnoreOrderCaseAndSurviveRepeatedCalls()
    {
        int first = TreemapPresentationRules.StableBucket(".TXT", 8);
        Assert.Equal(first, TreemapPresentationRules.StableBucket(".txt", 8));
        Assert.Equal(first, TreemapPresentationRules.StableBucket(".TXT", 8));
        Assert.Equal("(no extension)", TreemapPresentationRules.ExtensionKey("README"));
        IReadOnlyList<TreemapLegendEntry> legend = TreemapPresentationRules.Legend(
            [(".txt", 10), (".zip", 30), (".txt", 5)], 8);
        Assert.Equal(TreemapPresentationRules.StableBucket(legend[0].Key, 8), legend[0].Bucket);
        Assert.Equal(".zip", legend[0].Key);
    }

    [Fact]
    public void LabelsExposeConfiguredInformation()
    {
        Assert.Equal("file · 1,024 B", TreemapPresentationRules.Label("file", 1024, 12.5, TreemapLabelMode.NameAndSize));
        Assert.Contains("12.5%", TreemapPresentationRules.Label("file", 1024, 12.5, TreemapLabelMode.NameSizeAndPercent));
        Assert.Empty(TreemapPresentationRules.Label("file", 1, 1, TreemapLabelMode.Hidden));
    }

    [Fact]
    public async Task StoreIsVersionedAtomicAndRejectsUnknownVersion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canopy-treemap-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new TreemapPresentationStore(path);
            var expected = new TreemapPresentationPreferences { Labels = TreemapLabelMode.NameSizeAndPercent,
                Colors = TreemapColorMode.Extension, MinimumLabelArea = 2500 };
            await store.SaveAsync(expected, TestContext.Current.CancellationToken);
            Assert.Equal(expected, await store.LoadAsync(TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(path, "{\"Version\":99,\"Labels\":2}", TestContext.Current.CancellationToken);
            Assert.Equal(new TreemapPresentationPreferences(), await store.LoadAsync(TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(path, "{\"Version\":1,\"Unknown\":true}", TestContext.Current.CancellationToken);
            Assert.Equal(new TreemapPresentationPreferences(), await store.LoadAsync(TestContext.Current.CancellationToken));
            await File.WriteAllBytesAsync(path, new byte[65 * 1024], TestContext.Current.CancellationToken);
            Assert.Equal(new TreemapPresentationPreferences(), await store.LoadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(expected with { MinimumLabelArea = double.NaN },
                TestContext.Current.CancellationToken));
            Task first = store.SaveAsync(expected with { Labels = TreemapLabelMode.Name }, TestContext.Current.CancellationToken);
            Task last = store.SaveAsync(expected with { Labels = TreemapLabelMode.Hidden }, TestContext.Current.CancellationToken);
            await Task.WhenAll(first, last);
            Assert.Equal(TreemapLabelMode.Hidden, (await store.LoadAsync(TestContext.Current.CancellationToken)).Labels);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }


    [Fact]
    public void DescendantMetricsWorkIsLinearForLargeWideTree()
    {
        const int count = 20_001;
        var nodes = new ScanNode[count]; var names = new string[count];
        nodes[0] = new ScanNode { Parent = uint.MaxValue, FirstChild = 1, NextSibling = uint.MaxValue, Flags = ScanNodeFlags.Directory };
        names[0] = "root";
        for (int i = 1; i < count; i++)
        {
            nodes[i] = new ScanNode { Parent = 0, FirstChild = uint.MaxValue,
                NextSibling = i + 1 < count ? (uint)(i + 1) : uint.MaxValue };
            names[i] = i.ToString();
        }
        TreemapHierarchyMetrics metrics = TreemapHierarchyMetrics.Calculate(new ScanResultManaged
            { Nodes = nodes, Names = names });
        Assert.Equal((ulong)(count - 1), metrics.Counts[0].Files);
        Assert.InRange(metrics.WorkItems, (ulong)count, (ulong)(count * 2));
    }
}
