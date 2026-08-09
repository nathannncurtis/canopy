using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class SizeTreeView : UserControl
{
    public event Action<uint>? NodeSelected;
    public event Action<uint>? NodeActivated;
    public event Action<ResultSelectionSummary>? MultiSelectionChanged;
    public event Action<ResultSelectionSummary>? FileDragRequested;
    SizeNodeView[] _views = [];
    ScanResultManaged? _result;
    readonly ResultSelectionModel _selection = new();
    uint[] _displayOrder = [];
    bool _syncingMarks;
    Point? _dragStart;

    public SizeTreeView()
    {
        InitializeComponent();
    }

    internal static Task<SizeNodeView[]> PrepareAsync(
        ScanResultManaged result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Task.Run(() => BuildViewArray(result, cancellationToken), cancellationToken);
    }

    internal void PopulatePrepared(ScanResultManaged result, SizeNodeView[] views)
    {
        _result = result;
        _views = views;
        _displayOrder = Flatten(views.Length == 0 ? null : views[0]).ToArray();
        _selection.Clear();
        _tree.Items.Clear();

        if (result.Nodes.Length == 0) return;
        var root  = _views[0];

        _tree.Items.Add(root);

        // Auto-expand root after layout so the container exists.
        _tree.UpdateLayout();
        if (_tree.ItemContainerGenerator.ContainerFromItem(root) is TreeViewItem tvi)
            tvi.IsExpanded = true;
    }

    public bool SelectNode(uint nodeIndex)
    {
        if (_result is null || nodeIndex >= _views.Length || nodeIndex >= _result.Nodes.Length) return false;
        var lineage = new Stack<uint>();
        uint current = nodeIndex;
        while (current != uint.MaxValue)
        {
            lineage.Push(current);
            current = _result.Nodes[current].Parent;
        }
        ItemsControl parent = _tree;
        TreeViewItem? container = null;
        while (lineage.TryPop(out uint index))
        {
            container = GetOrRealizeContainer(parent, _views[index]);
            if (container is null) return false;
            if (lineage.Count > 0) container.IsExpanded = true;
            parent = container;
        }
        if (container is not null)
        {
            container.IsSelected = true;
            container.BringIntoView();
            return true;
        }
        return false;
    }

    static TreeViewItem? GetOrRealizeContainer(ItemsControl parent, object item)
    {
        parent.UpdateLayout();
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem existing) return existing;
        int index = parent.Items.IndexOf(item);
        if (index < 0) return null;
        IItemContainerGenerator generator = parent.ItemContainerGenerator;
        GeneratorPosition position = generator.GeneratorPositionFromIndex(index);
        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            bool newlyRealized;
            if (generator.GenerateNext(out newlyRealized) is not TreeViewItem generated) return null;
            if (newlyRealized) generator.PrepareItemContainer(generated);
            generated.BringIntoView();
            parent.UpdateLayout();
            return generated;
        }
    }

    static SizeNodeView[] BuildViewArray(ScanResultManaged result, CancellationToken cancellationToken)
    {
        var views = new SizeNodeView[result.Nodes.Length];
        for (int i = 0; i < result.Nodes.Length; i++)
        {
            if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            ref readonly ScanNode n = ref result.Nodes[i];
            ulong parentSize = n.Parent != uint.MaxValue
                ? result.Nodes[n.Parent].Size
                : n.Size; // root uses own size as 100%

            views[i] = new SizeNodeView
            {
                Index      = (uint)i,
                Name       = result.GetName((uint)i),
                Size       = n.Size,
                IsDir      = (n.Flags & ScanNodeFlags.Directory) != 0,
                ParentSize = parentSize,
            };
        }

        // Link children by parent index.
        for (int i = 0; i < result.Nodes.Length; i++)
        {
            if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            ref readonly ScanNode n = ref result.Nodes[i];
            if (n.Parent != uint.MaxValue)
                views[n.Parent].Children.Add(views[i]);
        }
        if (views.Length > 0) SortChildren(views[0], cancellationToken);
        return views;
    }

    static void SortChildren(SizeNodeView root, CancellationToken cancellationToken)
    {
        var pending = new Stack<SizeNodeView>();
        pending.Push(root);
        int visited = 0;
        while (pending.TryPop(out SizeNodeView? node))
        {
            if ((visited++ & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            node.Children.Sort(static (a, b) =>
            {
                int size = b.Size.CompareTo(a.Size);
                return size != 0 ? size : a.Index.CompareTo(b.Index);
            });
            foreach (SizeNodeView child in node.Children) pending.Push(child);
        }
    }

    void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SizeNodeView view)
        {
            ApplySelection(view.Index, Keyboard.Modifiers);
            NodeSelected?.Invoke(view.Index);
        }
    }

    void OnMarkChecked(object sender, RoutedEventArgs e) => OnMarkToggle(sender, true);
    void OnMarkUnchecked(object sender, RoutedEventArgs e) => OnMarkToggle(sender, false);
    void OnMarkToggle(object sender, bool requested)
    {
        if (_syncingMarks || sender is not CheckBox { DataContext: SizeNodeView view }) return;
        bool selected = _selection.SelectedIndices.Contains(view.Index);
        if (selected != requested) ApplySelection(view.Index, ModifierKeys.Control);
    }

    void ApplySelection(uint index, ModifierKeys modifiers)
    {
        uint[] before = _selection.SelectedIndices.ToArray();
        if (modifiers.HasFlag(ModifierKeys.Shift)) _selection.SelectRange(index, _displayOrder);
        else if (modifiers.HasFlag(ModifierKeys.Control)) _selection.Toggle(index);
        else _selection.Replace(index);
        _syncingMarks = true;
        try
        {
            foreach (uint changed in before.Concat(_selection.SelectedIndices).Distinct())
                if (changed < _views.Length) _views[changed].IsMarked = _selection.SelectedIndices.Contains(changed);
        }
        finally { _syncingMarks = false; }
        if (_result is not null) MultiSelectionChanged?.Invoke(_selection.Summarize(_result));
    }

    static IEnumerable<uint> Flatten(SizeNodeView? root)
    {
        if (root is null) yield break;
        var pending = new Stack<SizeNodeView>(); pending.Push(root);
        while (pending.TryPop(out SizeNodeView? node))
        {
            yield return node.Index;
            for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
    }

    void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindItem(e.OriginalSource as DependencyObject) is not TreeViewItem item ||
            item.DataContext is not SizeNodeView view) return;
        NodeActivated?.Invoke(view.Index);
        e.Handled = true;
    }

    void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        TreeViewItem? item = FindItem(e.OriginalSource as DependencyObject);
        if (item is null) { e.Handled = true; return; }
        item.IsSelected = true;
        item.Focus();
    }

    static TreeViewItem? FindItem(DependencyObject? source)
    {
        while (source is not null && source is not TreeViewItem)
            source = VisualTreeHelper.GetParent(source);
        return source as TreeViewItem;
    }

    void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_tree.SelectedItem is not SizeNodeView view) return;
        if (e.Key == Key.Space) { ApplySelection(view.Index, ModifierKeys.Control); e.Handled = true; }
        else if (e.Key == Key.Enter) { NodeActivated?.Invoke(view.Index); e.Handled = true; }
    }

    void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(_tree);

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _result is null) return;
        Point current = e.GetPosition(_tree);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        ResultSelectionSummary summary = _selection.Summarize(_result);
        if (summary.Items.Count > 0) FileDragRequested?.Invoke(summary);
    }
}
