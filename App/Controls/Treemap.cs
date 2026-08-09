using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public sealed class Treemap : Panel
{
    uint _rootIndex = uint.MaxValue;
    ScanResultManaged? _result;
    List<SizeNodeView>? _currentChildren;
    readonly Stack<uint> _navStack = new();
    readonly Dictionary<GeometryCacheKey, GeometryCacheEntry> _geometryCache = [];
    const int MaximumGeometryCacheEntries = 128;
    TreemapPresentationPreferences _presentation = new();
    TreemapViewport _viewport = TreemapViewport.Fitted;
    Point? _panStart;
    (ulong Files, ulong Directories)[] _descendantCounts = [];
    public ulong DescendantCountWork { get; private set; }

    /// <summary>Diagnostic counter used to verify that unchanged layouts reuse geometry.</summary>
    public ulong LayoutComputationCount { get; private set; }

    // 8 accent colors cycling by position.
    static readonly Brush[] StandardPalette = BrushesFrom(TreemapPalette.Standard);
    static readonly Brush[] DeuteranopiaPalette = BrushesFrom(TreemapPalette.DeuteranopiaSafe);
    static readonly Brush[] ProtanopiaPalette = BrushesFrom(TreemapPalette.ProtanopiaSafe);
    static readonly Brush[] TritanopiaPalette = BrushesFrom(TreemapPalette.TritanopiaSafe);
    static readonly Brush[] MonochromePalette = BrushesFrom(TreemapPalette.Monochrome);

    TreemapPalette _paletteMode;
    public TreemapPalette PaletteMode
    {
        get => _paletteMode;
        set { if (_paletteMode == value) return; _paletteMode = value; RefreshChildren(); InvalidateVisual(); }
    }
    public bool AnimationsEnabled { get; set; } = true;
    public TreemapPresentationPreferences Presentation
    {
        get => _presentation;
        set { _presentation = value ?? new(); RefreshChildren(); InvalidateVisual(); }
    }

    public event Action<IReadOnlyList<string>>? PathChanged;
    public event Action<IReadOnlyList<TreemapLegendEntry>>? LegendChanged;

    static Treemap()
    {
        foreach (var b in new[] { StandardPalette, DeuteranopiaPalette, ProtanopiaPalette, TritanopiaPalette, MonochromePalette }.SelectMany(value => value))
            ((SolidColorBrush)b).Freeze();
    }

    public void SetRoot(ScanResultManaged result, uint rootIndex)
    {
        if (!ReferenceEquals(_result, result))
        {
            _geometryCache.Clear();
            BuildDescendantCounts(result);
        }
        _result    = result;
        _rootIndex = rootIndex;
        _navStack.Clear();
        RefreshChildren();
        AnimateTransition();
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
        IReadOnlyList<TreemapLegendEntry> legend = Presentation.Colors switch
        {
            TreemapColorMode.Extension => TreemapPresentationRules.Legend(children.Select(view =>
                (view.IsDir ? "(directory)" : TreemapPresentationRules.ExtensionKey(view.Name), view.Size)), StandardPalette.Length),
            TreemapColorMode.TopLevelDirectory => TreemapPresentationRules.Legend(children.Select(view =>
                (TopLevelKey(view.Index), view.Size)), StandardPalette.Length),
            _ => [],
        };
        LegendChanged?.Invoke(legend);
    }

    Border MakeTile(SizeNodeView view, int pos)
    {
        Brush[] palette = PaletteMode switch
        {
            TreemapPalette.DeuteranopiaSafe => DeuteranopiaPalette,
            TreemapPalette.ProtanopiaSafe => ProtanopiaPalette,
            TreemapPalette.TritanopiaSafe => TritanopiaPalette,
            TreemapPalette.Monochrome => MonochromePalette,
            _ => StandardPalette,
        };
        string colorKey = Presentation.Colors switch
        {
            TreemapColorMode.Extension => view.IsDir ? "(directory)" : TreemapPresentationRules.ExtensionKey(view.Name),
            TreemapColorMode.TopLevelDirectory => TopLevelKey(view.Index),
            _ => pos.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        int colorIndex = Presentation.Colors == TreemapColorMode.SiblingOrder ? pos % palette.Length :
            TreemapPresentationRules.StableBucket(colorKey, palette.Length);
        var brush = palette[colorIndex];
        double percentage = view.ParentSize == 0 ? 0 : view.Size * 100d / view.ParentSize;
        string path = BuildPath(view.Index);
        (ulong files, ulong dirs) = DescendantCounts(view.Index);
        var border = new Border
        {
            Background      = brush,
            BorderBrush     = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(2),
            Tag             = view,
            ToolTip         = $"{path}\nLogical: {Helpers.SizeFormatter.FormatBytes(view.Size)}\nAllocated: unavailable\n" +
                              $"Parent share: {percentage:F2}%\n{files:N0} files, {dirs:N0} folders\n" +
                              $"Extension: {(view.IsDir ? "(directory)" : TreemapPresentationRules.ExtensionKey(view.Name))}",
            Cursor          = view.IsDir ? Cursors.Hand : Cursors.Arrow,
            Child           = new TextBlock
            {
                Text              = TreemapPresentationRules.Label(view.Name, view.Size, percentage, Presentation.Labels),
                Foreground        = Brushes.White,
                FontSize          = 11,
                Padding           = new Thickness(4, 2, 4, 2),
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top,
            },
        };
        ToolTipService.SetInitialShowDelay(border, 400);
        ToolTipService.SetShowDuration(border, 20_000);
        AutomationProperties.SetName(border, $"{view.Name}, {percentage:F1} percent of parent");
        AutomationProperties.SetHelpText(border, border.ToolTip?.ToString() ?? string.Empty);

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
            AnimateTransition();
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
            AnimateTransition();
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

        IReadOnlyList<Rect> rects = GetOrCreateGeometry(_currentChildren, finalSize);
        for (int i = 0; i < Children.Count && i < rects.Count; i++)
        {
            Children[i].Arrange(rects[i]);
            if (Children[i] is Border { Child: TextBlock label })
                label.Visibility = Presentation.Labels != TreemapLabelMode.Hidden &&
                    rects[i].Width * rects[i].Height >= Presentation.MinimumLabelArea
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        return finalSize;
    }

    public Treemap()
    {
        Focusable = true; ClipToBounds = true;
        MouseWheel += OnMouseWheel; MouseDown += OnViewportMouseDown; MouseMove += OnViewportMouseMove;
        MouseUp += (_, _) => { _panStart = null; ReleaseMouseCapture(); };
        KeyDown += OnViewportKeyDown;
    }

    public void ResetViewport() { _viewport = TreemapViewport.Fitted; ApplyViewport(); }

    void AnimateTransition()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        TimeSpan duration = AppearancePreferenceRules.TreemapTransitionDuration(
            AnimationsEnabled ? MotionPreference.Full : MotionPreference.Reduced);
        if (duration == TimeSpan.Zero) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, duration)
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    static Brush[] BrushesFrom(TreemapPalette palette) => AppearancePreferenceRules.TreemapColors(palette)
        .Select(value => (Brush)new SolidColorBrush(Color.FromArgb((byte)(value >> 24),
            (byte)(value >> 16), (byte)(value >> 8), (byte)value))).ToArray();

    void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point point = e.GetPosition(this);
        _viewport = _viewport.Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2, point.X, point.Y, ActualWidth, ActualHeight);
        ApplyViewport(); e.Handled = true;
    }
    void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Middle) { _panStart = e.GetPosition(this); CaptureMouse(); e.Handled = true; }
    }
    void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not Point prior || e.MiddleButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(this);
        _viewport = _viewport.Pan(current.X - prior.X, current.Y - prior.Y, ActualWidth, ActualHeight);
        _panStart = current; ApplyViewport(); e.Handled = true;
    }
    void OnViewportKeyDown(object sender, KeyEventArgs e)
    {
        const double step = 30;
        if (e.Key is Key.D0 or Key.NumPad0) ResetViewport();
        else if (e.Key is Key.Add or Key.OemPlus) _viewport = _viewport.Zoom(1.2, ActualWidth / 2, ActualHeight / 2, ActualWidth, ActualHeight);
        else if (e.Key is Key.Subtract or Key.OemMinus) _viewport = _viewport.Zoom(1 / 1.2, ActualWidth / 2, ActualHeight / 2, ActualWidth, ActualHeight);
        else if (e.Key == Key.Left) _viewport = _viewport.Pan(step, 0, ActualWidth, ActualHeight);
        else if (e.Key == Key.Right) _viewport = _viewport.Pan(-step, 0, ActualWidth, ActualHeight);
        else if (e.Key == Key.Up) _viewport = _viewport.Pan(0, step, ActualWidth, ActualHeight);
        else if (e.Key == Key.Down) _viewport = _viewport.Pan(0, -step, ActualWidth, ActualHeight);
        else return;
        ApplyViewport(); e.Handled = true;
    }
    void ApplyViewport() => RenderTransform = new MatrixTransform(_viewport.Scale, 0, 0, _viewport.Scale, _viewport.X, _viewport.Y);

    string BuildPath(uint index)
    {
        if (_result is null) return string.Empty;
        var parts = new Stack<string>(); var seen = new HashSet<uint>();
        while (index != uint.MaxValue && index < _result.Nodes.Length && seen.Add(index))
        { parts.Push(_result.GetName(index)); index = _result.Nodes[index].Parent; }
        string path = parts.Count > 0 ? parts.Pop() : string.Empty;
        while (parts.TryPop(out string? part)) path = System.IO.Path.Combine(path, part);
        return path;
    }
    string TopLevelKey(uint index)
    {
        if (_result is null) return "(unknown)";
        uint current = index;
        while (current < _result.Nodes.Length)
        {
            uint parent = _result.Nodes[current].Parent;
            if (parent is uint.MaxValue or 0) break;
            current = parent;
        }
        return current < _result.Nodes.Length ? _result.GetName(current) : "(unknown)";
    }
    (ulong Files, ulong Directories) DescendantCounts(uint root)
    {
        return root < _descendantCounts.Length ? _descendantCounts[root] : default;
    }
    void BuildDescendantCounts(ScanResultManaged result)
    {
        TreemapHierarchyMetrics metrics = TreemapHierarchyMetrics.Calculate(result);
        _descendantCounts = metrics.Counts.ToArray(); DescendantCountWork = metrics.WorkItems;
    }

    IReadOnlyList<Rect> GetOrCreateGeometry(List<SizeNodeView> nodes, Size finalSize)
    {
        var key = new GeometryCacheKey(_rootIndex,
            BitConverter.DoubleToInt64Bits(finalSize.Width),
            BitConverter.DoubleToInt64Bits(finalSize.Height));
        ChildGeometryFact[] facts = nodes.Select(node => new ChildGeometryFact(node.Index, node.Size)).ToArray();
        if (_geometryCache.TryGetValue(key, out GeometryCacheEntry? cached) &&
            cached.Facts.AsSpan().SequenceEqual(facts))
            return cached.Rectangles;

        List<Rect> rectangles = Squarify(nodes, new Rect(0, 0, finalSize.Width, finalSize.Height));
        LayoutComputationCount++;
        if (_geometryCache.Count >= MaximumGeometryCacheEntries && !_geometryCache.ContainsKey(key))
            _geometryCache.Remove(_geometryCache.Keys.First());
        _geometryCache[key] = new GeometryCacheEntry(facts, rectangles);
        return rectangles;
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

    readonly record struct GeometryCacheKey(uint RootIndex, long WidthBits, long HeightBits);
    readonly record struct ChildGeometryFact(uint Index, ulong Size);
    sealed record GeometryCacheEntry(ChildGeometryFact[] Facts, IReadOnlyList<Rect> Rectangles);
}
