using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public sealed class Treemap : Panel
{
    uint _rootIndex = uint.MaxValue;
    ScanResultManaged? _result;
    List<SizeNodeView>? _currentChildren;
    readonly Stack<uint> _navStack = new();

    // 8 accent colors cycling by position.
    static readonly Brush[] Palette =
    [
        new SolidColorBrush(Color.FromRgb(0x4C, 0x9D, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0x57, 0xB8, 0x9A)),
        new SolidColorBrush(Color.FromRgb(0xE0, 0x72, 0x72)),
        new SolidColorBrush(Color.FromRgb(0xC7, 0x97, 0x3E)),
        new SolidColorBrush(Color.FromRgb(0x9B, 0x72, 0xCF)),
        new SolidColorBrush(Color.FromRgb(0x4F, 0xB3, 0xD4)),
        new SolidColorBrush(Color.FromRgb(0xD4, 0x8F, 0x4B)),
        new SolidColorBrush(Color.FromRgb(0x6B, 0xB5, 0x6B)),
    ];

    public event Action<IReadOnlyList<string>>? PathChanged;

    static Treemap()
    {
        foreach (var b in Palette)
            ((SolidColorBrush)b).Freeze();
    }

    public void SetRoot(ScanResultManaged result, uint rootIndex)
    {
        _result    = result;
        _rootIndex = rootIndex;
        _navStack.Clear();
        RefreshChildren();
        InvalidateMeasure();
        InvalidateArrange();
    }

    void RefreshChildren()
    {
        Children.Clear();
        if (_result == null || _rootIndex == uint.MaxValue) return;
        if (_rootIndex >= (uint)_result.Nodes.Length) return; // guard empty result

        var children = new List<SizeNodeView>();
        ref readonly ScanNode root = ref _result.Nodes[_rootIndex];
        uint child = root.FirstChild;
        int pos = 0;

        while (child != uint.MaxValue && child < (uint)_result.Nodes.Length)
        {
            ref readonly ScanNode cn = ref _result.Nodes[child];
            if (cn.Size > 0)
            {
                var view = new SizeNodeView
                {
                    Index      = child,
                    Name       = _result.GetName(child),
                    Size       = cn.Size,
                    IsDir      = (cn.Flags & ScanNodeFlags.Directory) != 0,
                    ParentSize = root.Size > 0 ? root.Size : 1,
                };

                Children.Add(MakeTile(view, pos));
                children.Add(view);
                pos++;
            }
            child = cn.NextSibling;
        }

        _currentChildren = children;
    }

    Border MakeTile(SizeNodeView view, int pos)
    {
        var brush = Palette[pos % Palette.Length];
        var border = new Border
        {
            Background      = brush,
            BorderBrush     = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(2),
            Tag             = view,
            ToolTip         = $"{view.Name}\n{Helpers.SizeFormatter.FormatBytes(view.Size)}",
            Cursor          = view.IsDir ? Cursors.Hand : Cursors.Arrow,
            Child           = new TextBlock
            {
                Text              = view.Name,
                Foreground        = Brushes.White,
                FontSize          = 11,
                Padding           = new Thickness(4, 2, 4, 2),
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top,
            },
        };

        if (view.IsDir)
            border.MouseLeftButtonDown += OnTileClick;

        border.MouseRightButtonDown += OnTileRightClick;
        return border;
    }

    void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is SizeNodeView view && _rootIndex != uint.MaxValue)
        {
            _navStack.Push(_rootIndex);
            _rootIndex = view.Index;
            RefreshChildren();
            InvalidateArrange();
            EmitPath();
        }
    }

    void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_navStack.Count > 0)
        {
            _rootIndex = _navStack.Pop();
            RefreshChildren();
            InvalidateArrange();
            EmitPath();
        }
        e.Handled = true;
    }

    void EmitPath()
    {
        if (_result == null) return;
        var path = new List<string>();
        var indices = _navStack.ToArray();
        Array.Reverse(indices);
        foreach (var idx in indices)
            path.Add(_result.GetName(idx));
        path.Add(_result.GetName(_rootIndex));
        PathChanged?.Invoke(path);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_currentChildren == null || _currentChildren.Count == 0)
            return finalSize;

        var rects = Squarify(_currentChildren, new Rect(0, 0, finalSize.Width, finalSize.Height));
        for (int i = 0; i < Children.Count && i < rects.Count; i++)
            Children[i].Arrange(rects[i]);

        return finalSize;
    }

    // Squarified treemap algorithm (Bruls, Huizing, van Wijk 2000).
    static List<Rect> Squarify(List<SizeNodeView> nodes, Rect bounds)
    {
        if (nodes.Count == 0) return [];

        double total = 0;
        foreach (var n in nodes) total += n.Size;
        if (total == 0) return new List<Rect>(new Rect[nodes.Count]);

        double area = bounds.Width * bounds.Height;
        var rects = new Rect[nodes.Count];
        SquarifyRecurse(nodes, 0, nodes.Count, bounds, area, total, rects);
        return [.. rects];
    }

    static void SquarifyRecurse(List<SizeNodeView> nodes, int start, int end,
                                Rect bounds, double totalArea, double totalSize, Rect[] rects)
    {
        if (start >= end) return;
        if (end - start == 1)
        {
            rects[start] = bounds;
            return;
        }

        double w = Math.Min(bounds.Width, bounds.Height);
        int    rowEnd   = start + 1;
        double rowSize  = nodes[start].Size;
        double bestWorst = AspectWorstInRow(nodes, start, start + 1, w, totalSize, totalArea);

        for (int i = start + 1; i < end; i++)
        {
            double newWorst = AspectWorstInRow(nodes, start, i + 1, w, totalSize, totalArea);
            if (newWorst > bestWorst) break;
            rowEnd    = i + 1;
            rowSize  += nodes[i].Size;
            bestWorst = newWorst;
        }

        bool   horizontal = bounds.Width >= bounds.Height;
        double rowFrac    = totalSize > 0 ? rowSize / totalSize : 0;
        double rowDim     = horizontal ? bounds.Width * rowFrac : bounds.Height * rowFrac;

        double offset = horizontal ? bounds.Y : bounds.X;
        for (int i = start; i < rowEnd; i++)
        {
            double frac    = rowSize > 0 ? nodes[i].Size / rowSize : 0;
            double itemDim = horizontal ? bounds.Height * frac : bounds.Width * frac;

            rects[i] = horizontal
                ? new Rect(bounds.X,      offset, rowDim,   itemDim)
                : new Rect(offset, bounds.Y,      itemDim,  rowDim);

            offset += itemDim;
        }

        Rect remaining = horizontal
            ? new Rect(bounds.X + rowDim, bounds.Y, bounds.Width  - rowDim, bounds.Height)
            : new Rect(bounds.X, bounds.Y + rowDim, bounds.Width,  bounds.Height - rowDim);

        double remainingSize = totalSize - rowSize;
        double remainingArea = remaining.Width * remaining.Height;
        SquarifyRecurse(nodes, rowEnd, end, remaining, remainingArea, remainingSize, rects);
    }

    static double AspectRatio(double itemSize, double rowSize, double w,
                               double totalSize, double totalArea)
    {
        if (rowSize <= 0 || totalSize <= 0 || totalArea <= 0) return double.MaxValue;
        double rowArea = totalArea * (rowSize / totalSize);
        if (rowArea <= 0) return double.MaxValue;
        double h     = rowArea / w;
        double itemW = w * (itemSize / rowSize);
        if (itemW <= 0 || h <= 0) return double.MaxValue;
        return Math.Max(itemW / h, h / itemW);
    }

    static double AspectWorstInRow(List<SizeNodeView> nodes, int start, int end,
                                    double w, double totalSize, double totalArea)
    {
        double rowSize = 0;
        for (int i = start; i < end; i++) rowSize += nodes[i].Size;
        double worst = 0;
        for (int i = start; i < end; i++)
            worst = Math.Max(worst, AspectRatio(nodes[i].Size, rowSize, w, totalSize, totalArea));
        return worst;
    }
}
