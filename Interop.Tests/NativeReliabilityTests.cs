using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class NativeReliabilityTests
{
    [Fact]
    public void TelemetryLayoutAndConstantsMatchNativeAbi()
    {
        Assert.Equal(0x40ul, (ulong)CoreCapability.ScanTelemetry);
        Assert.Equal(80, Marshal.SizeOf<SmonScanStatusNative>());
        Assert.Equal(16, Marshal.OffsetOf<SmonScanStatusNative>(nameof(SmonScanStatusNative.DirsVisited)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<SmonScanStatusNative>(nameof(SmonScanStatusNative.ChangedItems)).ToInt32());
        Assert.Equal(1u, (uint)ScanPhase.Discovery);
        Assert.Equal(5u, (uint)ScanPhase.Complete);
    }

    [Fact]
    public void SafeHandleRepeatedDisposeReleasesExactlyOnce()
    {
        int releases = 0;
        var handle = new SafeScanHandle((IntPtr)123, _ =>
        {
            Interlocked.Increment(ref releases);
            return true;
        });

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(1, releases);
    }

    [Fact]
    public void SafeHandleFinalizerRecoversAbandonedHandleExactlyOnce()
    {
        var counter = new ReleaseCounter();
        WeakReference abandoned = AbandonHandle(counter);

        for (int i = 0; i < 5 && (abandoned.IsAlive || Volatile.Read(ref counter.Count) == 0); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(abandoned.IsAlive);
        Assert.Equal(1, Volatile.Read(ref counter.Count));
    }

    [Fact]
    public void ProgressCoalescesBurstsAndAlwaysDeliversTerminalState()
    {
        var delivered = new List<ScanProgress>();
        var reporter = new ProgressCoalescer(
            new InlineProgress<ScanProgress>(delivered.Add), TimeSpan.FromSeconds(1));

        reporter.Offer(new(1, 0, 0, ScanPhase.Discovery));
        for (ulong i = 2; i < 100; i++)
            reporter.Offer(new(i, i, i, ScanPhase.Discovery));
        reporter.Offer(new(100, 100, 100, ScanPhase.Complete, true), force: true);

        Assert.Collection(delivered,
            first => Assert.Equal(ScanPhase.Discovery, first.Phase),
            terminal =>
            {
                Assert.Equal(ScanPhase.Complete, terminal.Phase);
                Assert.True(terminal.IsTerminal);
                Assert.Equal(100ul, terminal.BytesSeen);
            });
    }

    [Fact]
    public void ProgressRejectsRegressingPhases()
    {
        var delivered = new List<ScanProgress>();
        var reporter = new ProgressCoalescer(
            new InlineProgress<ScanProgress>(delivered.Add), TimeSpan.FromMilliseconds(10));

        reporter.Offer(new(1, 1, 1, ScanPhase.Metadata), force: true);
        reporter.Offer(new(2, 2, 2, ScanPhase.Discovery), force: true);

        Assert.Single(delivered);
        Assert.Equal(ScanPhase.Metadata, delivered[0].Phase);
    }

    [Fact]
    public void ProgressOptionsRejectUnsafeCallbackRates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanProgressOptions { MinimumInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanProgressOptions { MinimumInterval = TimeSpan.FromMinutes(1) }.Validate());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference AbandonHandle(ReleaseCounter counter)
    {
        var handle = new SafeScanHandle((IntPtr)456, _ =>
        {
            Interlocked.Increment(ref counter.Count);
            return true;
        });
        return new WeakReference(handle);
    }

    sealed class ReleaseCounter { public int Count; }

    sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
