using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class StorageDistributionTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void AggregatesNormalizedExtensionsAndExcludesDirectories()
    {
        ScanResultManaged result = Result(
            [Directory(10_000), File(50), File(25), File(15), File(10)],
            ["root.zip", "ONE.TXT", "two.txt", "README", "trailing."]);

        IReadOnlyList<StorageDistributionBucket> buckets = StorageDistribution.ByExtension(
            result, TestContext.Current.CancellationToken);

        Assert.Equal([".txt", StorageDistribution.NoExtension], buckets.Select(item => item.Key));
        Assert.Equal(2ul, buckets[0].FileCount);
        Assert.Equal(75ul, buckets[0].Bytes);
        Assert.Equal(75d, buckets[0].Percentage);
        Assert.Equal(2ul, buckets[1].FileCount);
        Assert.Equal(25ul, buckets[1].Bytes);
    }

    [Fact]
    public void SortsByBytesDescendingThenKey()
    {
        ScanResultManaged result = Result(
            [File(20), File(20), File(30)],
            ["z.zzz", "a.aaa", "middle.mid"]);

        Assert.Equal([".mid", ".aaa", ".zzz"],
            StorageDistribution.ByExtension(result, TestContext.Current.CancellationToken)
                .Select(item => item.Key));
    }

    [Fact]
    public void TreatsSingleDotfilesAsHavingNoExtension()
    {
        IReadOnlyList<StorageDistributionBucket> buckets = StorageDistribution.ByExtension(
            Result([File(10), File(20)], [".gitignore", ".config.json"]),
            TestContext.Current.CancellationToken);

        Assert.Contains(buckets, item => item.Key == StorageDistribution.NoExtension && item.Bytes == 10);
        Assert.Contains(buckets, item => item.Key == ".json" && item.Bytes == 20);
    }

    [Fact]
    public void MapsUsefulFileCategoriesAndFallsBackToOther()
    {
        ScanResultManaged result = Result(
            [File(10), File(20), File(30), File(40), File(50), File(60), File(70), File(80), File(90)],
            ["book.PDF", "photo.png", "movie.mkv", "song.flac", "backup.7z", "main.cs",
                "setup.exe", "kernel.sys", "unknown.thing"]);

        IReadOnlyList<StorageDistributionBucket> buckets = StorageDistribution.ByCategory(
            result, TestContext.Current.CancellationToken);

        Assert.Equal(9, buckets.Count);
        Assert.Contains(buckets, bucket => bucket.Key == "documents" && bucket.Bytes == 10);
        Assert.Contains(buckets, bucket => bucket.Key == "images" && bucket.Bytes == 20);
        Assert.Contains(buckets, bucket => bucket.Key == "video" && bucket.Bytes == 30);
        Assert.Contains(buckets, bucket => bucket.Key == "audio" && bucket.Bytes == 40);
        Assert.Contains(buckets, bucket => bucket.Key == "archives" && bucket.Bytes == 50);
        Assert.Contains(buckets, bucket => bucket.Key == "code" && bucket.Bytes == 60);
        Assert.Contains(buckets, bucket => bucket.Key == "apps" && bucket.Bytes == 70);
        Assert.Contains(buckets, bucket => bucket.Key == "system" && bucket.Bytes == 80);
        Assert.Contains(buckets, bucket => bucket.Key == "other" && bucket.Bytes == 90);
    }

    [Fact]
    public void ZeroByteFilesHaveFiniteZeroPercentages()
    {
        IReadOnlyList<StorageDistributionBucket> buckets = StorageDistribution.ByExtension(
            Result([File(0), File(0)], ["one.txt", "two.bin"]),
            TestContext.Current.CancellationToken);

        Assert.All(buckets, bucket => Assert.Equal(0d, bucket.Percentage));
    }

    [Fact]
    public void SaturatesByteTotalsWithoutOverflow()
    {
        IReadOnlyList<StorageDistributionBucket> buckets = StorageDistribution.ByExtension(
            Result([File(ulong.MaxValue), File(10)], ["one.bin", "two.BIN"]),
            TestContext.Current.CancellationToken);

        StorageDistributionBucket bucket = Assert.Single(buckets);
        Assert.Equal(ulong.MaxValue, bucket.Bytes);
        Assert.Equal(100d, bucket.Percentage);
    }

    [Fact]
    public void RejectsMismatchedNodeAndNameArrays()
    {
        ScanResultManaged result = Result([File(1)], []);

        Assert.Throws<ArgumentException>(() => StorageDistribution.ByExtension(
            result, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            StorageDistribution.ByCategory(Result([File(1)], ["one.txt"]), cancellation.Token));
    }

    static ScanResultManaged Result(ScanNode[] nodes, string[] names) => new()
    {
        Nodes = nodes,
        Names = names,
    };

    static ScanNode Directory(ulong size) => new()
    {
        Size = size,
        Parent = None,
        Flags = ScanNodeFlags.Directory,
    };

    static ScanNode File(ulong size) => new()
    {
        Size = size,
        Parent = None,
    };
}
