using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class StorageDashboardView : UserControl
{
    readonly StorageDashboardPreferenceStore _store = new(AppDataPaths.StorageDashboard);
    StorageDashboardPreferences _preferences = new(); CancellationTokenSource? _calculation; bool _syncing; int _generation;
    public event Action<string>? QuickLocationActivated; public event Action<uint>? NodeActivated;
    public StorageDashboardView() { InitializeComponent(); Loaded += OnLoaded; }
    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _preferences = await _store.LoadAsync(); ApplyPreferences();
    }
    public void SetResult(ScanResultManaged? result, IReadOnlyDictionary<uint, DateTimeOffset>? lastWriteUtc = null)
    {
        int generation = ++_generation;
        _calculation?.Cancel(); _calculation?.Dispose(); _calculation = null;
        if (result is null) { _status.Text = "Run a scan to populate the dashboard."; Clear(); return; }
        _calculation = new(); CancellationToken token = _calculation.Token; _status.Text = "Building dashboard…";
        _ = BuildAsync(result, lastWriteUtc, generation, token);
    }
    async Task BuildAsync(ScanResultManaged result, IReadOnlyDictionary<uint, DateTimeOffset>? timestamps, int generation, CancellationToken token)
    {
        try
        {
            StorageDashboardResult dashboard = await Task.Run(() => StorageDashboard.Generate(result,
                new StorageDashboardOptions { LastWriteUtc = timestamps }, token), token);
            if (token.IsCancellationRequested || generation != _generation) return;
            _overview.Text = $"{SizeFormatter.FormatBytes(dashboard.TotalBytes)} scanned · {dashboard.Files:N0} files · {dashboard.Directories:N0} folders · {SizeFormatter.FormatBytes(dashboard.AccountedFileBytes)} accounted file bytes";
            _largestFiles.ItemsSource = dashboard.LargestFiles; _largestFolders.ItemsSource = dashboard.LargestFolders;
            _mostFiles.ItemsSource = dashboard.MostFiles; _types.ItemsSource = dashboard.FileTypes;
            _ages.ItemsSource = dashboard.FileAges; _sizes.ItemsSource = dashboard.SizeHistogram;
            _ageNotice.Text = dashboard.MissingAgeFiles == 0 ? "All file ages are represented."
                : $"Age metadata is unavailable for {dashboard.MissingAgeFiles:N0} file(s); legacy or partial scans may omit timestamps.";
            _status.Text = $"Dashboard ready · {dashboard.WorkItems:N0} bounded aggregation work items.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == _generation) { Clear(); _status.Text = $"Dashboard unavailable: {ex.Message}"; }
        }
    }
    void Clear() { _largestFiles.ItemsSource = _largestFolders.ItemsSource = _mostFiles.ItemsSource = null;
        _types.ItemsSource = _ages.ItemsSource = _sizes.ItemsSource = null; _overview.Text = _ageNotice.Text = string.Empty; }
    void ApplyPreferences()
    {
        _syncing = true;
        foreach (CheckBox box in FindVisualChildren<CheckBox>(this)) if (box.Tag is string tag)
            box.IsChecked = _preferences.EnabledWidgets.Contains(tag, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, TabItem tab) in new[] { ("overview", _overviewTab), ("largest-files", _largestFilesTab),
                     ("largest-folders", _largestFoldersTab), ("most-files", _mostFilesTab), ("types", _typesTab),
                     ("ages", _agesTab), ("sizes", _sizesTab) })
            tab.Visibility = _preferences.EnabledWidgets.Contains(key, StringComparer.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        _syncing = false; RenderQuickLocations();
    }
    void RenderQuickLocations()
    {
        _quick.Items.Clear(); foreach (string path in _preferences.QuickLocations)
        {
            bool available = Directory.Exists(path) || File.Exists(path);
            var button = new Button { Content = path, Tag = path, Margin = new Thickness(0, 0, 5, 0),
                ToolTip = available ? path : $"{path} (currently unavailable)" };
            button.Click += (_, _) => { if (available) QuickLocationActivated?.Invoke(path);
                else _status.Text = $"Quick location is currently unavailable: {path}"; };
            var remove = new MenuItem { Header = "Remove" }; remove.Click += async (_, _) => await RemoveLocationAsync(path);
            button.ContextMenu.Items.Add(remove); _quick.Items.Add(button);
        }
    }
    async void OnAddLocation(object sender, RoutedEventArgs e)
    {
        try { string path = Path.GetFullPath(_newLocation.Text); _preferences = _preferences with
            { QuickLocations = _preferences.QuickLocations.Append(path).ToArray() }; await SaveApplyAsync(); _newLocation.Clear(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
    }
    async void OnResetLocations(object sender, RoutedEventArgs e)
    { _preferences = _preferences with { QuickLocations = DefaultLocations() }; await SaveApplyAsync(); }
    async Task RemoveLocationAsync(string path)
    { _preferences = _preferences with { QuickLocations = _preferences.QuickLocations.Where(item => !item.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray() }; await SaveApplyAsync(); }
    async void OnWidgetsChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return; string[] enabled = FindVisualChildren<CheckBox>(this).Where(box => box.IsChecked == true && box.Tag is string).Select(box => (string)box.Tag).ToArray();
        _preferences = _preferences with { EnabledWidgets = enabled }; await SaveApplyAsync();
    }
    async Task SaveApplyAsync()
    {
        try { await _store.SaveAsync(_preferences); _preferences = await _store.LoadAsync(); ApplyPreferences(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { _status.Text = $"Dashboard preferences could not be saved: {ex.Message}"; }
    }
    void OnNodeActivated(object sender, MouseButtonEventArgs e)
    { if (sender is DataGrid { SelectedItem: StorageDashboardItem item }) NodeActivated?.Invoke(item.NodeIndex); }
    static string[] DefaultLocations() => SizeMonitor.Interop.DefaultLocations.Build();
    static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) { DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (T nested in FindVisualChildren<T>(child)) yield return nested; } }
}
