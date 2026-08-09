using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class CleanupQueueTests
{
    [Fact]
    public async Task PersistsNormalizedNotesTagsAndMarkForLaterEntry()
    {
        string directory = TemporaryDirectory(), file = Path.Combine(directory, "queue.json");
        try
        {
            var store = new CleanupQueueStore(file);
            CleanupQueueDocument saved = await store.AddOrUpdateAsync(new CleanupQueueEntry
            {
                Path = Path.Combine(directory, "item.tmp"), EstimatedBytes = 42, Risk = CleanupRisk.Low,
                Note = "  review later  ", Tags = [" Cache ", "cache", "safe"],
            }, TestContext.Current.CancellationToken);
            CleanupQueueEntry item = Assert.Single(saved.Items);
            Assert.Equal("review later", item.Note); Assert.Equal(["Cache", "safe"], item.Tags);
            CleanupQueueEntry loaded = Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Items);
            Assert.Equal(item.Path, loaded.Path); Assert.Equal((ulong)42, loaded.EstimatedBytes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UpdatesExistingPathInsteadOfDuplicatingIt()
    {
        string directory = TemporaryDirectory();
        try
        {
            var store = new CleanupQueueStore(Path.Combine(directory, "queue.json")); string itemPath = Path.Combine(directory, "same");
            await store.AddOrUpdateAsync(new CleanupQueueEntry { Path = itemPath, Note = "first" }, TestContext.Current.CancellationToken);
            CleanupQueueDocument result = await store.AddOrUpdateAsync(new CleanupQueueEntry { Path = itemPath.ToUpperInvariant(), Note = "second" }, TestContext.Current.CancellationToken);
            Assert.Equal("second", Assert.Single(result.Items).Note);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void BlocksWindowsAndProgramPaths()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        CleanupSafetyAssessment result = CleanupSafetyPolicy.Evaluate(Path.Combine(windows, "System32", "config"), 1, 100);
        Assert.True(result.IsBlocked); Assert.Contains(result.Warnings, warning => warning.Contains("cannot be queued", StringComparison.Ordinal));
    }

    [Fact]
    public void WarnsForProfileAndApplicationData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        CleanupSafetyAssessment result = CleanupSafetyPolicy.Evaluate(Path.Combine(appData, "Vendor", "cache"), 1, 100);
        Assert.False(result.IsBlocked); Assert.Contains(result.Warnings, warning => warning.Contains("application data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfigurableThresholdProducesExactTypedConfirmationPhrase()
    {
        string directory = TemporaryDirectory();
        try
        {
            var store = new CleanupQueueStore(Path.Combine(directory, "queue.json"));
            await store.SetThresholdAsync(100, TestContext.Current.CancellationToken);
            await store.AddOrUpdateAsync(new CleanupQueueEntry { Path = Path.Combine(directory, "a"), EstimatedBytes = 60 }, TestContext.Current.CancellationToken);
            CleanupQueueDocument document = await store.AddOrUpdateAsync(new CleanupQueueEntry { Path = Path.Combine(directory, "b"), EstimatedBytes = 50 }, TestContext.Current.CancellationToken);
            CleanupSafetyAssessment plan = CleanupQueueStore.AssessPlan(document);
            Assert.True(plan.RequiresTypedConfirmation); Assert.Equal("DELETE 2 ITEMS", plan.RequiredConfirmation);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RemoveIsPersistentAndStoreNeverDeletesTarget()
    {
        string directory = TemporaryDirectory(), target = Path.Combine(directory, "target.txt"); File.WriteAllText(target, "keep");
        try
        {
            var store = new CleanupQueueStore(Path.Combine(directory, "queue.json"));
            await store.AddOrUpdateAsync(new CleanupQueueEntry { Path = target, EstimatedBytes = 4 }, TestContext.Current.CancellationToken);
            CleanupQueueDocument document = await store.RemoveAsync(target, TestContext.Current.CancellationToken);
            Assert.Empty(document.Items); Assert.True(File.Exists(target)); Assert.Equal("keep", File.ReadAllText(target));
        }
        finally { Directory.Delete(directory, true); }
    }

    static string TemporaryDirectory() { string path = Path.Combine(Path.GetTempPath(), "canopy-cleanup-queue-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
