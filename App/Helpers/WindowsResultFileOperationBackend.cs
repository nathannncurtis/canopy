using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SizeMonitor.Interop;

namespace SizeMonitor.Helpers;

public sealed class WindowsResultFileOperationBackend : IResultFileOperationBackend, IResultFileMutation
{
    readonly IResultOperationProcessRunner _process;
    readonly IResultFileTraversal _traversal;
    public WindowsResultFileOperationBackend() : this(new ResultOperationProcessRunner(), new ReparseSafeResultFileTraversal()) { }
    public WindowsResultFileOperationBackend(IResultOperationProcessRunner process, IResultFileTraversal traversal)
    { _process = process; _traversal = traversal; }
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public bool IsDirectory(string path) => Directory.Exists(path);
    public ResultFileSourceSnapshot Capture(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh(); if (!info.Exists) throw new FileNotFoundException("Source is unavailable.", path);
        string identity = StableIdentity(path);
        return new(identity, info is DirectoryInfo, info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc);
    }
    public IReadOnlyList<ResultFileMemberSnapshot> CaptureMembers(string directory) => _traversal.Enumerate(directory)
        .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
        .Select(item => new ResultFileMemberSnapshot(item.RelativePath, Capture(item.FullPath))).ToArray();
    public async Task MoveAsync(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"Destination folder does not exist: {parent}");
        bool caseOnly = source.Equals(destination, StringComparison.OrdinalIgnoreCase) &&
            !source.Equals(destination, StringComparison.Ordinal);
        if (caseOnly)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(source)!, ".canopy-case-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(source))
            { Directory.Move(source, temporary); try { Directory.Move(temporary, destination); } catch { Directory.Move(temporary, source); throw; } }
            else
            { File.Move(source, temporary); try { File.Move(temporary, destination); } catch { File.Move(temporary, source); throw; } }
            return;
        }
        if (!Directory.Exists(source)) { File.Move(source, destination, overwrite: false); return; }
        if (!DirectoryMoveRouting.RequiresStaging(source, destination))
        { Directory.Move(source, destination); return; }
        string staging = destination + ".canopy-stage-" + Guid.NewGuid().ToString("N");
        await StagedDirectoryMove.ExecuteAsync(source, destination, staging, _traversal, this, token);
    }
    public async Task SetCompressionAsync(string path, bool compressed, CancellationToken token)
    {
        string root = Path.GetPathRoot(path) ?? throw new IOException("The target has no volume root.");
        bool supports = new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        ExternalProcessRequest request = NtfsCompressionPlan.Create(path, Directory.Exists(path), compressed, supports);
        (int exitCode, string error) = await _process.RunAsync(request, token);
        if (exitCode != 0) throw new IOException($"NTFS compression failed ({exitCode}): {error}");
    }
    public async Task CreateZipAsync(IReadOnlyList<string> sources, string destination, CancellationToken token)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        try
        {
            await using FileStream stream = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in sources)
            {
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(source))
                {
                    string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                    foreach (ResultTraversalEntry item in _traversal.Enumerate(source).Where(item => !item.IsDirectory && !item.IsReparsePoint))
                    {
                        token.ThrowIfCancellationRequested();
                        string entry = NormalizeEntry(Path.Combine(rootName, item.RelativePath));
                        await AddFileAsync(archive, item.FullPath, Unique(entries, entry), token);
                    }
                }
                else await AddFileAsync(archive, source, Unique(entries, Path.GetFileName(source)), token);
            }
        }
        catch { try { File.Delete(destination); } catch (IOException) { } throw; }
    }
    static async Task AddFileAsync(ZipArchive archive, string path, string entryName, CancellationToken token)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using Stream output = entry.Open(); await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await input.CopyToAsync(output, token);
    }
    static string NormalizeEntry(string value) => value.Replace(Path.DirectorySeparatorChar, '/');
    static string Unique(HashSet<string> entries, string desired)
    {
        if (entries.Add(desired)) return desired;
        string directory = Path.GetDirectoryName(desired)?.Replace('\\', '/') ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(desired), extension = Path.GetExtension(desired);
        for (int suffix = 2; ; suffix++)
        {
            string candidate = (directory.Length == 0 ? string.Empty : directory + "/") + $"{name} ({suffix}){extension}";
            if (entries.Add(candidate)) return candidate;
        }
    }
    static string StableIdentity(string path)
    {
        string extended = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
            path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using SafeFileHandle handle = CreateFile(extended, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Unable to identify '{path}'.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            throw new IOException($"Unable to identify '{path}'.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return $"{info.VolumeSerialNumber:x8}:{index:x16}";
    }
    void IResultFileMutation.CreateDirectory(string path) => Directory.CreateDirectory(path);
    void IResultFileMutation.CopyFile(string source, string destination) => File.Copy(source, destination, false);
    void IResultFileMutation.MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    void IResultFileMutation.DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    bool IResultFileMutation.DirectoryExists(string path) => Directory.Exists(path);
    ResultFileMetadata IResultFileMutation.CaptureMetadata(string path) =>
        new(File.GetAttributes(path), File.GetLastWriteTimeUtc(path));
    void IResultFileMutation.ApplyMetadata(string path, ResultFileMetadata metadata)
    {
        File.SetLastWriteTimeUtc(path, metadata.LastWriteUtc);
        File.SetAttributes(path, metadata.Attributes & ~FileAttributes.ReparsePoint);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

}
