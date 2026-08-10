using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

// 32 bytes, Pack=8 matches the C struct comment in smon_api.h
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ScanNode
{
    public ulong Size;
    public uint  Parent;
    public uint  FirstChild;
    public uint  NextSibling;
    public uint  Flags;
    public uint  NameOffset; // byte offset into name_buf
    public uint  NameLen;    // wchar_t count, not bytes
}

public static class ScanNodeFlags
{
    public const uint Directory = 0x01u;
    public const uint Symlink   = 0x02u;
    public const uint Reparse   = 0x04u;
    public const uint Stream    = 0x08u;
    public const uint CloudPlaceholder = 0x10u;
}

// Mirrors the C ScanResult struct for P/Invoke; must not be copied after Smon_GetResult.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ScanResultNative
{
    public ScanNode* Nodes;
    public uint      NodeCount;
    // 4 bytes implicit padding here on x64 to align the pointer below
    private uint     _pad;
    public char*     NameBuf;
    public ulong     TotalBytes;
    public ulong     FileCount;
    public ulong     DirCount;
    public double    ElapsedSec;
}

public enum ScanPhase : uint
{
    Discovery = 1,
    Metadata = 2,
    Aggregation = 3,
    Finalization = 4,
    Complete = 5,
}

[Flags]
public enum ScanNodeMetadataFlags : uint
{
    None = 0,
    UniqueAllocation = 0x01,
    CycleEdge = 0x02,
    CanonicalLinkOnly = 0x04,
    Compressed = 0x08,
    Sparse = 0x10,
}

public sealed record ScanNodeMetadata(ScanNodeMetadataFlags Flags, uint LinkCount,
    uint VolumeSerial, ulong FileId, ulong LogicalBytes, ulong AllocatedBytes,
    ulong UniquelyAccountedBytes);

[StructLayout(LayoutKind.Sequential)]
internal struct SmonNodeMetadataNative
{
    public uint StructSize;
    public ScanNodeMetadataFlags Flags;
    public uint LinkCount;
    public uint VolumeSerial;
    public ulong FileId;
    public ulong LogicalBytes;
    public ulong AllocatedBytes;
    public ulong UniquelyAccountedBytes;
}

public sealed record ScanProgress(
    ulong DirsVisited,
    ulong FilesVisited,
    ulong BytesSeen,
    ScanPhase Phase = ScanPhase.Discovery,
    bool IsTerminal = false,
    ulong SkippedDirectories = 0,
    ulong SkippedFiles = 0,
    ulong PermissionSkips = 0,
    ulong ErrorSkips = 0,
    ulong ChangedItems = 0);

public sealed record ScanProgressOptions
{
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    internal void Validate()
    {
        if (MinimumInterval < TimeSpan.FromMilliseconds(10) || MinimumInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(MinimumInterval));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SmonScanStatusNative
{
    public uint StructSize;
    public ScanPhase Phase;
    public uint Terminal;
    public uint Reserved;
    public ulong DirsVisited;
    public ulong FilesVisited;
    public ulong BytesSeen;
    public ulong SkippedDirectories;
    public ulong SkippedFiles;
    public ulong PermissionSkips;
    public ulong ErrorSkips;
    public ulong ChangedItems;

    public readonly ScanProgress ToManaged(bool terminal = false) => new(
        DirsVisited, FilesVisited, BytesSeen,
        terminal ? ScanPhase.Complete : Phase,
        terminal || Terminal != 0,
        SkippedDirectories, SkippedFiles, PermissionSkips, ErrorSkips, ChangedItems);
}

public sealed class ScanResultManaged
{
    public required ScanNode[] Nodes      { get; init; }
    public required string[]   Names      { get; init; }
    public ulong               TotalBytes { get; init; }
    public ulong               FileCount  { get; init; }
    public ulong               DirCount   { get; init; }
    public double              ElapsedSec { get; init; }
    /// <summary>Per-node physical-storage metadata. Empty for legacy/imported results.</summary>
    public ScanNodeMetadata?[] Metadata   { get; internal set; } = [];

    public string GetName(uint nodeIndex) => Names[nodeIndex];

    public ScanNodeMetadata? GetMetadata(uint nodeIndex) =>
        nodeIndex < (uint)Metadata.Length ? Metadata[nodeIndex] : null;

    internal static unsafe ScanResultManaged FromNative(ScanResultNative native)
    {
        var nodes = new ScanNode[native.NodeCount];
        if (native.NodeCount > 0 && native.Nodes != null)
        {
            var span = new ReadOnlySpan<ScanNode>(native.Nodes, (int)native.NodeCount);
            span.CopyTo(nodes);
        }

        var names = new string[native.NodeCount];
        for (int i = 0; i < (int)native.NodeCount; i++)
        {
            ref readonly ScanNode n = ref nodes[i];
            if (native.NameBuf != null && n.NameLen > 0)
            {
                // name_offset is a byte offset into a wchar_t buffer; divide by 2 for char index
                char* ptr = (char*)native.NameBuf + (n.NameOffset / sizeof(char));
                names[i] = new string(ptr, 0, (int)n.NameLen);
            }
            else
            {
                names[i] = string.Empty;
            }
        }

        return new ScanResultManaged
        {
            Nodes      = nodes,
            Names      = names,
            TotalBytes = native.TotalBytes,
            FileCount  = native.FileCount,
            DirCount   = native.DirCount,
            ElapsedSec = native.ElapsedSec,
        };
    }
}
