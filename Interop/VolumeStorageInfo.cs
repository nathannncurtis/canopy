using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SizeMonitor.Interop;

public sealed record VolumeStorageInfo(
    string RootPath,
    string Label,
    string FileSystem,
    uint SerialNumber,
    ulong ClusterSize,
    ulong TotalBytes,
    ulong UsedBytes,
    ulong FreeBytes)
{
    public string SerialNumberText => $"{SerialNumber >> 16:X4}-{SerialNumber & 0xffff:X4}";

    public static VolumeStorageInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = new StringBuilder(512);
        if (!VolumeNative.GetVolumePathName(path, root, root.Capacity))
            throw CreateException("resolve the volume", path);

        if (!VolumeNative.GetDiskFreeSpaceEx(root.ToString(), out _,
                out ulong totalBytes, out ulong freeBytes))
            throw CreateException("read volume capacity", root.ToString());

        var label = new StringBuilder(256);
        var fileSystem = new StringBuilder(64);
        if (!VolumeNative.GetVolumeInformation(root.ToString(), label, label.Capacity,
                out uint serialNumber, out _, out _, fileSystem, fileSystem.Capacity))
            throw CreateException("read volume identity", root.ToString());

        if (!VolumeNative.GetDiskFreeSpace(root.ToString(), out uint sectorsPerCluster,
                out uint bytesPerSector, out _, out _))
            throw CreateException("read cluster size", root.ToString());

        ulong clusterSize = checked((ulong)sectorsPerCluster * bytesPerSector);
        return FromRaw(root.ToString(), label.ToString(), fileSystem.ToString(),
            serialNumber, clusterSize, totalBytes, freeBytes);
    }

    internal static VolumeStorageInfo FromRaw(
        string rootPath,
        string label,
        string fileSystem,
        uint serialNumber,
        ulong clusterSize,
        ulong totalBytes,
        ulong freeBytes)
    {
        if (freeBytes > totalBytes)
            throw new ArgumentOutOfRangeException(nameof(freeBytes),
                "Free space cannot exceed total capacity.");
        return new(rootPath, label, fileSystem, serialNumber, clusterSize,
            totalBytes, totalBytes - freeBytes, freeBytes);
    }

    static Win32Exception CreateException(string operation, string path) =>
        new(Marshal.GetLastPInvokeError(), $"Could not {operation} for '{path}'.");
}

static class VolumeNative
{
    const string Kernel32 = "kernel32.dll";

    [DllImport(Kernel32, EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathName(
        string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport(Kernel32, EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport(Kernel32, EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport(Kernel32, EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpace(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);
}
