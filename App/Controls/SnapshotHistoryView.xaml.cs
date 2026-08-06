using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class SnapshotHistoryView : UserControl
{
    ScanSnapshotCatalog? _catalog;

    public event Action<Guid>? SnapshotActivated;

    public SnapshotHistoryView()
    {
        InitializeComponent();
        Clear("Configure a snapshot catalog to view history.");
    }

    public ScanSnapshotCatalog? Catalog => _catalog;

    public async Task SetCatalogAsync(ScanSnapshotCatalog? catalog, bool load = true,
        CancellationToken cancellationToken = default)
    {
        _catalog = catalog;
        if (catalog is null) { Clear("Configure a snapshot catalog to view history."); return; }
        if (load) await catalog.LoadAsync(cancellationToken);
        RefreshCatalog();
    }

    public void RefreshCatalog()
    {
        if (_catalog is null) { Clear("Configure a snapshot catalog to view history."); return; }
        SnapshotRetentionPolicy policy = _catalog.RetentionPolicy;
        string age = policy.MaximumAge is { } maximumAge ? $", age {maximumAge:g}" : string.Empty;
        string bytes = policy.MaximumBytesPerTarget is { } maximumBytes ? $", {maximumBytes:N0} bytes" : string.Empty;
        _policy.Text = $"Catalog: {_catalog.DirectoryPath} · Retention per target: {policy.MaximumPerTarget:N0} snapshots{age}{bytes}.";
        string? selected = _target.SelectedItem as string;
        string[] targets = _catalog.Entries.Select(x => x.Target).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        _target.ItemsSource = targets;
        _target.SelectedItem = targets.FirstOrDefault(x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault();
        if (targets.Length == 0) ShowTarget(null);
    }

    void OnTargetChanged(object sender, SelectionChangedEventArgs e) => ShowTarget(_target.SelectedItem as string);

    void ShowTarget(string? target)
    {
        _forecast.Text = string.Empty;
        if (_catalog is null || target is null)
        {
            _snapshots.ItemsSource = null; _trend.ItemsSource = null;
            _summary.Text = "No historical snapshots are available.";
            return;
        }
        ScanSnapshotMetadata[] entries = _catalog.Entries.Where(x =>
            string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase)).ToArray();
        ScanTrendSample[] samples = _catalog.GetTrend(target).ToArray();
        _snapshots.ItemsSource = entries.Select(x => new SnapshotRow(x, x.CapturedUtc.ToLocalTime().ToString("g"))).ToArray();
        _trend.ItemsSource = samples.Select(x => new TrendRow(x.CapturedUtc.ToLocalTime().ToString("g"), x.TotalBytes)).ToArray();
        _summary.Text = $"{entries.Length:N0} snapshots · {samples.Length:N0} trend samples for {target}.";
    }

    void OnForecast(object sender, RoutedEventArgs e) => CalculateForecast();
    void OnCapacityKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; CalculateForecast(); e.Handled = true;
    }

    void CalculateForecast()
    {
        if (_catalog is null || _target.SelectedItem is not string target) return;
        if (!ByteSizeParser.TryParse(_capacity.Text, out ulong capacity) || capacity == 0)
        {
            _forecast.Text = "Enter a valid nonzero capacity, such as 1 TiB.";
            return;
        }
        CapacityForecast forecast = _catalog.ForecastFull(target, capacity);
        if (!forecast.HasSufficientData)
        {
            _forecast.Text = $"Forecast unavailable: {forecast.Reason} " +
                $"Confidence {forecast.Confidence:P0}; trend {forecast.DailyGrowthBytes:N0} bytes/day.";
            return;
        }
        _forecast.Text = $"Estimated full: {forecast.EstimatedFullUtc!.Value.ToLocalTime():g} · " +
            $"growth {forecast.DailyGrowthBytes:N0} bytes/day · confidence {forecast.Confidence:P0}.";
    }

    void OnSnapshotDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindRow(e.OriginalSource as DependencyObject) is null) return;
        ActivateSelected();
    }
    void OnSnapshotKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; ActivateSelected(); e.Handled = true;
    }
    void ActivateSelected()
    {
        if (_snapshots.SelectedItem is SnapshotRow row) SnapshotActivated?.Invoke(row.Metadata.Id);
    }
    static DataGridRow? FindRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow) source = VisualTreeHelper.GetParent(source);
        return source as DataGridRow;
    }
    void Clear(string status)
    {
        _policy.Text = status; _summary.Text = string.Empty; _forecast.Text = string.Empty;
        _target.ItemsSource = null; _snapshots.ItemsSource = null; _trend.ItemsSource = null;
    }
    sealed record SnapshotRow(ScanSnapshotMetadata Metadata, string CapturedLocal)
    {
        public ulong TotalBytes => Metadata.TotalBytes;
        public ulong FileCount => Metadata.FileCount;
        public long SnapshotBytes => Metadata.SnapshotBytes;
    }
    sealed record TrendRow(string CapturedLocal, ulong TotalBytes);
}
