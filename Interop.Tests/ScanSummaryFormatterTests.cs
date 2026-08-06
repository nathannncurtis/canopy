using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanSummaryFormatterTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void PrivacySafeMarkdownIsDeterministicAndSortsLargestData()
    {
        ScanResultManaged result = Result();
        StorageDistributionBucket[] categories =
        [
            new("Documents", 2, 20, 20),
            new("Archives", 1, 80, 80),
        ];

        string first = ScanSummaryFormatter.Format(result, categories,
            new ScanSummaryOptions { Format = ScanSummaryFormat.Markdown }, TestContext.Current.CancellationToken);
        string second = ScanSummaryFormatter.Format(result, categories,
            new ScanSummaryOptions { Format = ScanSummaryFormat.Markdown }, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Contains("# Canopy Scan Summary", first);
        Assert.Contains("Item #2 — 80 B", first);
        Assert.True(first.IndexOf("Archives", StringComparison.Ordinal) < first.IndexOf("Documents", StringComparison.Ordinal));
        Assert.DoesNotContain("private.zip", first);
        Assert.DoesNotContain("Alice", first);
    }

    [Fact]
    public void SensitiveOptInIncludesFullPathsAndPlainTextLimits()
    {
        string summary = ScanSummaryFormatter.Format(Result(), options: new ScanSummaryOptions
        {
            IncludeSensitivePaths = true,
            LargestItemCount = 1,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(Path.Combine("root", "Alice", "private.zip"), summary);
        Assert.DoesNotContain("# Canopy", summary);
        Assert.Equal(1, summary.Split('\n').Count(line => line.StartsWith("1. ", StringComparison.Ordinal)));
    }

    [Fact]
    public void RejectsMalformedTopologyAndHonorsCancellation()
    {
        ScanResultManaged malformed = Result();
        ScanNode node = malformed.Nodes[1];
        node.Parent = 99;
        malformed.Nodes[1] = node;
        Assert.Throws<InvalidDataException>(() => ScanSummaryFormatter.Format(
            malformed, cancellationToken: TestContext.Current.CancellationToken));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ScanSummaryFormatter.Format(Result(), cancellationToken: cancellation.Token));
    }

    static ScanResultManaged Result() => new()
    {
        Nodes =
        [
            new ScanNode { Parent = None, Size = 100, Flags = ScanNodeFlags.Directory },
            new ScanNode { Parent = 0, Size = 100, Flags = ScanNodeFlags.Directory },
            new ScanNode { Parent = 1, Size = 80 },
            new ScanNode { Parent = 1, Size = 20 },
        ],
        Names = ["root", "Alice", "private.zip", "notes.txt"],
        TotalBytes = 100,
        FileCount = 2,
        DirCount = 2,
        ElapsedSec = 1.25,
    };
}
