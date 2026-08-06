using System.Buffers.Binary;
using System.Text;

namespace SizeMonitor.Interop;

/// <summary>Persists complete scan results in a bounded, versioned binary format.</summary>
public static class ScanSnapshotStore
{
    static readonly byte[] Magic = "CANOPY\0S"u8.ToArray();
    const uint Version = 1;
    const uint NoNode = uint.MaxValue;
    // Matches the native node-pool ceiling and prevents corrupt files from requesting
    // multi-gigabyte managed arrays before any node data has been verified.
    const int MaxNodes = 4_194_304;
    const int MinimumSerializedNodeBytes = 36;
    const int MaxNameBytes = 16 * 1024 * 1024;
    const long MaxSnapshotBytes = 16L * 1024 * 1024 * 1024;

    public static async Task SaveAsync(string path, ScanResultManaged result,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(result);
        Validate(result.Nodes, result.Names);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WriteAsync(stream, Magic, cancellationToken);
                await WriteUInt32Async(stream, Version, cancellationToken);
                await WriteUInt32Async(stream, checked((uint)result.Nodes.Length), cancellationToken);
                await WriteUInt64Async(stream, result.TotalBytes, cancellationToken);
                await WriteUInt64Async(stream, result.FileCount, cancellationToken);
                await WriteUInt64Async(stream, result.DirCount, cancellationToken);
                await WriteUInt64Async(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits(result.ElapsedSec)), cancellationToken);

                for (int i = 0; i < result.Nodes.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScanNode node = result.Nodes[i];
                    await WriteUInt64Async(stream, node.Size, cancellationToken);
                    await WriteUInt32Async(stream, node.Parent, cancellationToken);
                    await WriteUInt32Async(stream, node.FirstChild, cancellationToken);
                    await WriteUInt32Async(stream, node.NextSibling, cancellationToken);
                    await WriteUInt32Async(stream, node.Flags, cancellationToken);
                    await WriteUInt32Async(stream, node.NameOffset, cancellationToken);
                    await WriteUInt32Async(stream, node.NameLen, cancellationToken);
                    byte[] name = Encoding.UTF8.GetBytes(result.Names[i]);
                    if (name.Length > MaxNameBytes) throw new InvalidDataException("A node name is too large.");
                    await WriteUInt32Async(stream, checked((uint)name.Length), cancellationToken);
                    await WriteAsync(stream, name, cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<ScanResultManaged> LoadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxSnapshotBytes) throw new InvalidDataException("The scan snapshot is too large.");

        byte[] magic = new byte[Magic.Length];
        await ReadExactlyAsync(stream, magic, cancellationToken);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a Canopy scan snapshot.");
        uint version = await ReadUInt32Async(stream, cancellationToken);
        if (version != Version) throw new InvalidDataException($"Unsupported scan snapshot version {version}.");
        uint rawCount = await ReadUInt32Async(stream, cancellationToken);
        if (rawCount > MaxNodes) throw new InvalidDataException("The scan snapshot contains too many nodes.");
        int count = checked((int)rawCount);
        ulong totalBytes = await ReadUInt64Async(stream, cancellationToken);
        ulong fileCount = await ReadUInt64Async(stream, cancellationToken);
        ulong dirCount = await ReadUInt64Async(stream, cancellationToken);
        double elapsed = BitConverter.Int64BitsToDouble(unchecked((long)await ReadUInt64Async(stream, cancellationToken)));
        if (!double.IsFinite(elapsed) || elapsed < 0) throw new InvalidDataException("The elapsed time is invalid.");
        long minimumNodeBytes = checked((long)count * MinimumSerializedNodeBytes);
        if (stream.Length - stream.Position < minimumNodeBytes)
            throw new InvalidDataException("The scan snapshot node count exceeds the available data.");

        var nodes = new ScanNode[count];
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes[i] = new ScanNode
            {
                Size = await ReadUInt64Async(stream, cancellationToken),
                Parent = await ReadUInt32Async(stream, cancellationToken),
                FirstChild = await ReadUInt32Async(stream, cancellationToken),
                NextSibling = await ReadUInt32Async(stream, cancellationToken),
                Flags = await ReadUInt32Async(stream, cancellationToken),
                NameOffset = await ReadUInt32Async(stream, cancellationToken),
                NameLen = await ReadUInt32Async(stream, cancellationToken),
            };
            uint byteCount = await ReadUInt32Async(stream, cancellationToken);
            if (byteCount > MaxNameBytes) throw new InvalidDataException("A node name is too large.");
            byte[] bytes = new byte[checked((int)byteCount)];
            await ReadExactlyAsync(stream, bytes, cancellationToken);
            try { names[i] = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("A node name is not valid UTF-8.", ex); }
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("The scan snapshot has trailing data.");
        Validate(nodes, names);
        return new ScanResultManaged
        {
            Nodes = nodes, Names = names, TotalBytes = totalBytes, FileCount = fileCount,
            DirCount = dirCount, ElapsedSec = elapsed,
        };
    }

