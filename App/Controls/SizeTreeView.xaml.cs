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
    SizeNodeView[] _views = [];
    ScanResultManaged? _result;

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
            NodeSelected?.Invoke(view.Index);
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
        if (e.Key != System.Windows.Input.Key.Enter || _tree.SelectedItem is not SizeNodeView view) return;
        NodeActivated?.Invoke(view.Index);
        e.Handled = true;
    }
}
