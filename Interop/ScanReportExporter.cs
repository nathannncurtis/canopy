using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;

namespace SizeMonitor.Interop;

public static class ScanReportExporter
{
    const uint NoNode = uint.MaxValue;

    public static async Task ExportXmlAsync(
        ScanResultManaged result,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateDestination(destination);
        ValidateResult(result, cancellationToken);

        var settings = new XmlWriterSettings
        {
            Async = true,
            CloseOutput = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineChars = "\n",
        };
        using XmlWriter writer = XmlWriter.Create(destination, settings);
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "scanReport", null).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "totalBytes", null, Invariant(result.TotalBytes)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "fileCount", null, Invariant(result.FileCount)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "directoryCount", null, Invariant(result.DirCount)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "elapsedSeconds", null,
            result.ElapsedSec.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "items", null).ConfigureAwait(false);

        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            await writer.WriteStartElementAsync(null, "item", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "index", null, Invariant(index)).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "kind", null,
                IsDirectory(node) ? "directory" : "file").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "size", null, Invariant(node.Size)).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "flags", null, Invariant(node.Flags)).ConfigureAwait(false);
            await writer.WriteElementStringAsync(null, "name", null, SanitizeXml(result.Names[index])).ConfigureAwait(false);
            await writer.WriteElementStringAsync(null, "path", null, SanitizeXml(BuildPath(result, index))).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static async Task ExportHtmlAsync(
        ScanResultManaged result,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateDestination(destination);
        ValidateResult(result, cancellationToken);
        using var writer = new StreamWriter(
            destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024, leaveOpen: true);

        await WriteAsync(writer, "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>Canopy scan report</title><style>body{font-family:system-ui,sans-serif;margin:2rem}" +
            "table{border-collapse:collapse;width:100%}caption{text-align:left;font-weight:bold;margin:.5rem 0}" +
            "th,td{border:1px solid #777;padding:.35rem;text-align:left}thead{background:#eee}" +
            ".number{text-align:right}</style></head><body><main><h1>Canopy scan report</h1>",
            cancellationToken).ConfigureAwait(false);
        await WriteAsync(writer, $"<section aria-labelledby=\"summary-heading\"><h2 id=\"summary-heading\">Summary</h2>" +
            $"<dl><dt>Total bytes</dt><dd>{Invariant(result.TotalBytes)}</dd>" +
            $"<dt>Files</dt><dd>{Invariant(result.FileCount)}</dd>" +
            $"<dt>Directories</dt><dd>{Invariant(result.DirCount)}</dd>" +
            $"<dt>Elapsed seconds</dt><dd>{result.ElapsedSec.ToString("R", CultureInfo.InvariantCulture)}</dd>" +
            "</dl></section><table><caption>Scanned files and directories</caption><thead><tr>" +
            "<th scope=\"col\">Index</th><th scope=\"col\">Kind</th><th scope=\"col\">Name</th>" +
            "<th scope=\"col\">Path</th><th scope=\"col\">Bytes</th><th scope=\"col\">Flags</th>" +
            "</tr></thead><tbody>", cancellationToken).ConfigureAwait(false);

        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            string name = WebUtility.HtmlEncode(result.Names[index]);
            string path = WebUtility.HtmlEncode(BuildPath(result, index));
            string kind = IsDirectory(node) ? "Directory" : "File";
            await WriteAsync(writer, $"<tr><td class=\"number\">{Invariant(index)}</td><td>{kind}</td>" +
                $"<td>{name}</td><td>{path}</td><td class=\"number\">{Invariant(node.Size)}</td>" +
                $"<td class=\"number\">{Invariant(node.Flags)}</td></tr>", cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync(writer, "</tbody></table></main></body></html>\n", cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static Task WriteAsync(StreamWriter writer, string value, CancellationToken token) =>
        writer.WriteAsync(value.AsMemory(), token);

    static void ValidateResult(ScanResultManaged result, CancellationToken cancellationToken)
    {
        if (result.Nodes is null || result.Names is null || result.Nodes.Length != result.Names.Length)
            throw new ArgumentException("The result must have one name per node.", nameof(result));

        int count = result.Nodes.Length;
        var states = new byte[count];
        var chain = new List<uint>();
        for (uint start = 0; start < count; start++)
        {
            if (result.Names[start] is null)
                throw new InvalidDataException("The scan result contains a null node name.");
            cancellationToken.ThrowIfCancellationRequested();
            if (states[start] == 2) continue;
            chain.Clear();
            uint current = start;
            while (current != NoNode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current >= count)
                    throw new InvalidDataException("The scan result contains an invalid parent index.");
                if (states[current] == 2)
                    break;
                if (states[current] == 1)
                    throw new InvalidDataException("The scan result contains a parent cycle.");
                states[current] = 1;
                chain.Add(current);
                current = result.Nodes[current].Parent;
            }

            for (int i = chain.Count - 1; i >= 0; i--)
            {
                uint index = chain[i];
                states[index] = 2;
            }
        }
    }

    // Construct only the current row's path. This keeps peak retained path memory at
    // O(depth) rather than O(node count), which matters for multi-million-node reports.
    static string BuildPath(ScanResultManaged result, uint index)
    {
        var names = new List<string>();
        uint current = index;
        while (current != NoNode)
        {
            names.Add(result.Names[current]);
            current = result.Nodes[current].Parent;
        }
        var builder = new StringBuilder();
        for (int i = names.Count - 1; i >= 0; i--)
        {
            if (builder.Length > 0 && builder[^1] != Path.DirectorySeparatorChar)
                builder.Append(Path.DirectorySeparatorChar);
            builder.Append(names[i]);
        }
        return builder.ToString();
    }

    static string SanitizeXml(string value)
    {
        StringBuilder? sanitized = null;
        int consumed = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            bool valid = rune.Value is 0x9 or 0xA or 0xD or >= 0x20 and <= 0xD7FF
                or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF;
            if (!valid)
            {
                sanitized ??= new StringBuilder(value.Length).Append(value.AsSpan(0, consumed));
                sanitized.Append('\uFFFD');
            }
            else if (sanitized is not null)
            {
                sanitized.Append(rune);
            }
            consumed += rune.Utf16SequenceLength;
        }
        return sanitized?.ToString() ?? value;
    }

    static void ValidateDestination(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
    }

    static bool IsDirectory(ScanNode node) => (node.Flags & ScanNodeFlags.Directory) != 0;
    static string Invariant(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    static string Invariant(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
