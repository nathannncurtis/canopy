using System.Buffers.Binary;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanSnapshotStoreTests
{
    const uint None = uint.MaxValue;

    [Fact]
    public async Task RoundTripsEveryFieldAndUnicodeNames()
    {
        string path = TempPath();
        try
        {
            ScanResultManaged expected = Result();
            await ScanSnapshotStore.SaveAsync(path, expected, TestContext.Current.CancellationToken);
            ScanResultManaged actual = await ScanSnapshotStore.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(expected.Names, actual.Names);
            Assert.Equal(expected.Nodes, actual.Nodes);
            Assert.Equal(expected.TotalBytes, actual.TotalBytes);
            Assert.Equal(expected.FileCount, actual.FileCount);
            Assert.Equal(expected.DirCount, actual.DirCount);
            Assert.Equal(expected.ElapsedSec, actual.ElapsedSec);
            Assert.Equal(expected.Metadata, actual.Metadata);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ProducesDeterministicBytes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string first = TempPath(), second = TempPath();
        try
        {
            await ScanSnapshotStore.SaveAsync(first, Result(), token);
            await ScanSnapshotStore.SaveAsync(second, Result(), token);
            Assert.Equal(await File.ReadAllBytesAsync(first, token), await File.ReadAllBytesAsync(second, token));
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(50)]
    public async Task RejectsTruncatedFiles(int length)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string complete = TempPath(), truncated = TempPath();
        try
        {
            await ScanSnapshotStore.SaveAsync(complete, Result(), token);
            byte[] bytes = await File.ReadAllBytesAsync(complete, token);
            await File.WriteAllBytesAsync(truncated, bytes[..Math.Min(length, bytes.Length)], token);
            await Assert.ThrowsAsync<InvalidDataException>(() => ScanSnapshotStore.LoadAsync(truncated, token));
        }
        finally { File.Delete(complete); File.Delete(truncated); }
    }

    [Fact]
    public async Task RejectsUnknownVersion()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = TempPath();
        try
        {
            await ScanSnapshotStore.SaveAsync(path, Result(), token);
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 99);
            await File.WriteAllBytesAsync(path, bytes, token);
            await Assert.ThrowsAsync<InvalidDataException>(() => ScanSnapshotStore.LoadAsync(path, token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadsVersionOneSnapshotsWithoutMetadata()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = TempPath();
        try
        {
            await ScanSnapshotStore.SaveAsync(path, Result(), token);
            byte[] current = await File.ReadAllBytesAsync(path, token);
            int position = 48;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(12, 4));
            for (uint i = 0; i < count; i++)
            {
                uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(position + 32, 4));
                position = checked(position + 36 + (int)nameBytes);
            }
            byte[] legacy = current[..position];
            BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(8, 4), 1);
            await File.WriteAllBytesAsync(path, legacy, token);

            ScanResultManaged loaded = await ScanSnapshotStore.LoadAsync(path, token);
            Assert.Equal(Result().Nodes, loaded.Nodes);
            Assert.Empty(loaded.Metadata);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RejectsMalformedTopology()
    {
        ScanResultManaged result = Result();
        result.Nodes[0] = Node(0, 0, 1, None, 0, 0, 2);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ScanSnapshotStore.SaveAsync(TempPath(), result, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsImpossibleNodeCountBeforeAllocatingTables()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = TempPath();
        try
        {
            await ScanSnapshotStore.SaveAsync(path, Result(), token);
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 4_000_000);
            await File.WriteAllBytesAsync(path, bytes, token);
            await Assert.ThrowsAsync<InvalidDataException>(() => ScanSnapshotStore.LoadAsync(path, token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ValidatesVeryDeepParentTopologyWithoutRecursion()
    {
        const int count = 20_000;
        var nodes = new ScanNode[count];
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = new ScanNode
            {
                Parent = i == 0 ? None : (uint)(i - 1),
                FirstChild = i + 1 < count ? (uint)(i + 1) : None,
                NextSibling = None,
            };
            names[i] = "n";
        }
        string path = TempPath();
        try
        {
            await ScanSnapshotStore.SaveAsync(path, new ScanResultManaged { Nodes = nodes, Names = names },
                TestContext.Current.CancellationToken);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RejectsSiblingCyclesAndInconsistentChildLinks()
    {
        ScanResultManaged cycle = Result();
        cycle.Nodes[2] = cycle.Nodes[2] with { NextSibling = 2 };
        await Assert.ThrowsAsync<InvalidDataException>(() => ScanSnapshotStore.SaveAsync(
            TempPath(), cycle, TestContext.Current.CancellationToken));

        ScanResultManaged mismatch = Result();
        mismatch.Nodes[1] = mismatch.Nodes[1] with { FirstChild = None };
        await Assert.ThrowsAsync<InvalidDataException>(() => ScanSnapshotStore.SaveAsync(
            TempPath(), mismatch, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationDoesNotOverwriteTargetOrLeaveTemporaryFile()
    {
        string path = TempPath();
        await File.WriteAllTextAsync(path, "existing", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScanSnapshotStore.SaveAsync(path, Result(), cancellation.Token));
        Assert.Equal("existing", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        File.Delete(path);
    }

    static ScanResultManaged Result() => new()
    {
        Nodes =
        [
            Node(42, None, 1, None, 1, 0, 2),
            Node(42, 0, 2, None, 1, 4, 5),
            Node(42, 1, None, None, 0x1234, 14, 6),
        ],
        Names = [@"C:\", "文書", "résumé.txt"],
        TotalBytes = 42, FileCount = 1, DirCount = 2, ElapsedSec = 1.25,
        Metadata =
        [
            new(ScanNodeMetadataFlags.UniqueAllocation, 1, 7, 10, 42, 42, 42, 133_500_000_000_000_000),
            null,
            new(ScanNodeMetadataFlags.Compressed, 2, 7, 12, 42, 32, 32, 133_600_000_000_000_000),
        ],
    };

    static ScanNode Node(ulong size, uint parent, uint child, uint sibling, uint flags,
        uint nameOffset, uint nameLength) => new()
    {
        Size = size, Parent = parent, FirstChild = child, NextSibling = sibling,
        Flags = flags, NameOffset = nameOffset, NameLen = nameLength,
    };

    static string TempPath() => Path.Combine(Path.GetTempPath(), $"canopy-{Guid.NewGuid():N}.scan");
}
