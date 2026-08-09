using System.IO;
using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class ShadowCopiesView : UserControl
{
    readonly WindowsShadowCopyService _service = new();
    CancellationTokenSource? _refreshCancellation;
    string? _livePath;
    public event Action<ShadowCopyInfo, string, bool>? ScanRequested;

    public ShadowCopiesView() { InitializeComponent(); Unloaded += (_, _) => CancelRefresh(); }

    public void SetLivePath(string? path)
    {
        _livePath = string.IsNullOrWhiteSpace(path) ? null : path;
        _status.Text = _livePath is null ? "Enter a local path before browsing its previous versions." :
            $"Showing snapshots that can contain {_livePath}.";
    }

    async void OnRefresh(object sender, RoutedEventArgs e)
    {
        CancelRefresh();
        _refreshCancellation = new CancellationTokenSource();
        try
        {
            _status.Text = "Reading Windows shadow-copy metadata…";
            IReadOnlyList<ShadowCopyInfo> snapshots = await _service.ListAsync(_refreshCancellation.Token);
            if (_livePath is not null)
            {
                string root = WindowsShadowCopyService.GetLocalDriveRoot(_livePath);
                snapshots = snapshots.Where(item => string.IsNullOrWhiteSpace(item.DriveRoot) ||
                    string.Equals(Path.TrimEndingDirectorySeparator(item.DriveRoot),
                        Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            _items.ItemsSource = snapshots;
            _items.SelectedIndex = snapshots.Count > 0 ? 0 : -1;
            _status.Text = snapshots.Count == 0 ? "No accessible shadow copies were found for this volume." :
                $"{snapshots.Count:N0} read-only snapshots found.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ShadowCopyException or ArgumentException or IOException or UnauthorizedAccessException)
        { _status.Text = $"Could not read shadow copies: {ex.Message}"; }
    }

    void OnScan(object sender, RoutedEventArgs e)
    {
        if (_items.SelectedItem is not ShadowCopyInfo shadow || string.IsNullOrWhiteSpace(_livePath))
        { _status.Text = "Select a snapshot and provide a local path first."; return; }
        try { ScanRequested?.Invoke(shadow, WindowsShadowCopyService.ResolvePath(shadow, _livePath), _compare.IsChecked == true); }
        catch (ArgumentException ex) { _status.Text = ex.Message; }
    }

    void CancelRefresh() { _refreshCancellation?.Cancel(); _refreshCancellation?.Dispose(); _refreshCancellation = null; }
}
