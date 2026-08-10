using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SizeMonitor.Interop;

public sealed class WindowsCleanupFileSystem : ICleanupFileSystem
{
    public CleanupTargetSnapshot Capture(string path)
    {
        string full = Path.GetFullPath(path); FileAttributes attributes = File.GetAttributes(full);
        bool directory = attributes.HasFlag(FileAttributes.Directory);
        using SafeFileHandle handle = CreateFile(ToExtendedLengthPath(full), 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, directory ? 0x02000000u : 0u, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        ulong size = directory ? 0 : ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        DateTimeOffset write = directory ? Directory.GetLastWriteTimeUtc(full) : File.GetLastWriteTimeUtc(full);
        return new(full, new(info.VolumeSerialNumber.ToString("X8"), id.ToString("X16")),
            directory ? CleanupTargetType.Directory : CleanupTargetType.File, size, write,
            attributes.HasFlag(FileAttributes.ReparsePoint), LinkCount: info.NumberOfLinks);
    }
    public IEnumerable<CleanupTargetSnapshot> EnumerateTree(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            CleanupTargetSnapshot item = Capture(path); yield return item;
            if (item.Type == CleanupTargetType.Directory && !item.IsReparsePoint)
                foreach (CleanupTargetSnapshot child in EnumerateTree(path)) yield return child;
        }
    }
    internal static string ToExtendedLengthPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential)] struct ByHandleFileInformation { public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime; public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}

public interface IWindowsShellDeleteAdapter { (int Error, bool Aborted) Recycle(string path); }

public sealed class WindowsShellDeleteAdapter : IWindowsShellDeleteAdapter
{
    public (int Error, bool Aborted) Recycle(string path)
    {
        var operation = new ShFileOp { Func = 3, From = path + "\0\0", Flags = 0x0040 | 0x0010 | 0x0400 };
        int error = SHFileOperation(ref operation); return (error, operation.Aborted != 0);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ShFileOp { public IntPtr Window; public uint Func; [MarshalAs(UnmanagedType.LPWStr)] public string From; [MarshalAs(UnmanagedType.LPWStr)] public string? To; public ushort Flags; public int Aborted; public IntPtr Mappings; [MarshalAs(UnmanagedType.LPWStr)] public string? Title; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref ShFileOp operation);
}

public sealed class WindowsCleanupMutationBackend(IWindowsShellDeleteAdapter? shell = null) : ICleanupMutationBackend
{
    readonly IWindowsShellDeleteAdapter _shell = shell ?? new WindowsShellDeleteAdapter();
    public ValueTask<CleanupMutationResult> MutateAsync(CleanupTargetSnapshot target, CleanupMutationMode disposition, bool recursive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposition == CleanupMutationMode.Recoverable) return ValueTask.FromResult(new CleanupMutationResult(false, 0, "Use the app-owned recovery-vault backend."));
        if (disposition == CleanupMutationMode.Recycle)
        {
            (int code, bool aborted) = _shell.Recycle(target.CanonicalPath);
            return ValueTask.FromResult(code == 0 && !aborted
                ? new CleanupMutationResult(true, 0, "Moved to the Windows Recycle Bin; this does not immediately free disk space. Restore through Windows because Canopy cannot safely identify a shell restore record.")
                : new CleanupMutationResult(false, 0, aborted ? "Windows shell operation was aborted." : $"Windows shell error {code}."));
        }
        if (target.Type == CleanupTargetType.Directory)
        {
            if (target.IsReparsePoint || !recursive) Directory.Delete(target.CanonicalPath, false);
            else DeleteTreeWithoutFollowingReparsePoints(target.CanonicalPath, token);
        }
        else File.Delete(target.CanonicalPath);
        return ValueTask.FromResult(new CleanupMutationResult(true, 0, "Deleted successfully; freed disk space was not measured."));
    }
    public ValueTask<bool> UndoAsync(string token, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    static void DeleteTreeWithoutFollowingReparsePoints(string directory, CancellationToken token)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested(); FileAttributes attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)) DeleteTreeWithoutFollowingReparsePoints(entry, token);
            else if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(entry, false);
            else { if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly); File.Delete(entry); }
        }
        Directory.Delete(directory, false);
    }
}
