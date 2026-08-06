namespace SizeMonitor.Interop;

public readonly record struct ScanBreadcrumb(uint NodeIndex, string Name, string Path);

/// <summary>Builds validated paths and lookups from a scan result's parent topology.</summary>
public sealed class ScanNavigationIndex
{
    const uint NoNode = uint.MaxValue;
    readonly ScanResultManaged _result;
    readonly string[] _paths;
    readonly Dictionary<string, uint> _pathLookup = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _ambiguousPaths = new(StringComparer.OrdinalIgnoreCase);

    public ScanNavigationIndex(ScanResultManaged result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Nodes is null || result.Names is null || result.Nodes.Length != result.Names.Length)
            throw new InvalidDataException("The scan node and name tables must have the same length.");

        _result = result;
        _paths = new string[result.Nodes.Length];
        var states = new byte[result.Nodes.Length];
        for (uint i = 0; i < result.Nodes.Length; i++)
            Validate(i, states);
        var chain = new List<int>();
        for (uint i = 0; i < result.Nodes.Length; i++)
            BuildPath(i, chain);
    }

    public int Count => _paths.Length;

    public string GetPath(uint nodeIndex)
    {
        ValidateIndex(nodeIndex);
        return _paths[nodeIndex];
    }

    public IReadOnlyList<ScanBreadcrumb> GetBreadcrumbs(uint nodeIndex)
    {
        ValidateIndex(nodeIndex);
        var breadcrumbs = new List<ScanBreadcrumb>();
        uint current = nodeIndex;
        while (current != NoNode)
        {
            breadcrumbs.Add(new(current, _result.Names[current] ?? string.Empty, _paths[current]));
            current = _result.Nodes[current].Parent;
        }
        breadcrumbs.Reverse();
        return breadcrumbs;
    }

    public bool TryFind(string path, out uint nodeIndex)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            nodeIndex = default;
            return false;
        }

        return _pathLookup.TryGetValue(Normalize(path), out nodeIndex);
    }

    public bool IsAmbiguous(string path) =>
        !string.IsNullOrWhiteSpace(path) && _ambiguousPaths.Contains(Normalize(path));

    public uint GetParent(uint nodeIndex)
    {
        ValidateIndex(nodeIndex);
        return _result.Nodes[nodeIndex].Parent;
    }

    void Validate(uint nodeIndex, byte[] states)
    {
        var chain = new List<int>();
        uint current = nodeIndex;
        while (current != NoNode)
        {
            if (current >= _result.Nodes.Length)
                throw new InvalidDataException($"Node {nodeIndex} has an invalid parent index {current}.");
            if (states[current] == 2) break;
            if (states[current] == 1)
                throw new InvalidDataException("The scan parent topology contains a cycle.");
            states[current] = 1;
            chain.Add((int)current);
            current = _result.Nodes[current].Parent;
        }
        foreach (int index in chain) states[index] = 2;
    }

    void BuildPath(uint nodeIndex, List<int> chain)
    {
        if (_paths[nodeIndex] is not null) return;
        chain.Clear();
        uint current = nodeIndex;
        while (current != NoNode && _paths[current] is null)
        {
            chain.Add((int)current);
            current = _result.Nodes[current].Parent;
        }
        string path = current == NoNode ? string.Empty : _paths[current];
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            int index = chain[i];
            path = path.Length == 0 ? Normalize(_result.Names[index] ?? string.Empty) : Join(path, _result.Names[index] ?? string.Empty);
            _paths[index] = path;
            if (!_ambiguousPaths.Contains(path) && !_pathLookup.TryAdd(path, (uint)index))
            {
                _pathLookup.Remove(path);
                _ambiguousPaths.Add(path);
            }
        }
    }

    static string Join(string parent, string child)
    {
        string cleanChild = child.Replace('/', '\\').Trim('\\');
        return parent.EndsWith('\\') ? parent + cleanChild : parent + "\\" + cleanChild;
    }

    static string Normalize(string path)
    {
        string normalized = path.Trim().Replace('/', '\\');
        while (normalized.Length > 3 && normalized.EndsWith('\\'))
            normalized = normalized[..^1];
        return normalized;
    }

    void ValidateIndex(uint nodeIndex)
    {
        if (nodeIndex >= _paths.Length)
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));
    }
}

/// <summary>Maintains browser-style node navigation history over a scan result.</summary>
public sealed class ScanNavigation
{
    readonly List<uint> _history = [];
    int _position;

    public ScanNavigation(ScanNavigationIndex index, uint initialNode = 0)
    {
        Index = index ?? throw new ArgumentNullException(nameof(index));
        _ = index.GetPath(initialNode);
        _history.Add(initialNode);
    }

    public ScanNavigationIndex Index { get; }
    public uint Current => _history[_position];
    public string CurrentPath => Index.GetPath(Current);
    public IReadOnlyList<ScanBreadcrumb> Breadcrumbs => Index.GetBreadcrumbs(Current);
    public bool CanGoBack => _position > 0;
    public bool CanGoForward => _position + 1 < _history.Count;

    public bool Navigate(uint nodeIndex)
    {
        _ = Index.GetPath(nodeIndex);
        if (nodeIndex == Current) return false;
        if (CanGoForward) _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add(nodeIndex);
        _position++;
        return true;
    }

    public bool Navigate(string path) => Index.TryFind(path, out uint node) && Navigate(node);

    public bool NavigateParent()
    {
        uint parent = Index.GetParent(Current);
        return parent != uint.MaxValue && Navigate(parent);
    }

    public bool GoBack()
    {
        if (!CanGoBack) return false;
        _position--;
        return true;
    }

    public bool GoForward()
    {
        if (!CanGoForward) return false;
        _position++;
        return true;
    }
}
