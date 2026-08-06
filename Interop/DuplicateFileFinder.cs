using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SizeMonitor.Interop;

public sealed record DuplicateFileOptions
{
    public long MinimumSize { get; init; }
    public bool IgnoreInaccessible { get; init; } = true;
}

public sealed record DuplicateFileGroup(
    long Size,
    string Sha256,
    IReadOnlyList<string> Paths);

public static class DuplicateFileFinder
{
    const int SampleBytes = 64 * 1024;

    public static async Task<IReadOnlyList<DuplicateFileGroup>> FindAsync(
        IEnumerable<string> roots,
        DuplicateFileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        options ??= new DuplicateFileOptions();
        if (options.MinimumSize < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum size cannot be negative.");

        List<FileCandidate> files = Enumerate(roots, options, cancellationToken);
        var results = new List<DuplicateFileGroup>();
        foreach (IGrouping<long, FileCandidate> sizeGroup in files
                     .GroupBy(file => file.Size)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fastGroups = new Dictionary<ulong, List<FileCandidate>>();
            foreach (FileCandidate file in sizeGroup)
            {
                ulong hash = await FastHashAsync(file.Path, file.Size, cancellationToken).ConfigureAwait(false);
                if (!fastGroups.TryGetValue(hash, out List<FileCandidate>? group))
                    fastGroups.Add(hash, group = []);
                group.Add(file);
            }

            foreach (List<FileCandidate> fastGroup in fastGroups.Values.Where(group => group.Count > 1))
            {
                var verified = new Dictionary<string, List<FileCandidate>>(StringComparer.Ordinal);
                foreach (FileCandidate file in fastGroup)
                {
                    string hash = await FullHashAsync(file.Path, file.Size, cancellationToken).ConfigureAwait(false);
                    if (!verified.TryGetValue(hash, out List<FileCandidate>? group))
                        verified.Add(hash, group = []);
                    group.Add(file);
                }

                results.AddRange(verified
                    .Where(pair => pair.Value.Count > 1)
                    .Select(pair => new DuplicateFileGroup(
                        sizeGroup.Key,
                        pair.Key,
                        pair.Value.Select(file => file.Path)
                            .Order(StringComparer.OrdinalIgnoreCase).ToArray())));
            }
        }

        return results
            .OrderByDescending(group => group.Size)
            .ThenBy(group => group.Sha256, StringComparer.Ordinal)
            .ToArray();
    }

    static List<FileCandidate> Enumerate(
        IEnumerable<string> roots,
        DuplicateFileOptions options,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<FileIdentity>();
        var files = new List<FileCandidate>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = options.IgnoreInaccessible,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            string fullRoot = Path.GetFullPath(root);
            if (File.Exists(fullRoot))
            {
                Add(fullRoot);
                continue;
            }
            if (!Directory.Exists(fullRoot))
                throw new FileNotFoundException($"Duplicate search root does not exist: {fullRoot}", fullRoot);

            foreach (string path in Directory.EnumerateFiles(fullRoot, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(path);
            }
        }
        return files;

        void Add(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!paths.Add(fullPath))
                return;
            var info = new FileInfo(fullPath);
            if (info.Length < options.MinimumSize)
                return;

            FileIdentity? identity = TryGetIdentity(fullPath);
            if (identity is not null && !identities.Add(identity.Value))
                return;
            files.Add(new(fullPath, info.Length));
        }
    }

    static async Task<ulong> FastHashAsync(string path, long expectedSize, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        ulong hash = 14695981039346656037UL;
        hash = Mix(hash, unchecked((ulong)expectedSize));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SampleBytes);
        try
        {
            int first = await stream.ReadAsync(buffer.AsMemory(0, SampleBytes), cancellationToken).ConfigureAwait(false);
            hash = Mix(hash, buffer.AsSpan(0, first));
            if (expectedSize > SampleBytes)
            {
                stream.Seek(Math.Max(SampleBytes, expectedSize - SampleBytes), SeekOrigin.Begin);
                int last = await stream.ReadAsync(buffer.AsMemory(0, SampleBytes), cancellationToken).ConfigureAwait(false);
                hash = Mix(hash, buffer.AsSpan(0, last));
            }
            EnsureLength(stream, expectedSize, path);
            return hash;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async Task<string> FullHashAsync(string path, long expectedSize, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        EnsureLength(stream, expectedSize, path);
        return Convert.ToHexString(hash);
    }

    static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    static void EnsureLength(FileStream stream, long expectedSize, string path)
    {
        if (stream.Length != expectedSize)
            throw new IOException($"File changed while duplicate detection was reading it: {path}");
    }

    static ulong Mix(ulong hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BitConverter.TryWriteBytes(bytes, value);
        return Mix(hash, bytes);
    }

    static ulong Mix(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            hash = (hash ^ value) * 1099511628211UL;
        return hash;
    }

    static FileIdentity? TryGetIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            return null;
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    readonly record struct FileCandidate(string Path, long Size);
    readonly record struct FileIdentity(uint Volume, ulong Index);

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}
