using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class DiskUsageSummaryView : UserControl
{
    CancellationTokenSource? _generationCancellation;
    int _generation;

    public DiskUsageSummaryView() => InitializeComponent();

    public event Action<uint>? NodeActivated;

    public void SetResult(ScanResultManaged? result, ulong? capacityBytes = null,
        IReadOnlyList<string>? limitations = null)
    {
        int generation = ++_generation;
        _generationCancellation?.Cancel();
        _generationCancellation?.Dispose();
        _generationCancellation = new CancellationTokenSource();
        _folders.ItemsSource = null;
        _categories.ItemsSource = null;
        if (result is null)
        {
            _status.Text = "Run a scan to generate a summary.";
            return;
        }

        _status.Text = "Generating summary...";
        _ = GenerateAsync(result, capacityBytes, limitations ?? [], generation,
            _generationCancellation.Token);
    }

    async Task GenerateAsync(ScanResultManaged result, ulong? capacityBytes,
        IReadOnlyList<string> limitations, int generation, CancellationToken token)
    {
        try
        {
            DiskUsageSummaryResult summary = await Task.Run(() => DiskUsageSummary.Generate(result,
                new DiskUsageSummaryOptions
                {
                    CapacityBytes = capacityBytes,
                    ScanLimitations = limitations,
                }, token), token);
            if (generation != _generation || token.IsCancellationRequested) return;

            _folders.ItemsSource = summary.LargestFolders.Select(item =>
                new FolderRow(item.NodeIndex, item.Name, ByteSize(item.Bytes))).ToArray();
            _categories.ItemsSource = summary.LargestCategories.Select(item =>
                new CategoryRow(item.Key, ByteSize(item.Bytes), $"{item.Percentage:0.0}%")).ToArray();
            string capacity = summary.CapacityPercentage is double percentage
                ? $"; {percentage:0.0}% of capacity"
                : string.Empty;
            string completeness = summary.IsComplete
                ? "Scan complete."
                : $"Incomplete scan: {string.Join("; ", summary.Limitations)}.";
            string reconciliation = summary.TotalsReconcile
                ? "Totals reconcile."
                : $"Reported total differs from accounted files ({ByteSize(summary.AccountedFileBytes)}).";
            _status.Text = $"{ByteSize(summary.TotalBytes)} scanned{capacity}; " +
                $"{summary.AnomalyCount:N0} anomalies. {reconciliation} {completeness}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (generation == _generation)
                _status.Text = "The summary could not be generated. The scan result remains available.";
        }
    }

    void OnFolderActivated(object sender, MouseButtonEventArgs e) => ActivateSelected();

    void OnFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ActivateSelected();
        e.Handled = true;
    }

    void ActivateSelected()
    {
        if (_folders.SelectedItem is FolderRow row) NodeActivated?.Invoke(row.NodeIndex);
    }

    static string ByteSize(ulong value) => SizeFormatter.FormatBytes(value);

    sealed record FolderRow(uint NodeIndex, string Name, string DisplayBytes);
    sealed record CategoryRow(string Name, string DisplayBytes, string Share);
}