    static void Validate(ScanNode[]? nodes, string[]? names)
    {
        if (nodes is null || names is null || nodes.Length != names.Length)
            throw new InvalidDataException("Node and name counts do not match.");
        if (nodes.Length > MaxNodes) throw new InvalidDataException("The scan contains too many nodes.");
        var states = new byte[nodes.Length];
        var chain = new List<int>();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (names[i] is null) throw new InvalidDataException("A node name is null.");
            CheckLink(nodes[i].FirstChild, nodes.Length, "first-child");
            CheckLink(nodes[i].NextSibling, nodes.Length, "next-sibling");
            ValidateParentChain(i);
        }

        // Validate parent chains iteratively so a valid but extremely deep tree cannot
        // overflow the managed stack.
        void ValidateParentChain(int start)
        {
            chain.Clear();
            int index = start;
            while (index >= 0 && states[index] != 2)
            {
                if (states[index] == 1) throw new InvalidDataException("The parent topology contains a cycle.");
                states[index] = 1;
                chain.Add(index);
                uint parent = nodes[index].Parent;
                CheckLink(parent, nodes.Length, "parent");
                index = parent == NoNode ? -1 : checked((int)parent);
            }
            foreach (int item in chain) states[item] = 2;
        }

        // NextSibling is a directed graph with at most one outgoing edge. Validate it
        // independently, including orphaned links that are not reachable from a root.
        Array.Clear(states);
        for (int start = 0; start < nodes.Length; start++)
        {
            int index = start;
            chain.Clear();
            while (index >= 0 && states[index] != 2)
            {
                if (states[index] == 1) throw new InvalidDataException("The sibling topology contains a cycle.");
                states[index] = 1;
                chain.Add(index);
                uint sibling = nodes[index].NextSibling;
                index = sibling == NoNode ? -1 : checked((int)sibling);
            }
            foreach (int item in chain) states[item] = 2;
        }

        // Ensure child lists agree with Parent and do not claim one node twice.
        var claimed = new bool[nodes.Length];
        for (int parent = 0; parent < nodes.Length; parent++)
        {
            uint child = nodes[parent].FirstChild;
            while (child != NoNode)
            {
                int childIndex = checked((int)child);
                if (claimed[childIndex]) throw new InvalidDataException("A node appears more than once in child lists.");
                if (nodes[childIndex].Parent != (uint)parent)
                    throw new InvalidDataException("A child link does not agree with its parent link.");
                claimed[childIndex] = true;
                child = nodes[childIndex].NextSibling;
            }
        }
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i].Parent != NoNode && !claimed[i])
                throw new InvalidDataException("A parented node is missing from its parent's child list.");
    }

    static void CheckLink(uint link, int count, string label)
    {
        if (link != NoNode && link >= count) throw new InvalidDataException($"A {label} index is out of range.");
    }

    static async ValueTask WriteUInt32Async(Stream stream, uint value, CancellationToken token)
    {
        byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        await WriteAsync(stream, bytes, token);
    }
    static async ValueTask WriteUInt64Async(Stream stream, ulong value, CancellationToken token)
    {
        byte[] bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        await WriteAsync(stream, bytes, token);
    }
    static ValueTask WriteAsync(Stream stream, byte[] bytes, CancellationToken token) => stream.WriteAsync(bytes, token);
    static async ValueTask<uint> ReadUInt32Async(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[4]; await ReadExactlyAsync(stream, bytes, token);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
    static async ValueTask<ulong> ReadUInt64Async(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[8]; await ReadExactlyAsync(stream, bytes, token);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
    static async ValueTask ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        try { await stream.ReadExactlyAsync(buffer, token); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("The scan snapshot is truncated.", ex); }
    }
}
