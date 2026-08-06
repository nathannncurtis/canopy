using System.Text.RegularExpressions;

namespace SizeMonitor.Interop;

public static class ScanResultQuery
{
    const uint NoNode = uint.MaxValue;

    public static IReadOnlyList<ScanSearchResult> Search(
        ScanResultManaged result,
        ScanQuery? query = null,
        CancellationToken cancellationToken = default) =>
        SearchPage(result, query, cancellationToken).Items;

    public static ScanSearchPage SearchPage(
        ScanResultManaged result,
        ScanQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        query ??= new ScanQuery();
        Validate(result, query);

        Regex? regex = CompileRegex(query.RegexPattern);
        HashSet<string> extensions = query.Extensions
            .Select(NormalizeExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var paths = new string?[result.Nodes.Length];
        var depths = new int[result.Nodes.Length];
        var matches = new List<ScanSearchResult>();
        PriorityQueue<ScanSearchResult, ScanSearchResult>? top = query.ResultLimit is not null
            ? new(Comparer<ScanSearchResult>.Create((left, right) => -Compare(left, right, query.Sort)))
            : null;
        int totalMatches = 0;
        for (uint index = 0; index < result.Nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanNode node = result.Nodes[index];
            string name = result.Names[index];
            bool isDirectory = (node.Flags & ScanNodeFlags.Directory) != 0;
            if (!Matches(node, name, isDirectory, query, extensions, regex))
                continue;
            string path = ResolvePath(result, index, paths, depths);
            ulong parentSize = node.Parent == NoNode || result.Nodes[node.Parent].Size == 0
                ? result.TotalBytes
                : result.Nodes[node.Parent].Size;
            var match = new ScanSearchResult(
                index,
                name,
                path,
                node.Size,
                depths[index],
                Percentage(node.Size, parentSize),
                Percentage(node.Size, result.TotalBytes),
                node.Flags);
            totalMatches = checked(totalMatches + 1);
            if (top is null) matches.Add(match);
            else
            {
                top.Enqueue(match, match);
                if (top.Count > query.ResultLimit!.Value) top.Dequeue();
            }
        }

        if (top is not null)
            while (top.TryDequeue(out ScanSearchResult? match, out _)) matches.Add(match);
        return new ScanSearchPage(Sort(matches, query.Sort), totalMatches);
    }

    static int Compare(ScanSearchResult left, ScanSearchResult right, IReadOnlyList<ScanSortTerm> terms)
    {
        foreach (ScanSortTerm term in terms)
        {
            int value = term.Field switch
            {
                ScanSortField.Name => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
                ScanSortField.Path => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath),
                ScanSortField.Size => left.Size.CompareTo(right.Size),
                ScanSortField.Depth => left.Depth.CompareTo(right.Depth),
                ScanSortField.PercentOfParent => left.PercentOfParent.CompareTo(right.PercentOfParent),
                ScanSortField.PercentOfTotal => left.PercentOfTotal.CompareTo(right.PercentOfTotal),
                _ => throw new ArgumentOutOfRangeException(nameof(terms)),
            };
            if (value != 0) return term.Descending ? -value : value;
        }
        return left.NodeIndex.CompareTo(right.NodeIndex);
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
        if (!isDirectory && extensions.Count > 0 && !extensions.Contains(FileNameFacts.ExtensionOf(name))) return false;
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
            ScanSortField.Path => Order(source, subsequent, term.Descending, item => item.RelativePath,
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
            string name = result.Names[nodeIndex];
            path = Path.Join(path, name);
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
        if (query.ResultLimit is <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Result limit must be positive.");
        for (int i = 0; i < result.Nodes.Length; i++)
        {
            uint parent = result.Nodes[i].Parent;
            if (parent != NoNode && parent >= result.Nodes.Length)
                throw new InvalidDataException("The scan result contains an invalid parent index.");
        }
    }

    static Regex? CompileRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            return new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(250));
        }
        catch (NotSupportedException)
        {
            // Preserve valid .NET patterns which use lookarounds/backreferences while
            // retaining the per-match timeout used before the safe engine was added.
            try
            {
                return new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                throw new ScanQueryRegexException(pattern, ex);
            }
        }
        catch (ArgumentException ex)
        {
            throw new ScanQueryRegexException(pattern, ex);
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
