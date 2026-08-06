using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SizeMonitor.Interop;

/// <summary>Streams scan results in stable, portable interchange formats.</summary>
public static class ScanResultExporter
{
    const uint NoNode = uint.MaxValue;
    static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task ExportCsvAsync(
        ScanResultManaged result,
        Stream destination,
        CancellationToken cancellationToken = default,
        bool escapeFormulas = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Validate(result, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var writer = new StreamWriter(destination, Utf8WithoutBom, bufferSize: 16 * 1024, leaveOpen: true)
        {
            NewLine = "\n",
        };

        await writer.WriteLineAsync("index,path,name,size,flags".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        var paths = new string?[result.Nodes.Length];
        var chain = new List<int>();
        for (int index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            string line = string.Concat(
                index.ToString(CultureInfo.InvariantCulture), ",",
                EscapeCsv(BuildPath(result, index, paths, chain), escapeFormulas), ",",
                EscapeCsv(result.Names[index], escapeFormulas), ",",
                node.Size.ToString(CultureInfo.InvariantCulture), ",",
                node.Flags.ToString(CultureInfo.InvariantCulture));
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task ExportJsonAsync(
        ScanResultManaged result,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Validate(result, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var json = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = false });
        json.WriteStartObject();
        json.WriteNumber("totalBytes", result.TotalBytes);
        json.WriteNumber("fileCount", result.FileCount);
        json.WriteNumber("dirCount", result.DirCount);
        json.WriteNumber("elapsedSeconds", result.ElapsedSec);
        json.WriteStartArray("nodes");

        var paths = new string?[result.Nodes.Length];
        var chain = new List<int>();
        for (int index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            json.WriteStartObject();
            json.WriteNumber("index", index);
            json.WriteString("path", BuildPath(result, index, paths, chain));
            json.WriteString("name", result.Names[index]);
            json.WriteNumber("size", node.Size);
            json.WriteNumber("flags", node.Flags);
            json.WriteEndObject();

            if ((index & 0xff) == 0xff)
                await json.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static string EscapeCsv(string value, bool escapeFormulas)
    {
        bool formula = escapeFormulas && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
        if (formula)
            value = "'" + value;

        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return formula ? string.Concat('"', value, '"') : value;

        return string.Concat('"', value.Replace("\"", "\"\"", StringComparison.Ordinal), '"');
    }

    static string BuildPath(
        ScanResultManaged result,
        int nodeIndex,
        string?[] paths,
        List<int> chain)
    {
        if (paths[nodeIndex] is { } cached)
            return cached;

        chain.Clear();
        uint current = (uint)nodeIndex;
        while (current != NoNode && paths[current] is null)
        {
            chain.Add((int)current);
            current = result.Nodes[current].Parent;
        }

        var path = new StringBuilder(current == NoNode ? string.Empty : paths[current]);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            int index = chain[i];
            if (path.Length > 0 && path[^1] is not ('\\' or '/'))
                path.Append(Path.DirectorySeparatorChar);
            path.Append(result.Names[index]);
            if ((result.Nodes[index].Flags & ScanNodeFlags.Directory) != 0)
                paths[index] = path.ToString();
        }

        string resolved = path.ToString();
        if ((result.Nodes[nodeIndex].Flags & ScanNodeFlags.Directory) != 0)
            paths[nodeIndex] = resolved;
        return resolved;
    }

    static void Validate(ScanResultManaged result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Nodes is null || result.Names is null || result.Nodes.Length != result.Names.Length)
            throw new InvalidDataException("Node and name arrays must be non-null and have equal lengths.");

        int count = result.Nodes.Length;
        var states = new byte[count];
        var chain = new List<int>();
        for (int start = 0; start < count; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Names[start] is null)
                throw new InvalidDataException($"Node {start} has a null name.");
            if (states[start] == 2)
                continue;

            chain.Clear();
            uint current = (uint)start;
            while (current != NoNode)
            {
                if (current >= count)
                    throw new InvalidDataException($"Node {start} has an invalid parent index {current}.");
                if (states[current] == 2)
                    break;
                if (states[current] == 1)
                    throw new InvalidDataException($"The parent topology contains a cycle at node {current}.");
                states[current] = 1;
                chain.Add((int)current);
                current = result.Nodes[current].Parent;
            }

            foreach (int index in chain)
                states[index] = 2;
        }
    }
}
