using System.Text.RegularExpressions;

namespace SizeMonitor.Interop;

public static class ScanResultQuery
{
    const uint NoNode = uint.MaxValue;

    public static IReadOnlyList<ScanSearchResult> Search(
        ScanResultManaged result,
        ScanQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        query ??= new ScanQuery();
        Validate(result, query);

        Regex? regex = string.IsNullOrWhiteSpace(query.RegexPattern)
            ? null
            : new Regex(query.RegexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        HashSet<string> extensions = query.Extensions
            .Select(NormalizeExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var paths = new string?[result.Nodes.Length];
        var depths = new int[result.Nodes.Length];
        var matches = new List<ScanSearchResult>();
        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            ScanNode node = result.Nodes[index];
            string name = result.Names[index];
            bool isDirectory = (node.Flags & ScanNodeFlags.Directory) != 0;
            if (!Matches(node, name, isDirectory, query, extensions, regex))
                continue;

            string path = ResolvePath(result, index, paths, depths);
            ulong parentSize = node.Parent == NoNode ? result.TotalBytes : result.Nodes[node.Parent].Size;
            matches.Add(new ScanSearchResult(
                index,
                name,
                path,
                node.Size,
                depths[index],
                Percentage(node.Size, parentSize),
                Percentage(node.Size, result.TotalBytes),
                node.Flags));
        }

        return Sort(matches, query.Sort);
    }

    static bool Matches(
        ScanNode node,
        string name,
        bool isDirectory,
        ScanQuery query,
        HashSet<string> extensions,
        Regex? regex)
    {
        ScanItemKinds kind = isDirectory ? ScanItemKinds.Directories : ScanItemKinds.Files;
        if ((query.Kinds & kind) == 0) return false;
        if ((node.Flags & query.RequiredFlags) != query.RequiredFlags) return false;
        if ((node.Flags & query.ExcludedFlags) != 0) return false;
        if (query.MinimumSize is ulong minimum && node.Size < minimum) return false;
        if (query.MaximumSize is ulong maximum && node.Size > maximum) return false;
        if (!string.IsNullOrWhiteSpace(query.Text) &&
            !name.Contains(query.Text, StringComparison.OrdinalIgnoreCase)) return false;
        if (regex is not null && !regex.IsMatch(name)) return false;
        if (!isDirectory && extensions.Count > 0 && !extensions.Contains(Path.GetExtension(name))) return false;
        if (isDirectory && extensions.Count > 0) return false;
        return true;
    }

    static IReadOnlyList<ScanSearchResult> Sort(
        IEnumerable<ScanSearchResult> matches,
        IReadOnlyList<ScanSortTerm> terms)
    {
        IOrderedEnumerable<ScanSearchResult>? ordered = null;
        foreach (ScanSortTerm term in terms)
        {
            ordered = ApplySort(ordered ?? matches, ordered is not null, term);
        }

        ordered ??= matches.OrderBy(match => match.NodeIndex);
        return ordered.ThenBy(match => match.NodeIndex).ToArray();
    }

    static IOrderedEnumerable<ScanSearchResult> ApplySort(
        IEnumerable<ScanSearchResult> source,
        bool subsequent,
        ScanSortTerm term)
    {
        return term.Field switch
        {
            ScanSortField.Name => Order(source, subsequent, term.Descending, item => item.Name,
                StringComparer.OrdinalIgnoreCase),
            ScanSortField.Path => Order(source, subsequent, term.Descending, item => item.Path,
                StringComparer.OrdinalIgnoreCase),
            ScanSortField.Size => Order(source, subsequent, term.Descending, item => item.Size),
            ScanSortField.Depth => Order(source, subsequent, term.Descending, item => item.Depth),
            ScanSortField.PercentOfParent => Order(source, subsequent, term.Descending,
                item => item.PercentOfParent),
            ScanSortField.PercentOfTotal => Order(source, subsequent, term.Descending,
                item => item.PercentOfTotal),
            _ => throw new ArgumentOutOfRangeException(nameof(term)),
        };
    }

    static IOrderedEnumerable<ScanSearchResult> Order<TKey>(
        IEnumerable<ScanSearchResult> source,
        bool subsequent,
        bool descending,
        Func<ScanSearchResult, TKey> selector,
        IComparer<TKey>? comparer = null)
    {
        if (subsequent)
        {
            var ordered = (IOrderedEnumerable<ScanSearchResult>)source;
            return descending
                ? ordered.ThenByDescending(selector, comparer)
                : ordered.ThenBy(selector, comparer);
        }
        return descending
            ? source.OrderByDescending(selector, comparer)
            : source.OrderBy(selector, comparer);
    }

    static string ResolvePath(
        ScanResultManaged result,
        uint index,
        string?[] paths,
        int[] depths)
    {
        if (paths[index] is not null) return paths[index]!;

        var chain = new List<uint>();
        var visited = new HashSet<uint>();
        uint current = index;
        while (current != NoNode && paths[current] is null)
        {
            if (!visited.Add(current))
                throw new InvalidDataException("The scan result contains a parent cycle.");
            chain.Add(current);
            current = result.Nodes[current].Parent;
        }

        string path = current == NoNode ? string.Empty : paths[current]!;
        int depth = current == NoNode ? -1 : depths[current];
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            uint nodeIndex = chain[i];
            path = path.Length == 0 ? result.Names[nodeIndex] : Path.Combine(path, result.Names[nodeIndex]);
            depth++;
            paths[nodeIndex] = path;
            depths[nodeIndex] = depth;
        }
        return paths[index]!;
    }

    static void Validate(ScanResultManaged result, ScanQuery query)
    {
        if (result.Nodes.Length != result.Names.Length)
            throw new ArgumentException("The result must have one name per node.", nameof(result));
        if (query.MinimumSize > query.MaximumSize)
            throw new ArgumentException("Minimum size cannot exceed maximum size.", nameof(query));
        for (int i = 0; i < result.Nodes.Length; i++)
        {
            uint parent = result.Nodes[i].Parent;
            if (parent != NoNode && parent >= result.Nodes.Length)
                throw new InvalidDataException("The scan result contains an invalid parent index.");
        }
    }

    static string NormalizeExtension(string extension)
    {
        string trimmed = extension.Trim();
        return trimmed.Length == 0 || trimmed[0] == '.' ? trimmed : $".{trimmed}";
    }

    static double Percentage(ulong value, ulong total) =>
        total == 0 ? 0 : 100d * value / total;
}
