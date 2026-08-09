using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class StorageMonitoringTests
{
    [Fact]
    public void FreeSpaceCrossingTriggersOnceRecoversAndRearms()
    {
        var evaluator = new FreeSpaceAlertEvaluator(new(
            MinimumFreeBytes: 100, MinimumFreePercent: 10,
            RecoveryHysteresisBytes: 20, RecoveryHysteresisPercent: 2,
            Cooldown: TimeSpan.Zero));

        Assert.Null(evaluator.Evaluate(Volume(200, 1_000), Utc(0)));
        FreeSpaceAlert? low = evaluator.Evaluate(Volume(90, 1_000), Utc(1));
        Assert.NotNull(low);
        Assert.False(low.IsRecovery);
        Assert.Null(evaluator.Evaluate(Volume(80, 1_000), Utc(2)));
        Assert.Null(evaluator.Evaluate(Volume(110, 1_000), Utc(3)));
        FreeSpaceAlert? recovery = evaluator.Evaluate(Volume(130, 1_000), Utc(4));
        Assert.NotNull(recovery);
        Assert.True(recovery.IsRecovery);
        Assert.NotNull(evaluator.Evaluate(Volume(90, 1_000), Utc(5)));
    }

    [Fact]
    public void FreeSpaceCooldownSuppressesRapidRetriggerAfterRecovery()
    {
        var evaluator = new FreeSpaceAlertEvaluator(new(MinimumFreeBytes: 100,
            RecoveryHysteresisBytes: 10, Cooldown: TimeSpan.FromHours(1)));
        Assert.NotNull(evaluator.Evaluate(Volume(90, 1_000), Utc(0)));
        Assert.NotNull(evaluator.Evaluate(Volume(120, 1_000), Utc(1)));
        Assert.Null(evaluator.Evaluate(Volume(90, 1_000), Utc(2)));
        Assert.NotNull(evaluator.Evaluate(Volume(90, 1_000), Utc(61)));
    }

    [Fact]
    public void GrowthRequiresBaselineAbsoluteAndRateThresholds()
    {
        var policy = new DirectoryGrowthPolicy(100, 50, 25);
        var start = new ScanTrendSample(Utc(0), 100);

        Assert.NotNull(DirectoryGrowthAlertEvaluator.Evaluate("C:\\logs", start,
            new(Utc(60), 160), policy));
        Assert.Null(DirectoryGrowthAlertEvaluator.Evaluate("C:\\logs", start,
            new(Utc(60), 140), policy));
        Assert.Null(DirectoryGrowthAlertEvaluator.Evaluate("C:\\logs",
            new(Utc(0), 50), new(Utc(60), 500), policy));
        Assert.Null(DirectoryGrowthAlertEvaluator.Evaluate("C:\\logs", start,
            new(Utc(180), 160), policy));
    }

    [Fact]
    public async Task DriveHistoryPersistsEventsAndAppliesPerDriveRetention()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "history.json");
        var store = new DriveHistoryStore(path, new(MaximumPointsPerDrive: 2));
        await store.AppendAsync(new("C:\\", Utc(1), 1_000, 500), Token);
        await store.AppendAsync(new("C:\\", Utc(2), 1_000, 400, true), Token);
        await store.AppendAsync(new("C:\\", Utc(3), 1_000, 300), Token);
        await store.AppendAsync(new("D:\\", Utc(3), 2_000, 1_000), Token);

        var loaded = new DriveHistoryStore(path, new(MaximumPointsPerDrive: 2));
        await loaded.LoadAsync(Token);

        Assert.Equal([400ul, 300ul], loaded.GetDrive("c:\\").Select(point => point.FreeBytes));
        Assert.True(loaded.GetDrive("C:\\")[0].ThresholdEvent);
        Assert.Single(loaded.GetDrive("D:\\"));
    }

    [Fact]
    public async Task DriveHistoryRejectsCorruptOrOversizedLogicalValues()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "history.json");
        await File.WriteAllTextAsync(path,
            "{\"Version\":1,\"Points\":[{\"RootPath\":\"C:\\\\\",\"CapturedUtc\":\"2026-01-01T00:00:00Z\",\"TotalBytes\":1,\"FreeBytes\":2,\"ThresholdEvent\":false}]}", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new DriveHistoryStore(path).LoadAsync(Token));
    }

    [Fact]
    public async Task FolderMonitorCoalescesChangesAndPeriodicallyReconciles()
    {
        using var temp = new TempDirectory();
        ulong measured = 10;
        var samples = new List<FolderMonitorSample>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new ContinuousFolderMonitor(temp.Path,
            (_, _) => Task.FromResult(Volatile.Read(ref measured)),
            new(PollInterval: TimeSpan.FromMilliseconds(100),
                CoalescingWindow: TimeSpan.FromMilliseconds(40)),
            new(0, 5, 1));
        var progress = new InlineProgress<FolderMonitorSample>(sample =>
        {
            lock (samples)
            {
                samples.Add(sample);
                if (samples.Any(item => item.Reason == FolderMonitorReason.Initial) &&
                    samples.Any(item => item.Reason == FolderMonitorReason.FileSystemChange) &&
                    samples.Any(item => item.Reason == FolderMonitorReason.PeriodicReconciliation))
                    received.TrySetResult();
            }
        });

        await monitor.StartAsync(progress, Token);
        Volatile.Write(ref measured, 30);
        for (int i = 0; i < 20; i++)
            await File.WriteAllTextAsync(Path.Combine(temp.Path, $"change-{i}.txt"), "x", Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await monitor.StopAsync();

        FolderMonitorSample[] snapshot;
        lock (samples) snapshot = samples.ToArray();
        Assert.Equal(FolderMonitorReason.Initial, snapshot[0].Reason);
        Assert.Contains(snapshot, sample => sample.Reason == FolderMonitorReason.FileSystemChange);
        Assert.Contains(snapshot, sample => sample.Reason == FolderMonitorReason.PeriodicReconciliation);
        Assert.True(snapshot.Length < 20, "A burst must be coalesced instead of producing one scan per event.");
        Assert.Contains(snapshot, sample => sample.GrowthAlert is not null);
    }

    [Fact]
    public async Task DriveMonitorPublishesHistoryAndStopsOnCancellation()
    {
        using var temp = new TempDirectory();
        var history = new DriveHistoryStore(Path.Combine(temp.Path, "drives.json"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var updates = new List<DriveMonitorUpdate>();
        ulong free = 150;
        var service = new DriveMonitorService(["C:\\"], history,
            new(MinimumFreeBytes: 100, Cooldown: TimeSpan.Zero),
            TimeSpan.FromMilliseconds(100), _ => Volume(Interlocked.Add(ref free, unchecked((ulong)-60)), 1_000));
        var progress = new InlineProgress<DriveMonitorUpdate>(update =>
        {
            updates.Add(update);
            if (updates.Count == 2) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(progress, cancellation.Token));

        Assert.Equal(2, updates.Count);
        Assert.Equal(2, history.GetDrive("C:\\").Count);
        Assert.NotNull(updates[0].Alert);
        Assert.Null(updates[1].Alert);
    }

    static VolumeStorageInfo Volume(ulong free, ulong total) =>
        VolumeStorageInfo.FromRaw("C:\\", "Data", "NTFS", 1, 4096, total, free);
    static DateTimeOffset Utc(int minutes) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canopy-monitor-{Guid.NewGuid():N}");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
