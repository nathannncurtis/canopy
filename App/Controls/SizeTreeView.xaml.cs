using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class SizeTreeView : UserControl
{
    public event Action<uint>? NodeSelected;

    public SizeTreeView()
    {
        InitializeComponent();
    }

    public void Populate(ScanResultManaged result)
    {
        _tree.Items.Clear();

        if (result.Nodes.Length == 0) return;

        var views = BuildViewArray(result);
        var root  = views[0];

        SortChildren(root);

        _tree.Items.Add(root);

        // Auto-expand root after layout so the container exists.
        _tree.UpdateLayout();
        if (_tree.ItemContainerGenerator.ContainerFromItem(root) is TreeViewItem tvi)
            tvi.IsExpanded = true;
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
}
