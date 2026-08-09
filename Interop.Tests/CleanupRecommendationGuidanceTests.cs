using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class CleanupRecommendationGuidanceTests
{
    [Fact]
    public void RevealsLargestFindingsDeterministicallyAndNavigates()
    {
        CleanupPreview preview = Preview();
        var navigator = new CleanupFindingNavigator(preview);

        Assert.Equal(2u, navigator.RevealNextLargest()!.NodeIndex);
        Assert.Equal(3u, navigator.RevealNextLargest()!.NodeIndex);
        Assert.Equal(1u, navigator.RevealNextLargest()!.NodeIndex);
        Assert.False(navigator.CanRevealNext);
        Assert.Null(navigator.RevealNextLargest());
        Assert.Equal(3u, navigator.MovePrevious()!.NodeIndex);
        navigator.Reset();
        Assert.Null(navigator.Current);
        Assert.Equal(3, navigator.RemainingCount);
    }

    [Fact]
    public void ExplanationIsPrivacySafeAndStatesRiskReasonsAndLimitations()
    {
        CleanupPreviewItem item = Preview().Items[1];

        CleanupRecommendationExplanation safe = CleanupRecommendationExplainer.Explain(item);
        CleanupRecommendationExplanation sensitive = CleanupRecommendationExplainer.Explain(item, includeSensitivePath: true);

        Assert.DoesNotContain("Alice", safe.Title);
        Assert.Contains("scan item #2", safe.Summary);
        Assert.Contains("high-risk", safe.Summary);
        Assert.Contains("cache-rule", safe.Reasons[^1]);
        Assert.Contains(safe.Limitations, text => text.Contains("no file has been deleted", StringComparison.Ordinal));
        Assert.Contains(@"C:\Users\Alice\cache.bin", sensitive.Title);
    }

    [Fact]
    public void EqualSizesUseRiskThenNodeIndexTieBreakers()
    {
        CleanupPreview preview = Preview();
        var navigator = new CleanupFindingNavigator(preview);

        CleanupPreviewItem first = navigator.RevealNextLargest()!;
        Assert.Equal(CleanupRisk.High, first.Risk);
        Assert.Equal(2u, first.NodeIndex);
    }

    static CleanupPreview Preview()
    {
        CleanupPreviewItem[] items =
        [
            new(1, @"C:\temp.log", 10, CleanupRisk.Low, ["logs"], ["Old log"]),
            new(2, @"C:\Users\Alice\cache.bin", 20, CleanupRisk.High, ["cache-rule"], ["Cache location"]),
            new(3, @"C:\large.tmp", 20, CleanupRisk.High, ["temp-rule"], ["Temporary data"]),
        ];
        return new(items, 50, 1, 0, 2);
    }
}
