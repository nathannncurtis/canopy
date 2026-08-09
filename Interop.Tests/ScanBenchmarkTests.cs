using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanBenchmarkTests
{
    [Fact]
    public async Task WarmsUpRepeatsAndReportsStablePrivacySafeContext()
    {
        int calls = 0;
        ScanBenchmarkResult result = await ScanBenchmark.RunAsync(_ =>
        {
            calls++;
            return Task.FromResult((Result(100), ScannerKind.Directory));
        }, new ScanBenchmarkOptions { WarmupRuns = 2, MeasuredRuns = 4 }, "dataset-token",
            TestContext.Current.CancellationToken);

        Assert.Equal(6, calls);
        Assert.Equal(4, result.Runs.Count);
        Assert.False(result.DatasetChanged);
        Assert.Equal(ScannerKind.Directory, result.Scanner);
        Assert.All(result.Runs, run => Assert.True(run.BytesPerSecond >= 0));
        Assert.DoesNotContain("dataset-token", result.DatasetIdentity);
        Assert.NotEmpty(result.Architecture);
    }

    [Fact]
    public async Task MarksDatasetMutationAcrossMeasuredRuns()
    {
        int calls = 0;
        ScanBenchmarkResult result = await ScanBenchmark.RunAsync(_ =>
            Task.FromResult((Result((ulong)Interlocked.Increment(ref calls)), ScannerKind.Mft)),
            new ScanBenchmarkOptions { WarmupRuns = 0, MeasuredRuns = 2 }, "stable",
            TestContext.Current.CancellationToken);
        Assert.True(result.DatasetChanged);
    }

    [Fact]
    public void RejectsNonRepeatableRunCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanBenchmarkOptions { MeasuredRuns = 1 }.Validate());
    }

    static ScanResultManaged Result(ulong bytes) => new()
    {
        Nodes = [new ScanNode { Parent = uint.MaxValue, Size = bytes, Flags = ScanNodeFlags.Directory }],
        Names = ["root"], TotalBytes = bytes, DirCount = 1,
    };
}
