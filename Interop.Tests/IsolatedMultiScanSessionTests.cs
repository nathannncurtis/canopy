using System.Diagnostics;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class IsolatedMultiScanSessionTests
{
    static string Worker => Path.Combine(AppContext.BaseDirectory, "canopy-isolation-test-worker.exe");

    [Fact]
    public async Task PublishesValidatedSnapshotAndProgress()
    {
        string target = CreateTarget();
        var updates = new List<IsolatedTargetUpdate>();
        try
        {
            await using var session = new IsolatedMultiScanSession(Worker, 1);
            IReadOnlyList<TargetScanOutcome> outcomes = await session.ScanOutcomesAsync([target],
                new ImmediateProgress<IsolatedTargetUpdate>(updates.Add), TestContext.Current.CancellationToken);
            TargetScanOutcome outcome = Assert.Single(outcomes);
            Assert.True(outcome.Succeeded); Assert.Equal((ulong)42, outcome.Result!.TotalBytes);
            Assert.Contains(updates, update => update.Progress.BytesSeen == 42);
            Assert.Contains(updates, update => update.CompletedOutcome?.Succeeded == true);
        }
        finally { Directory.Delete(target, true); }
    }

    [Fact]
    public async Task ConvertsAbnormalExitToTargetFailure()
    {
        string target = CreateTarget("abnormal.exit");
        try
        {
            await using var session = new IsolatedMultiScanSession(Worker, 1);
            TargetScanOutcome outcome = Assert.Single(await session.ScanOutcomesAsync([target], cancellationToken: TestContext.Current.CancellationToken));
            var error = Assert.IsType<IsolatedWorkerException>(outcome.Error); Assert.Equal(23, error.ExitCode);
        }
        finally { Directory.Delete(target, true); }
    }

    [Fact]
    public async Task RejectsOversizedProtocolLine()
    {
        string target = CreateTarget("oversized.line");
        try
        {
            await using var session = new IsolatedMultiScanSession(Worker, 1);
            TargetScanOutcome outcome = Assert.Single(await session.ScanOutcomesAsync([target], cancellationToken: TestContext.Current.CancellationToken));
            Assert.IsType<InvalidDataException>(outcome.Error);
        }
        finally { Directory.Delete(target, true); }
    }

    [Fact]
    public async Task RetainsSuccessfulPartialOutcomeWhenAnotherWorkerCrashes()
    {
        string good = CreateTarget(), bad = CreateTarget("abnormal.exit");
        var updates = new List<IsolatedTargetUpdate>();
        try
        {
            await using var session = new IsolatedMultiScanSession(Worker, 2);
            IReadOnlyList<TargetScanOutcome> outcomes = await session.ScanOutcomesAsync([good, bad],
                new ImmediateProgress<IsolatedTargetUpdate>(updates.Add), TestContext.Current.CancellationToken);
            Assert.Single(outcomes, item => item.Succeeded); Assert.Single(outcomes, item => !item.Succeeded);
            Assert.Contains(updates, item => item.CompletedOutcome?.Succeeded == true);
        }
        finally { Directory.Delete(good, true); Directory.Delete(bad, true); }
    }

    [Fact]
    public async Task CancellationKillsWorkerProcessTree()
    {
        string target = CreateTarget("cancel.tree");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var session = new IsolatedMultiScanSession(Worker, 1);
        Task<IReadOnlyList<TargetScanOutcome>> scan = session.ScanOutcomesAsync([target], cancellationToken: cancellation.Token);
        string pidFile = Path.Combine(target, "child.pid");
        try
        {
            for (int i = 0; i < 100 && !File.Exists(pidFile); i++) await Task.Delay(20, TestContext.Current.CancellationToken);
            string? pidText = null;
            for (int i = 0; i < 100 && pidText is null; i++)
            {
                try { pidText = await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken); }
                catch (IOException) { await Task.Delay(20, TestContext.Current.CancellationToken); }
            }
            int pid = int.Parse(pidText ?? throw new IOException("The worker did not publish its child PID."));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            for (int i = 0; i < 100 && IsRunning(pid); i++) await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.False(IsRunning(pid));
        }
        finally { cancellation.Cancel(); try { await scan; } catch { } Directory.Delete(target, true); }
    }

    static bool IsRunning(int pid) { try { using Process process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    static string CreateTarget(string? marker = null) { string path = Path.Combine(Path.GetTempPath(), "canopy-isolation-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); if (marker is not null) File.WriteAllText(Path.Combine(path, marker), "1"); return path; }
    sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}
