using System.Globalization;
using System.Text;

namespace SizeMonitor.Interop;

public enum ScanSummaryFormat { PlainText, Markdown }

public sealed record ScanSummaryOptions
{
    public ScanSummaryFormat Format { get; init; }
    public bool IncludeSensitivePaths { get; init; }
    public int LargestItemCount { get; init; } = 5;
    public int LargestCategoryCount { get; init; } = 5;
}

/// <summary>Creates concise, deterministic scan summaries suitable for sharing.</summary>
public static class ScanSummaryFormatter
{
    const uint NoNode = uint.MaxValue;

    public static string Format(
        ScanResultManaged result,
        IEnumerable<StorageDistributionBucket>? categories = null,
        ScanSummaryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new ScanSummaryOptions();
        if (options.LargestItemCount < 0 || options.LargestCategoryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Summary item limits cannot be negative.");
        Validate(result, cancellationToken);

        bool markdown = options.Format == ScanSummaryFormat.Markdown;
        var output = new StringBuilder();
        output.AppendLine(markdown ? "# Canopy Scan Summary" : "Canopy scan summary");
        AppendFact(output, markdown, "Total size", Bytes(result.TotalBytes));
        AppendFact(output, markdown, "Files", result.FileCount.ToString("N0", CultureInfo.InvariantCulture));
        AppendFact(output, markdown, "Directories", result.DirCount.ToString("N0", CultureInfo.InvariantCulture));
        AppendFact(output, markdown, "Elapsed", result.ElapsedSec.ToString("0.0", CultureInfo.InvariantCulture) + " s");

        ScanSearchResult[] largest = result.Nodes
            .Select((node, index) => (node, index))
            .Where(item => item.node.Parent != NoNode &&
                           (item.node.Flags & ScanNodeFlags.Directory) == 0)
            .OrderByDescending(item => item.node.Size)
            .ThenBy(item => item.index)
            .Take(options.LargestItemCount)
            .Select(item => new ScanSearchResult(
                (uint)item.index,
                result.Names[item.index],
                options.IncludeSensitivePaths ? BuildPath(result, (uint)item.index) : string.Empty,
                item.node.Size, 0, 0, 0, item.node.Flags))
            .ToArray();

        if (largest.Length > 0)
        {
            output.AppendLine().AppendLine(markdown ? "## Largest items" : "Largest items");
            for (int i = 0; i < largest.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string label = options.IncludeSensitivePaths
                    ? Sanitize(largest[i].RelativePath, markdown)
                    : $"Item #{largest[i].NodeIndex}";
                output.Append(i + 1).Append(". ").Append(label).Append(" — ")
                    .AppendLine(Bytes(largest[i].Size));
            }
        }

        StorageDistributionBucket[] largestCategories = (categories ?? [])
            .OrderByDescending(category => category.Bytes)
            .ThenBy(category => category.Key, StringComparer.Ordinal)
            .Take(options.LargestCategoryCount)
            .ToArray();
        if (largestCategories.Length > 0)
        {
            output.AppendLine().AppendLine(markdown ? "## Largest categories" : "Largest categories");
            for (int i = 0; i < largestCategories.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StorageDistributionBucket category = largestCategories[i];
                output.Append(i + 1).Append(". ").Append(Sanitize(category.Key, markdown))
                    .Append(" — ").Append(Bytes(category.Bytes)).Append(" · ")
                    .Append(category.FileCount.ToString("N0", CultureInfo.InvariantCulture))
                    .AppendLine(" files");
            }
        }

        return output.ToString().TrimEnd() + "\n";
    }

    static void AppendFact(StringBuilder output, bool markdown, string name, string value) =>
        output.Append(markdown ? "- **" : "- ").Append(name).Append(markdown ? ":** " : ": ")
            .AppendLine(value);

    static string BuildPath(ScanResultManaged result, uint index)
    {
        var segments = new Stack<string>();
        uint current = index;
        while (current != NoNode)
        {
            segments.Push(result.Names[current]);
            current = result.Nodes[current].Parent;
        }
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    static string Sanitize(string value, bool markdown)
    {
        string safe = value.Replace('\r', ' ').Replace('\n', ' ');
        return markdown ? safe.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal) : safe;
    }

    static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? bytes.ToString("N0", CultureInfo.InvariantCulture) + " B"
            : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    static void Validate(ScanResultManaged result, CancellationToken cancellationToken)
    {
        if (result.Nodes is null || result.Names is null || result.Nodes.Length != result.Names.Length)
            throw new InvalidDataException("Node and name arrays must be non-null and have equal lengths.");
        var states = new byte[result.Nodes.Length];
        for (int start = 0; start < result.Nodes.Length; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Names[start] is null) throw new InvalidDataException($"Node {start} has a null name.");
            var chain = new List<int>();
            uint current = (uint)start;
            while (current != NoNode)
            {
                if (current >= result.Nodes.Length) throw new InvalidDataException($"Node {start} has an invalid parent index.");
                if (states[current] == 2) break;
                if (states[current] == 1) throw new InvalidDataException("The scan parent topology contains a cycle.");
                states[current] = 1;
                chain.Add((int)current);
                current = result.Nodes[current].Parent;
            }
            foreach (int index in chain) states[index] = 2;
        }
    }
}
