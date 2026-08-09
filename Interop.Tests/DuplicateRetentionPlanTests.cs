using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class DuplicateRetentionPlanTests
{
    [Fact]
    public void ExplicitSurvivorCanNeverEnterRemovablePaths()
    {
        DuplicateFileGroup[] groups = [new(10, "HASH", [@"C:\b.bin", @"C:\a.bin", @"C:\c.bin"])];
        DuplicateRetentionPlan plan = DuplicateRetentionPlanner.Create(groups,
            new Dictionary<string, string> { ["HASH"] = @"C:\b.bin" });
        DuplicateRetentionGroup group = Assert.Single(plan.Groups);
        Assert.Equal(@"C:\b.bin", group.RetainedPath);
        Assert.DoesNotContain(group.RetainedPath, group.RemovablePaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, group.RemovablePaths.Count);
        Assert.Equal(20UL, plan.ReclaimableBytes);
    }

    [Fact]
    public void DefaultsDeterministicallyAndRejectsForeignSelection()
    {
        DuplicateFileGroup[] groups = [new(5, "HASH", [@"C:\z", @"C:\a"])];
        Assert.Equal(@"C:\a", DuplicateRetentionPlanner.Create(groups).Groups[0].RetainedPath);
        Assert.Throws<ArgumentException>(() => DuplicateRetentionPlanner.Create(groups,
            new Dictionary<string, string> { ["HASH"] = @"C:\foreign" }));
    }
}
