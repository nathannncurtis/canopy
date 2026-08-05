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
        string[] paths = BuildPaths(result, cancellationToken);

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
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "scanReport", null);
        await writer.WriteAttributeStringAsync(null, "totalBytes", null, Invariant(result.TotalBytes));
        await writer.WriteAttributeStringAsync(null, "fileCount", null, Invariant(result.FileCount));
        await writer.WriteAttributeStringAsync(null, "directoryCount", null, Invariant(result.DirCount));
        await writer.WriteAttributeStringAsync(null, "elapsedSeconds", null,
            result.ElapsedSec.ToString("R", CultureInfo.InvariantCulture));
        await writer.WriteStartElementAsync(null, "items", null);

        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            await writer.WriteStartElementAsync(null, "item", null);
            await writer.WriteAttributeStringAsync(null, "index", null, Invariant(index));
            await writer.WriteAttributeStringAsync(null, "kind", null,
                IsDirectory(node) ? "directory" : "file");
            await writer.WriteAttributeStringAsync(null, "size", null, Invariant(node.Size));
            await writer.WriteAttributeStringAsync(null, "flags", null, Invariant(node.Flags));
            await writer.WriteElementStringAsync(null, "name", null, result.Names[index]);
            await writer.WriteElementStringAsync(null, "path", null, paths[index]);
            await writer.WriteEndElementAsync();
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static async Task ExportHtmlAsync(
        ScanResultManaged result,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateDestination(destination);
        string[] paths = BuildPaths(result, cancellationToken);
        using var writer = new StreamWriter(
            destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024, leaveOpen: true);

        await WriteAsync(writer, "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>Canopy scan report</title><style>body{font-family:system-ui,sans-serif;margin:2rem}" +
            "table{border-collapse:collapse;width:100%}caption{text-align:left;font-weight:bold;margin:.5rem 0}" +
            "th,td{border:1px solid #777;padding:.35rem;text-align:left}thead{background:#eee}" +
            ".number{text-align:right}</style></head><body><main><h1>Canopy scan report</h1>",
            cancellationToken);
        await WriteAsync(writer, $"<section aria-labelledby=\"summary-heading\"><h2 id=\"summary-heading\">Summary</h2>" +
            $"<dl><dt>Total bytes</dt><dd>{Invariant(result.TotalBytes)}</dd>" +
            $"<dt>Files</dt><dd>{Invariant(result.FileCount)}</dd>" +
            $"<dt>Directories</dt><dd>{Invariant(result.DirCount)}</dd>" +
            $"<dt>Elapsed seconds</dt><dd>{result.ElapsedSec.ToString("R", CultureInfo.InvariantCulture)}</dd>" +
            "</dl></section><table><caption>Scanned files and directories</caption><thead><tr>" +
            "<th scope=\"col\">Index</th><th scope=\"col\">Kind</th><th scope=\"col\">Name</th>" +
            "<th scope=\"col\">Path</th><th scope=\"col\">Bytes</th><th scope=\"col\">Flags</th>" +
            "</tr></thead><tbody>", cancellationToken);

        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            string name = WebUtility.HtmlEncode(result.Names[index]);
            string path = WebUtility.HtmlEncode(paths[index]);
            string kind = IsDirectory(node) ? "Directory" : "File";
            await WriteAsync(writer, $"<tr><td class=\"number\">{Invariant(index)}</td><td>{kind}</td>" +
                $"<td>{name}</td><td>{path}</td><td class=\"number\">{Invariant(node.Size)}</td>" +
                $"<td class=\"number\">{Invariant(node.Flags)}</td></tr>", cancellationToken);
        }

        await WriteAsync(writer, "</tbody></table></main></body></html>\n", cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    static async Task WriteAsync(StreamWriter writer, string value, CancellationToken token) =>
        await writer.WriteAsync(value.AsMemory(), token);

    static string[] BuildPaths(ScanResultManaged result, CancellationToken cancellationToken)
    {
        if (result.Nodes.Length != result.Names.Length)
            throw new ArgumentException("The result must have one name per node.", nameof(result));

        int count = result.Nodes.Length;
        var states = new byte[count];
        var paths = new string?[count];
        var chain = new List<uint>();
        for (uint start = 0; start < count; start++)
        {
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

            string path = current == NoNode ? string.Empty : paths[current]!;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                uint index = chain[i];
                string name = result.Names[index]
                    ?? throw new InvalidDataException("The scan result contains a null node name.");
                path = path.Length == 0 ? name : Path.Combine(path, name);
                paths[index] = path;
                states[index] = 2;
            }
        }
        return paths.Select(path => path!).ToArray();
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
