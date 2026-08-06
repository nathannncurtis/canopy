using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public sealed record ComparisonNodeActivation(bool IsCurrentScan, uint NodeIndex);

public partial class ScanComparisonView : UserControl
{
    CancellationTokenSource? _comparisonCancellation;
    ScanComparisonResult? _comparison;
    int _generation;

    public event Action<ComparisonNodeActivation>? NodeActivated;

    public ScanComparisonView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelComparison();
    }

    public void SetResults(ScanResultManaged? previous, ScanResultManaged? current)
    {
        CancelComparison();
        int generation = ++_generation;
        _comparison = null;
        _changes.ItemsSource = null;
        _growth.ItemsSource = null;
        _warning.Text = string.Empty;
        if (previous is null || current is null)
        {
            _summary.Text = "Choose two scan results to compare.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _comparisonCancellation = cancellation;
        _summary.Text = "Comparing scans…";
        _ = CompareAsync(previous, current, generation, cancellation);
    }

    async Task CompareAsync(ScanResultManaged previous, ScanResultManaged current,
        int generation, CancellationTokenSource cancellation)
    {
        try
        {
            ScanComparisonResult comparison = await Task.Run(
                () => ScanResultComparison.Compare(previous, current, cancellation.Token),
                cancellation.Token);
            if (generation != _generation || cancellation.IsCancellationRequested) return;
            _comparison = comparison;
            ApplyChangeFilter();
            ApplyGrowthFilter();
            int added = comparison.Changes.Count(x => x.Kind == ScanChangeKind.Added);
            int removed = comparison.Changes.Count(x => x.Kind == ScanChangeKind.Removed);
            int moved = comparison.Changes.Count(x => x.Kind == ScanChangeKind.Moved);
            int resized = comparison.Changes.Count(x => x.Kind == ScanChangeKind.Resized);
            _summary.Text = $"{comparison.Changes.Count:N0} changes — {added:N0} added, " +
                $"{removed:N0} removed, {moved:N0} moved, {resized:N0} resized; " +
                $"{comparison.DirectoryGrowth.Count:N0} folders changed size.";
            _warning.Text = comparison.AmbiguousPaths.Count == 0 ? string.Empty :
                $"Warning: {comparison.AmbiguousPaths.Count:N0} duplicate paths were ambiguous and excluded from comparison.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            if (generation == _generation) _summary.Text = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_comparisonCancellation, cancellation)) _comparisonCancellation = null;
            cancellation.Dispose();
        }
    }

    void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyChangeFilter();
    void OnGrowthFilterChanged(object sender, RoutedEventArgs e) => ApplyGrowthFilter();

    void ApplyChangeFilter()
    {
        if (_comparison is null) return;
        _changes.ItemsSource = _comparison.Changes.Where(change => change.Kind switch
        {
            ScanChangeKind.Added => _added.IsChecked == true,
            ScanChangeKind.Removed => _removed.IsChecked == true,
            ScanChangeKind.Moved => _moved.IsChecked == true,
            ScanChangeKind.Resized => _resized.IsChecked == true,
            _ => false,
        }).ToArray();
    }

    void ApplyGrowthFilter()
    {
        if (_comparison is null) return;
        _growth.ItemsSource = _fastestOnly.IsChecked == true
            ? _comparison.GetFastestGrowingFolders(25)
            : _comparison.DirectoryGrowth.OrderByDescending(x => x.SizeDelta)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    void OnChangeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindRow(e.OriginalSource as DependencyObject) is null) return;
        ActivateChange();
    }
    void OnChangeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; ActivateChange(); e.Handled = true;
    }
    void OnGrowthDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindRow(e.OriginalSource as DependencyObject) is null) return;
        ActivateGrowth();
    }
    void OnGrowthKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; ActivateGrowth(); e.Handled = true;
    }

    void ActivateChange()
    {
        if (_changes.SelectedItem is not ScanItemChange change) return;
        bool current = change.CurrentNodeIndex != uint.MaxValue;
        NodeActivated?.Invoke(new(current, current ? change.CurrentNodeIndex : change.PreviousNodeIndex));
    }
    void ActivateGrowth()
    {
        if (_growth.SelectedItem is not DirectoryGrowth growth) return;
        bool current = growth.CurrentNodeIndex != uint.MaxValue;
        NodeActivated?.Invoke(new(current, current ? growth.CurrentNodeIndex : growth.PreviousNodeIndex));
    }

    static DataGridRow? FindRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow) source = VisualTreeHelper.GetParent(source);
        return source as DataGridRow;
    }

    void CancelComparison()
    {
        _comparisonCancellation?.Cancel();
        _comparisonCancellation = null;
    }
}
