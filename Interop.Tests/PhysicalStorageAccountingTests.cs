using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class PhysicalStorageAccountingTests
{
    [Fact]
    public void VolumeEnrichmentRequiresExactlyOneLocalVolume()
    {
        Assert.Equal(@"C:\", PhysicalStorageContext.SingleLocalVolumeRoot([@"C:\one", @"C:\two"]));
        Assert.Null(PhysicalStorageContext.SingleLocalVolumeRoot([@"C:\one", @"D:\two"]));
        Assert.Null(PhysicalStorageContext.SingleLocalVolumeRoot([@"\\server\share\one"]));
        Assert.Null(PhysicalStorageContext.SingleLocalVolumeRoot([]));
        Assert.Equal([@"C:\", @"D:\"],
            PhysicalStorageContext.AllVolumeRoots([@"C:\one", @"D:\two"]));
    }

    [Fact]
    public void AccountingSeparatesLogicalAllocatedSavingsAndUnaccountedSpace()
    {
        ScanResultManaged result = Result(
            new ScanNode { Size = 4096, Flags = 0 },
            new ScanNodeMetadata(ScanNodeMetadataFlags.UniqueAllocation | ScanNodeMetadataFlags.Compressed, 1, 1, 1,
                16_384, 4096, 4096));
        VolumeStorageInfo volume = VolumeStorageInfo.FromRaw("C:\\", "", "NTFS", 1,
            4096, 100_000, 20_000, reservedBytes: 8192, systemManagedUnavailableBytes: 4096);

        PhysicalStorageAccounting accounting = PhysicalStorageAccounting.Calculate(result, volume);

        Assert.Equal(16_384UL, accounting.LogicalBytes);
        Assert.Equal(4096UL, accounting.AllocatedBytes);
        Assert.Equal(12_288UL, accounting.CompressionSavingsBytes);
        Assert.Equal(75, accounting.CompressionSavingsPercent);
        Assert.Equal(0UL, accounting.AllocationOverheadBytes);
        Assert.Equal(75_904UL, accounting.UnaccountedUsedBytes);
        Assert.Equal(8192UL, accounting.ReservedBytes);
        Assert.Equal(4096UL, accounting.SystemManagedUnavailableBytes);
    }

    [Fact]
    public void AccountingIsNonnegativeHonestForPartialAndLegacyResults()
    {
        ScanResultManaged result = Result(new ScanNode { Size = 8192, Flags = 0 }, null);
        VolumeStorageInfo volume = VolumeStorageInfo.FromRaw("C:\\", "", "NTFS", 1,
            4096, 10_000, 5000);

        PhysicalStorageAccounting accounting = PhysicalStorageAccounting.Calculate(result, volume, isPartial: true);

        Assert.Equal(0UL, accounting.UnaccountedUsedBytes);
        Assert.Null(accounting.ReservedBytes);
        Assert.True(accounting.IsPartial);
    }

    [Fact]
    public void SparseAndHardlinkAliasesDoNotMasqueradeAsCompression()
    {
        var result = new ScanResultManaged
        {
            Nodes = [new() { Size = 4096 }, new() { Size = 4096 }, new() { Size = 4096 }],
            Names = ["sparse", "canonical", "alias"],
            Metadata =
            [
                new(ScanNodeMetadataFlags.UniqueAllocation | ScanNodeMetadataFlags.Sparse, 1, 1, 1, 1_000_000, 4096, 4096),
                new(ScanNodeMetadataFlags.UniqueAllocation, 2, 1, 2, 8192, 4096, 4096),
                new(ScanNodeMetadataFlags.None, 2, 1, 2, 8192, 4096, 0),
            ],
        };

        PhysicalStorageAccounting accounting = PhysicalStorageAccounting.Calculate(result);

        Assert.Equal(0UL, accounting.CompressionSavingsBytes);
        Assert.Equal(995_904UL, accounting.SparseSavingsBytes);
        Assert.Equal(1_008_192UL, accounting.LogicalBytes);
        Assert.Equal(8192UL, accounting.AllocatedBytes);
    }

    [Fact]
    public void DiskSpaceInformationAbiAndHresultConversionMatchWindowsSdk()
    {
        Assert.Equal(96, System.Runtime.InteropServices.Marshal.SizeOf<VolumeNative.DiskSpaceInformation>());
        Assert.Equal(88, System.Runtime.InteropServices.Marshal.OffsetOf<VolumeNative.DiskSpaceInformation>(
            nameof(VolumeNative.DiskSpaceInformation.SectorsPerAllocationUnit)).ToInt32());
        Assert.Equal(92, System.Runtime.InteropServices.Marshal.OffsetOf<VolumeNative.DiskSpaceInformation>(
            nameof(VolumeNative.DiskSpaceInformation.BytesPerSector)).ToInt32());
        var value = new VolumeNative.DiskSpaceInformation
        {
            TotalReservedAllocationUnits = 3,
            ActualPoolUnavailableAllocationUnits = 2,
            SectorsPerAllocationUnit = 8,
            BytesPerSector = 512,
        };

        Assert.Equal((12_288UL, 8192UL), VolumeNative.ConvertReservation(0, value));
        Assert.Equal((null, null), VolumeNative.ConvertReservation(unchecked((int)0x80004005), value));
        value.TotalReservedAllocationUnits = ulong.MaxValue;
        Assert.Equal(ulong.MaxValue, VolumeNative.ConvertReservation(0, value).Reserved);
    }

    [Theory]
    [InlineData(100, 25, 0)]
    [InlineData(100, 75, 1)]
    [InlineData(100, 99, 2)]
    [InlineData(100, 100, 3)]
    [InlineData(100, 105, 4)]
    [InlineData(100, 125, 5)]
    [InlineData(100, 175, 6)]
    public void AllocationDivergenceMapsToDocumentedColorBuckets(
        ulong logical, ulong allocated, int expected) =>
        Assert.Equal(expected, AllocationOverheadColors.Bucket(logical, allocated));

    [Fact]
    public void DirectoryLogicalAndAllocatedTotalsMatchDescendants()
    {
        var result = new ScanResultManaged
        {
            Nodes =
            [
                new() { Flags = ScanNodeFlags.Directory, Parent = uint.MaxValue, FirstChild = 1, NextSibling = uint.MaxValue },
                new() { Size = 4096, Parent = 0, FirstChild = uint.MaxValue, NextSibling = 2 },
                new() { Size = 8192, Parent = 0, FirstChild = uint.MaxValue, NextSibling = uint.MaxValue },
            ],
            Names = ["root", "compressed", "rounded"],
            Metadata =
            [
                null,
                new(ScanNodeMetadataFlags.UniqueAllocation, 1, 1, 1, 16_384, 4096, 4096),
                new(ScanNodeMetadataFlags.UniqueAllocation, 1, 1, 2, 5000, 8192, 8192),
            ],
        };

        PhysicalStorageMetadata.RollUpDirectories(result);

        Assert.Equal(21_384UL, result.Metadata[0]!.LogicalBytes);
        Assert.Equal(12_288UL, result.Metadata[0]!.AllocatedBytes);
    }

    static ScanResultManaged Result(ScanNode node, ScanNodeMetadata? metadata) => new()
    {
        Nodes = [node],
        Names = ["file"],
        Metadata = [metadata],
        TotalBytes = node.Size,
    };
}
