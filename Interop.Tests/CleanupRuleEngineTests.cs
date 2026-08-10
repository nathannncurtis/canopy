using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class CleanupRuleEngineTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public void ParsesJsonAndProducesDeterministicUniqueRiskPreview()
    {
        CleanupRuleSet rules = CleanupRuleEngine.Parse("""
        { "version": 1, "rules": [
          { "id":"logs", "reason":"Old logs", "risk":"Low", "extensions":["log"], "minimumSize":10 },
          { "id":"cache", "reason":"Cache content", "risk":"High", "pathPattern":"*cache*", "kind":"Files" }
        ] }
        """);

        CleanupPreview preview = CleanupRuleEngine.Preview(Result(), rules, TestContext.Current.CancellationToken);

        Assert.Equal([2u, 3u], preview.Items.Select(item => item.NodeIndex));
        Assert.Equal(["logs", "cache"], preview.Items[0].RuleIds);
        Assert.Equal(CleanupRisk.High, preview.Items[0].Risk);
        Assert.Equal(30ul, preview.ReclaimableBytes);
        Assert.Equal(2, preview.HighRiskCount);
    }

    [Fact]
    public void RejectsExecutableOrMatchEverythingShapes()
    {
        Assert.Throws<InvalidDataException>(() => CleanupRuleEngine.Parse("""
        { "version":1, "rules":[{"id":"bad","reason":"unsafe","command":"rm","kind":"All"}] }
        """));
        Assert.Throws<InvalidDataException>(() => CleanupRuleEngine.Parse("""
        { "version":1, "rules":[{"id":"bad","reason":"matches everything","kind":"All"}] }
        """));
        Assert.Throws<InvalidDataException>(() => CleanupRuleEngine.Parse("""
        { "version":1, "rules":[{"id":"escape","reason":"escape","pathPattern":"../*"}] }
        """));
    }

    [Fact]
    public void AuditCoversEveryRuleAndExportsDeterministically()
    {
        CleanupRuleSet rules = CleanupRuleEngine.Parse("""
        { "version":1, "rules":[
          {"id":"logs","reason":"Old logs","risk":"Low","extensions":["log"]},
          {"id":"none","reason":"No executables","risk":"High","extensions":["exe"]}
        ] }
        """);
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        CleanupAuditReport report = CleanupRuleEngine.Audit(Result(), rules, stamp, TestContext.Current.CancellationToken);
        Assert.Equal(["logs", "none"], report.Rules.Select(x => x.RuleId)); Assert.Equal(0, report.Rules[1].MatchCount);
        string first = CleanupReportExporter.ToMarkdown(report), second = CleanupReportExporter.ToMarkdown(report);
        Assert.Equal(first, second); Assert.Contains("Preview only", first); Assert.Contains("matched no items", first);
        Assert.Equal(CleanupReportExporter.ToJson(report), CleanupReportExporter.ToJson(report));
    }

    [Fact]
    public void PhysicalEstimateDoesNotDoubleCountMatchedDirectoryAndChild()
    {
        ScanResultManaged result = Result();
        result.Metadata =
        [
            new(ScanNodeMetadataFlags.UniqueAllocation, 1, 1, 1, 30, 12, 12),
            new(ScanNodeMetadataFlags.UniqueAllocation, 1, 1, 2, 30, 12, 12),
            new(ScanNodeMetadataFlags.UniqueAllocation, 1, 1, 3, 20, 8, 8),
            new(ScanNodeMetadataFlags.UniqueAllocation, 1, 1, 4, 10, 4, 4),
        ];
        CleanupRuleSet rules = CleanupRuleEngine.Parse("""{ "version":1, "rules":[{"id":"cache-tree","reason":"Cache tree","risk":"Medium","pathPattern":"*cache*","kind":"All"}] }""");
        CleanupPreview preview = CleanupRuleEngine.Preview(result, rules, TestContext.Current.CancellationToken);
        Assert.Equal(3, preview.Items.Count); Assert.Equal((ulong)12, preview.ReclaimableBytes); Assert.True(preview.UsesPhysicalAllocation);
    }

    [Fact]
    public void PhysicalEstimateCountsHardLinkAliasesOnce()
    {
        ScanResultManaged result = Result();
        result.Metadata =
        [
            new(ScanNodeMetadataFlags.UniqueAllocation, 1, 1, 1, 30, 8, 8), null,
            new(ScanNodeMetadataFlags.UniqueAllocation | ScanNodeMetadataFlags.CanonicalLinkOnly, 2, 1, 99, 20, 8, 8),
            new(ScanNodeMetadataFlags.None, 2, 1, 99, 10, 8, 0),
        ];
        CleanupRuleSet rules = CleanupRuleEngine.Parse("""{ "version":1, "rules":[{"id":"files","reason":"Selected files","namePattern":"*.*"}] }""");
        CleanupPreview preview = CleanupRuleEngine.Preview(result, rules, TestContext.Current.CancellationToken);
        Assert.Equal((ulong)8, preview.ReclaimableBytes);
    }

    [Fact]
    public void EmptyAuditStatesEstimateIsNotApplicable()
    {
        CleanupRuleSet rules = CleanupRuleEngine.Parse("""{ "version":1, "rules":[{"id":"none","reason":"No matches","extensions":["never"]}] }""");
        CleanupAuditReport report = CleanupRuleEngine.Audit(Result(), rules, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);
        Assert.Empty(report.Preview.Items); Assert.Contains("not applicable (no proposed items)", CleanupReportExporter.ToMarkdown(report));
    }

    [Fact]
    public async Task AtomicExportReplacesDestinationAndLeavesNoTemporaryFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canopy-audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string destination = Path.Combine(directory, "audit-文件.md"); await File.WriteAllTextAsync(destination, "old", TestContext.Current.CancellationToken);
            await CleanupReportExporter.WriteAtomicAsync(destination, "new", TestContext.Current.CancellationToken);
            Assert.Equal("new", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp-*"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RejectsTopologyCyclesAndHonorsCancellation()
    {
        ScanResultManaged result = Result();
        ScanNode root = result.Nodes[0]; root.Parent = 1; result.Nodes[0] = root;
        CleanupRuleSet rules = CleanupRuleEngine.Parse("""
        { "version":1, "rules":[{"id":"tmp","reason":"Temporary files","extensions":["tmp"]}] }
        """);
        Assert.Throws<InvalidDataException>(() => CleanupRuleEngine.Preview(
            result, rules, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CleanupRuleEngine.Preview(Result(), rules, cancellation.Token));
    }

    static ScanResultManaged Result() => new()
    {
        Nodes =
        [
            new ScanNode { Parent = None, Flags = ScanNodeFlags.Directory, Size = 30 },
            new ScanNode { Parent = 0, Flags = ScanNodeFlags.Directory, Size = 30 },
            new ScanNode { Parent = 1, Size = 20 },
            new ScanNode { Parent = 1, Size = 10 },
        ],
        Names = ["root", "cache", "app.log", "data.tmp"],
        TotalBytes = 30,
    };
}
