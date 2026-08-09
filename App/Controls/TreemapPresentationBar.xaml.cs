using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class TreemapPresentationBar : UserControl
{
    bool _syncing;
    TreemapPresentationPreferences _current = new();
    public event Action<TreemapPresentationPreferences>? PreferencesChanged;
    public event Action? ResetRequested;
    public TreemapPresentationBar() { InitializeComponent(); Apply(new()); }
    public void Apply(TreemapPresentationPreferences value)
    {
        _current = value; _syncing = true; _labels.SelectedIndex = (int)value.Labels; _colors.SelectedIndex = (int)value.Colors;
        _syncing = false;
    }
    void OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _labels.SelectedIndex < 0 || _colors.SelectedIndex < 0) return;
        _current = _current with { Labels = (TreemapLabelMode)_labels.SelectedIndex,
            Colors = (TreemapColorMode)_colors.SelectedIndex };
        PreferencesChanged?.Invoke(_current);
    }
    void OnReset(object sender, RoutedEventArgs e) => ResetRequested?.Invoke();
    public void SetLegend(IReadOnlyList<TreemapLegendEntry> entries, IReadOnlyList<uint> colors)
    {
        _legend.Items.Clear();
        foreach (TreemapLegendEntry entry in entries)
        {
            uint value = entry.IsOther || entry.Bucket < 0 ? 0xff777777u : colors[entry.Bucket % colors.Count];
            var swatch = new Border { Width = 10, Height = 10, Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)) };
            _legend.Items.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0),
                Children = { swatch, new TextBlock { Text = entry.Key, VerticalAlignment = VerticalAlignment.Center } } });
        }
    }
}
