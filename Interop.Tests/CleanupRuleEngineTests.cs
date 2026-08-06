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
