using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanSnapshotCatalogTests
{
    [Fact]
    public async Task CapturesRoundTripsAndBuildsDeterministicTrend()
    {
        using var temp = new TempDirectory();
        var store = new ScanSnapshotCatalog(temp.Path);
        await store.CaptureAsync(@"C:\", Result(10), Utc(1), TestContext.Current.CancellationToken);
        ScanSnapshotMetadata second = await store.CaptureAsync(@"c:/", Result(20), Utc(2), TestContext.Current.CancellationToken);
        var loaded = new ScanSnapshotCatalog(temp.Path); await loaded.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal([10ul, 20ul], loaded.GetTrend(@"C:\").Select(x => x.TotalBytes));
        Assert.Equal(20ul, (await loaded.OpenAsync(second.Id, TestContext.Current.CancellationToken)).TotalBytes);
    }

    [Fact]
    public async Task AppliesCountRetentionPerTargetWithoutDeletingForeignFiles()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        string foreign = Path.Combine(temp.Path, "keep.txt"); await File.WriteAllTextAsync(foreign, "keep", token);
        var store = new ScanSnapshotCatalog(temp.Path, new(MaximumPerTarget: 2));
        await store.CaptureAsync(@"C:\", Result(1), Utc(1), token);
        await store.CaptureAsync(@"C:\", Result(2), Utc(2), token);
        await store.CaptureAsync(@"C:\", Result(3), Utc(3), token);
        Assert.Equal([3ul, 2ul], store.Entries.Select(x => x.TotalBytes));
        Assert.True(File.Exists(foreign));
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*.canopy").Length);
    }

    [Fact]
    public async Task ForecastReportsConfidenceAndInsufficientData()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory(); var store = new ScanSnapshotCatalog(temp.Path);
        Assert.False(store.ForecastFull(@"D:\", 100).HasSufficientData);
        await store.CaptureAsync(@"D:\", Result(10), Utc(1), token);
        await store.CaptureAsync(@"D:\", Result(20), Utc(2), token);
        await store.CaptureAsync(@"D:\", Result(30), Utc(3), token);
        CapacityForecast forecast = store.ForecastFull(@"D:\", 50);
        Assert.True(forecast.HasSufficientData); Assert.Equal(10, forecast.DailyGrowthBytes, 6);
        Assert.Equal(1, forecast.Confidence, 6); Assert.Equal(Utc(5), forecast.EstimatedFullUtc);
    }

    [Fact]
    public async Task ConcurrentCapturesAreSerializedAndCancellationPublishesNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory(); var store = new ScanSnapshotCatalog(temp.Path, new(20));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.CaptureAsync(@"E:\", Result((ulong)i), Utc(i + 1), token)));
        Assert.Equal(10, store.Entries.Count);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CaptureAsync(@"E:\", Result(99), cancellationToken: cancellation.Token));
        Assert.Equal(10, store.Entries.Count);
    }

    static ScanResultManaged Result(ulong bytes) => new()
    {
        Nodes = [new ScanNode { Parent = uint.MaxValue, FirstChild = uint.MaxValue, NextSibling = uint.MaxValue,
            Flags = ScanNodeFlags.Directory, Size = bytes }], Names = [@"C:\"], TotalBytes = bytes, DirCount = 1,
    };
    static DateTimeOffset Utc(int day) => new(2026, 1, day, 0, 0, 0, TimeSpan.Zero);
    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canopy-catalog-{Guid.NewGuid():N}");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
