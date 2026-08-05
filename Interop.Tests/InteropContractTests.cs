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
        Assert.Equal(1u, CoreCapabilities.ExpectedAbiVersion);
        Assert.Equal(0x01ul, (ulong)CoreCapability.MftScanner);
        Assert.Equal(0x08ul, (ulong)CoreCapability.Avx2Assembly);
        Assert.Equal(24, Marshal.SizeOf<SmonCapabilitiesNative>());
    }

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
    public void ScanExceptionPreservesNativeError()
    {
        var exception = new ScanException(5);

        Assert.Equal(5u, exception.NativeError);
        Assert.NotEmpty(exception.Message);
    }
}
