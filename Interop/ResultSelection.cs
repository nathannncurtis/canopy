using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

public sealed record SelectedResultItem(uint NodeIndex, string Path, ulong Bytes, bool IsDirectory);
public sealed record ResultSelectionSummary(IReadOnlyList<SelectedResultItem> Items, ulong TotalBytes,
    int FileCount, int DirectoryCount);
public sealed record ResultSelectionActionFailure(string Path, string Error);
public sealed record ResultSelectionActionOutcome(int Requested, int Succeeded,
    IReadOnlyList<ResultSelectionActionFailure> Failures);

public sealed class ResultSelectionModel
{
    readonly HashSet<uint> _selected = [];
    uint? _anchor;
    public IReadOnlyCollection<uint> SelectedIndices => _selected;

    public void Replace(uint index) { _selected.Clear(); _selected.Add(index); _anchor = index; }
    public void Toggle(uint index) { if (!_selected.Remove(index)) _selected.Add(index); _anchor = index; }
    public void Clear() { _selected.Clear(); _anchor = null; }

    public void SelectRange(uint index, IReadOnlyList<uint> displayOrder)
    {
        ArgumentNullException.ThrowIfNull(displayOrder);
        uint anchor = _anchor ?? index;
        int first = IndexOf(displayOrder, anchor), last = IndexOf(displayOrder, index);
        if (first < 0 || last < 0) { Replace(index); return; }
        _selected.Clear();
        for (int position = Math.Min(first, last); position <= Math.Max(first, last); position++)
            _selected.Add(displayOrder[position]);
    }

    public ResultSelectionSummary Summarize(ScanResultManaged result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var items = new List<SelectedResultItem>(_selected.Count);
        ulong total = 0; int files = 0, directories = 0;
        foreach (uint index in _selected.Order())
        {
            if (index >= result.Nodes.Length) continue;
            ScanNode node = result.Nodes[index]; bool directory = (node.Flags & ScanNodeFlags.Directory) != 0;
            items.Add(new(index, BuildPath(result, index), node.Size, directory));
            total = ulong.MaxValue - total < node.Size ? ulong.MaxValue : total + node.Size;
            if (directory) directories++; else files++;
        }
        return new(items, total, files, directories);
    }

    static int IndexOf(IReadOnlyList<uint> values, uint value)
    { for (int i = 0; i < values.Count; i++) if (values[i] == value) return i; return -1; }

    static string BuildPath(ScanResultManaged result, uint index)
    {
        var parts = new Stack<string>(); var visited = new HashSet<uint>(); uint current = index;
        while (current != uint.MaxValue)
        {
            if (current >= result.Nodes.Length || !visited.Add(current)) throw new InvalidDataException("Result parent topology is invalid.");
            parts.Push(result.Names[current]); current = result.Nodes[current].Parent;
        }
        string path = parts.Pop(); while (parts.TryPop(out string? part)) path = Path.Combine(path, part);
        return path;
    }
}

public static class ResultSelectionActions
{
    public static async Task<ResultSelectionActionOutcome> ExecuteAsync(
        ResultSelectionSummary selection,
        Func<SelectedResultItem, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(action);
        var failures = new List<ResultSelectionActionFailure>();
        int succeeded = 0;
        foreach (SelectedResultItem item in selection.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action(item, cancellationToken).ConfigureAwait(false);
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsExpectedItemFailure(ex))
            {
                failures.Add(new(item.Path, ex.Message));
            }
        }
        return new(selection.Items.Count, succeeded, failures);
    }

    static bool IsExpectedItemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or Win32Exception or COMException;
}

public sealed record ShellLaunchPlan(string FileName, IReadOnlyList<string> Arguments);

public static class ShellLaunchPlans
{
    public static ShellLaunchPlan SelectInExplorer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new("explorer.exe", ["/select,", path]);
    }
}

public static class ResultSelectionFormatter
{
    public static string ToText(ResultSelectionSummary selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var builder = new StringBuilder();
        foreach (SelectedResultItem item in selection.Items)
            builder.Append(item.Path).Append('\t').Append(item.Bytes).Append('\t')
                .Append(item.IsDirectory ? "Directory" : "File").AppendLine();
        return builder.ToString();
    }

    public static string ToCsv(ResultSelectionSummary selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var builder = new StringBuilder("Path,Bytes,Type\r\n");
        foreach (SelectedResultItem item in selection.Items)
            builder.Append(Escape(item.Path)).Append(',').Append(item.Bytes).Append(',')
                .Append(item.IsDirectory ? "Directory" : "File").Append("\r\n");
        return builder.ToString();
    }

    static string Escape(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
