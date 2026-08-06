using System.Windows;
using System.Windows.Controls;
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

    public void Populate(ScanResultManaged result)
    {
        _result = result;
        _tree.Items.Clear();

        if (result.Nodes.Length == 0) return;

        _views = BuildViewArray(result);
        var root  = _views[0];

        SortChildren(root);

        _tree.Items.Add(root);

        // Auto-expand root after layout so the container exists.
        _tree.UpdateLayout();
        if (_tree.ItemContainerGenerator.ContainerFromItem(root) is TreeViewItem tvi)
            tvi.IsExpanded = true;
    }

    public void SelectNode(uint nodeIndex)
    {
        if (_result is null || nodeIndex >= _views.Length) return;
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
            parent.UpdateLayout();
            container = parent.ItemContainerGenerator.ContainerFromItem(_views[index]) as TreeViewItem;
            if (container is null) return;
            if (lineage.Count > 0) container.IsExpanded = true;
            parent = container;
        }
        if (container is not null)
        {
            container.IsSelected = true;
            container.BringIntoView();
        }
    }

    static SizeNodeView[] BuildViewArray(ScanResultManaged result)
    {
        var views = new SizeNodeView[result.Nodes.Length];
        for (int i = 0; i < result.Nodes.Length; i++)
        {
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
            ref readonly ScanNode n = ref result.Nodes[i];
            if (n.Parent != uint.MaxValue)
                views[n.Parent].Children.Add(views[i]);
        }

        return views;
    }

    static void SortChildren(SizeNodeView node)
    {
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var child in node.Children)
            SortChildren(child);
    }

    void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SizeNodeView view)
            NodeSelected?.Invoke(view.Index);
    }

    void OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_tree.SelectedItem is SizeNodeView view) NodeActivated?.Invoke(view.Index);
    }

    void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || _tree.SelectedItem is not SizeNodeView view) return;
        NodeActivated?.Invoke(view.Index);
        e.Handled = true;
    }
}
