using System.Runtime.InteropServices;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class InteropContractTests
{
    [Fact]
    public void ScanNodeLayoutMatchesNativeAbi()
    {
        Assert.Equal(32, Marshal.SizeOf<ScanNode>());
        Assert.Equal(0, Marshal.OffsetOf<ScanNode>(nameof(ScanNode.Size)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<ScanNode>(nameof(ScanNode.Parent)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<ScanNode>(nameof(ScanNode.NameLen)).ToInt32());
    }

    [Fact]
    public void PublicConstantsMatchNativeAbi()
    {
        Assert.Equal(0u, (uint)ScannerKind.Unknown);
        Assert.Equal(1u, (uint)ScannerKind.Mft);
        Assert.Equal(2u, (uint)ScannerKind.Directory);
        Assert.Equal(0x01u, ScanNodeFlags.Directory);
        Assert.Equal(0x02u, ScanNodeFlags.Symlink);
        Assert.Equal(0x04u, ScanNodeFlags.Reparse);
        Assert.Equal(0x08u, ScanNodeFlags.Stream);
        Assert.Equal(0x10u, ScanNodeFlags.CloudPlaceholder);
        Assert.Equal(1u, CoreCapabilities.ExpectedAbiVersion);
        Assert.Equal(0x01ul, (ulong)CoreCapability.MftScanner);
        Assert.Equal(0x08ul, (ulong)CoreCapability.Avx2Assembly);
        Assert.Equal(0x10ul, (ulong)CoreCapability.ScanOptions);
        Assert.Equal(0x80ul, (ulong)CoreCapability.Arm64Intrinsics);
        Assert.Equal(0x100ul, (ulong)CoreCapability.RouteInfo);
        Assert.Equal(0x200ul, (ulong)CoreCapability.NodeMetadata);
        Assert.Equal(0x400ul, (ulong)CoreCapability.BulkNodeMetadata);
        Assert.Equal(24, Marshal.SizeOf<SmonCapabilitiesNative>());
        Assert.Equal(8, Marshal.OffsetOf<SmonCapabilitiesNative>(nameof(SmonCapabilitiesNative.Flags)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<SmonCapabilitiesNative>(nameof(SmonCapabilitiesNative.MaxNodes)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<SmonCapabilitiesNative>(nameof(SmonCapabilitiesNative.MaxNameBytes)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 72 : 64, Marshal.SizeOf<SmonScanOptionsNative>());
        Assert.Equal(24, Marshal.SizeOf<SmonRouteInfoNative>());
        Assert.Equal(56, Marshal.SizeOf<SmonNodeMetadataNative>());
        Assert.Equal(48, Marshal.OffsetOf<SmonNodeMetadataNative>(nameof(SmonNodeMetadataNative.LastWriteFileTime)).ToInt32());
    }

    [Theory]
    [InlineData(@"C:\data", @"C:\data")]
    [InlineData(@"C:\data\", @"C:\data")]
    public void MultiScanNormalizesTargetPaths(string input, string expected) =>
        Assert.Equal(expected, MultiScanSession.NormalizeTarget(input), ignoreCase: true);

    [Fact]
    public void MultiScanRejectsBareDriveSpecification() =>
        Assert.Throws<ArgumentException>(() => MultiScanSession.NormalizeTarget("C:"));

    [Fact]
    public void RejectsMismatchedNativeAbi()
    {
        CoreCapabilities.ValidateCompatibility(CoreCapabilities.ExpectedAbiVersion);
        Assert.Throws<CoreCompatibilityException>(() =>
            CoreCapabilities.ValidateCompatibility(CoreCapabilities.ExpectedAbiVersion + 1));
    }

    [Fact]
    public void MultiScanRequiresPositiveConcurrency() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiScanSession(0));

    [Fact]
    public async Task MultiScanRejectsEmptyTargetSet()
    {
        await using var session = new MultiScanSession();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.ScanAsync([], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultiScanRejectsWorkAfterDisposal()
    {
        var session = new MultiScanSession();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ScanAsync(["."], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultiScanValidatesOptionsBeforeStartingTargets()
    {
        await using var session = new MultiScanSession();
        var options = new ScanOptions
        {
            MinimumFileSize = 2,
            MaximumFileSize = 1,
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.ScanAsync(["."], options: options,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MultiScanCompatibilitySurfaceThrowsForEveryFailedTarget()
    {
        TargetScanOutcome[] outcomes =
        [
            new(@"C:\denied", ScannerKind.Directory, null, new UnauthorizedAccessException("denied")),
            new(@"D:\missing", ScannerKind.Unknown, null, new DirectoryNotFoundException("missing")),
        ];

        AggregateException error = Assert.Throws<AggregateException>(() =>
            MultiScanSession.MaterializeSuccessfulOutcomes(outcomes));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains(error.InnerExceptions, item => item.Message.Contains(@"C:\denied", StringComparison.Ordinal));
        Assert.Contains(error.InnerExceptions, item => item.Message.Contains(@"D:\missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultiScanPublishesOnlyAnAdmittedRunAndDrainsIt()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IEnumerable<string> BlockingTargets()
        {
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            yield break;
        }

        await using var session = new MultiScanSession();
        Task<IReadOnlyList<TargetScanOutcome>> admitted = session.ScanOutcomesAsync(BlockingTargets(),
            cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Task<IReadOnlyList<TargetScanOutcome>> rejected = session.ScanOutcomesAsync([], cancellationToken:
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected);
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<ArgumentException>(() => admitted);

        await Assert.ThrowsAsync<ArgumentException>(() => session.ScanOutcomesAsync([], cancellationToken:
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NativeCallbackProgressContainsReporterExceptions()
    {
        var progress = new MultiScanSession.CallbackProgress<int>(
            _ => throw new InvalidOperationException("reporter failure"));

        Exception? escaped = Record.Exception(() => progress.Report(1));

        Assert.Null(escaped);
    }

    [Fact]
    public void TerminalMultiScanProgressContainsReporterExceptions()
    {
        int deliveries = 0;
        var progress = new ThrowingProgress<TargetScanProgress>(() => deliveries++);
        var terminal = new TargetScanProgress(@"C:\", new ScanProgress(1, 2, 3), 1, 1);

        Exception? escaped = Record.Exception(() => MultiScanSession.ReportSafely(progress, terminal));

        Assert.Null(escaped);
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task MultiScanCancelAllowsSynchronousCallbackReentry()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IEnumerable<string> BlockingTargets()
        {
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            yield return ".";
        }

        await using var session = new MultiScanSession();
        Task<IReadOnlyList<TargetScanOutcome>> run = session.ScanOutcomesAsync(BlockingTargets(),
            cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            bool callbackReentered = false;
            using CancellationTokenRegistration registration = session.ActiveRunCancellationToken.Register(() =>
            {
                session.Pause();
                callbackReentered = true;
            });

            Task cancel = Task.Run(session.Cancel, TestContext.Current.CancellationToken);
            await cancel.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(callbackReentered);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void MultiScanControlsIgnoreOnlyDisposedSnapshots()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Dispose();
        using var scan = new ScanSession();
        scan.Dispose();

        Exception? completionRace = Record.Exception(() =>
        {
            MultiScanSession.InvokeSnapshotControl(cancellation.Cancel);
            MultiScanSession.InvokeSnapshotControl(scan.Pause);
            MultiScanSession.InvokeSnapshotControl(scan.Resume);
            MultiScanSession.InvokeSnapshotControl(scan.Cancel);
        });
        Assert.Null(completionRace);

        var nativeFailure = new InvalidOperationException("native control failure");
        InvalidOperationException escaped = Assert.Throws<InvalidOperationException>(() =>
            MultiScanSession.InvokeSnapshotControl(() => throw nativeFailure));
        Assert.Same(nativeFailure, escaped);
    }

    [Fact]
    public void ScanExceptionPreservesNativeError()
    {
        var exception = new ScanException(5);

        Assert.Equal(5u, exception.NativeError);
        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void StructuredErrorInfoLayoutMatchesNativeAbi()
    {
        Assert.Equal(2076, Marshal.SizeOf<SmonErrorInfoNative>());
        Assert.Equal(28, Marshal.OffsetOf<SmonErrorInfoNative>(nameof(SmonErrorInfoNative.Path)).ToInt32());
        var details = new ScanErrorInfo(5, ScanErrorCategory.Access, ScanErrorStage.Open, 2, 5, @"C:\denied");
        var exception = new ScanException(5, errorInfo: details);
        Assert.Equal(details, exception.ErrorInfo);
        Assert.Contains(@"C:\denied", exception.Message);
    }

    [Fact]
    public void ScanOptionsTranslateToNativeContract()
    {
        var options = new ScanOptions
        {
            MaximumDepth = 3,
            WorkerThreads = 4,
            MinimumFileSize = 10,
            MaximumFileSize = 20,
            IncludeHidden = false,
            IncludeAlternateStreams = true,
            FollowReparsePoints = true,
            StayOnVolume = false,
            NetworkWorkerThreads = 3,
            NetworkRetryCount = 2,
            NetworkRetryDelay = TimeSpan.FromMilliseconds(250),
            ForceDirectoryScanner = true,
            ExcludedPatterns = ["cache*", "obj\\*"],
            ExcludedExtensions = ["tmp", ".log"],
        };

        options.Validate();
        SmonScanOptionsNative native = options.ToNative((IntPtr)1, (IntPtr)2);

        Assert.Equal((uint)Marshal.SizeOf<SmonScanOptionsNative>(), native.StructSize);
        Assert.Equal(3u, native.MaxDepth);
        Assert.Equal(4u, native.WorkerThreads);
        Assert.Equal(10ul, native.MinimumFileSize);
        Assert.Equal(20ul, native.MaximumFileSize);
        Assert.True(native.Flags.HasFlag(SmonScanOptionFlags.ExcludeHidden));
        Assert.True(native.Flags.HasFlag(SmonScanOptionFlags.ForceDirectoryScanner));
        Assert.True(native.Flags.HasFlag(SmonScanOptionFlags.IncludeAlternateStreams));
        Assert.True(native.Flags.HasFlag(SmonScanOptionFlags.FollowReparsePoints));
        Assert.True(native.Flags.HasFlag(SmonScanOptionFlags.AllowCrossVolume));
        Assert.Equal(1u, native.TraversalPolicyVersion);
        Assert.Equal(3u, native.NetworkWorkerThreads);
        Assert.Equal(2u, native.NetworkRetryCount);
        Assert.Equal(250u, native.NetworkRetryDelayMilliseconds);
        Assert.Equal("cache*;obj\\*", options.BuildExcludedPatternList());
        Assert.Equal("tmp;.log", options.BuildExcludedExtensionList());
    }

    [Fact]
    public void ScanOptionsRejectInvalidRangesAndSeparators()
    {
        Assert.Throws<ArgumentException>(() => new ScanOptions
        {
            MinimumFileSize = 2,
            MaximumFileSize = 1,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions
        {
            NetworkWorkerThreads = 17,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions
        {
            NetworkRetryCount = 6,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions
        {
            WorkerThreads = 33,
        }.Validate());
        Assert.Throws<ArgumentException>(() => new ScanOptions
        {
            ExcludedPatterns = ["one;two"],
        }.Validate());
        Assert.Throws<ArgumentException>(() => new ScanOptions
        {
            ExcludedPatterns = ["one\0two"],
        }.Validate());
        Assert.Throws<ArgumentException>(() => new ScanOptions
        {
            ExcludedExtensions = ["temp*"],
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions
        {
            MaximumDepth = 0,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions
        {
            MaximumFileSize = 0,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions
        {
            WorkerThreads = 0,
        }.Validate());
        Assert.Throws<ArgumentException>(() => new ScanOptions
        {
            IncludeReparsePoints = false,
            FollowReparsePoints = true,
        }.Validate());
    }

    [Fact]
    public void ScanOptionsNormalizeWildcardExtensions()
    {
        var options = new ScanOptions { ExcludedExtensions = ["*.tmp", "*.LOG"] };

        options.Validate();

        Assert.Equal(".tmp;.LOG", options.BuildExcludedExtensionList());
    }

    [Fact]
    public async Task ManagedNodeMetadataQueriesCompletedResultAndRejectsOutOfRange()
    {
        string root = Path.Combine(Path.GetTempPath(), $"canopy-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "item.bin"), "payload",
            TestContext.Current.CancellationToken);
        try
        {
            ScanSession session;
            try
            {
                session = ScanSession.Start(root, null,
                    new ScanOptions { ForceDirectoryScanner = true });
            }
            catch (DllNotFoundException)
            {
                Assert.Skip("Native metadata integration requires a built Canopy.Core.dll on the DLL path.");
                return;
            }
            using (session)
            {
                ScanResultManaged result = await session.WaitAsync(TestContext.Current.CancellationToken);

                ScanNodeMetadata metadata = Assert.IsType<ScanNodeMetadata>(session.GetNodeMetadata(0));
                Assert.True(metadata.LinkCount >= 1);
                Assert.Equal(result.Nodes[0].Size, metadata.AllocatedBytes);
                Assert.Null(session.GetNodeMetadata(uint.MaxValue));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VolumeStorageInfoCalculatesUsedCapacityAndSerialText()
    {
        VolumeStorageInfo info = VolumeStorageInfo.FromRaw(
            "C:\\", "Data", "NTFS", 0x1234abcd, 4096, 10_000, 2_500);

        Assert.Equal(7_500ul, info.UsedBytes);
        Assert.Equal(2_500ul, info.FreeBytes);
        Assert.Equal("1234-ABCD", info.SerialNumberText);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VolumeStorageInfo.FromRaw("C:\\", "", "NTFS", 0, 4096, 1, 2));
    }

    [Fact]
    public void VolumeStorageInfoReadsTheTestVolume()
    {
        VolumeStorageInfo info = VolumeStorageInfo.Read(Path.GetTempPath());

        Assert.NotEmpty(info.RootPath);
        Assert.NotEmpty(info.FileSystem);
        Assert.True(info.ClusterSize > 0);
        Assert.Equal(info.TotalBytes, info.UsedBytes + info.FreeBytes);
    }

    sealed class ThrowingProgress<T>(Action beforeThrow) : IProgress<T>
    {
        public void Report(T value)
        {
            beforeThrow();
            throw new InvalidOperationException("reporter failure");
        }
    }
}
