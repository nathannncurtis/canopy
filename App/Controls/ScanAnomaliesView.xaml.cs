using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class ScanAnomaliesView : UserControl
{
    CancellationTokenSource? _analysisCancellation;
    ScanResultManaged? _result;
    IReadOnlyList<ScanAnomaly> _allAnomalies = [];
    ScanAnomalyOptions? _lastOptions;
    bool _analysisCompleted;
    long _generation;

    public event Action<uint>? NodeActivated;

    public ScanAnomaliesView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelAnalysis();
        Loaded += (_, _) =>
        {
            if (_result is not null && !_analysisCompleted && _analysisCancellation is null)
            {
                if (_lastOptions is not null) StartAnalysis(_lastOptions);
                else TryStartAnalysis();
            }
        };
    }

    public void SetResult(ScanResultManaged? result)
    {
        CancelAnalysis();
        _result = result;
        _allAnomalies = [];
        _analysisCompleted = false;
        _items.ItemsSource = null;
        if (result is null)
        {
            _status.Text = "Run a scan to analyze path anomalies.";
            return;
        }

        TryStartAnalysis();
    }

    void OnAnalyze(object sender, RoutedEventArgs e) => TryStartAnalysis();

    void TryStartAnalysis()
    {
        try { StartAnalysis(ReadOptions()); }
        catch (FormatException ex) { _status.Text = ex.Message; }
    }

    ScanAnomalyOptions ReadOptions() => new()
    {
        DeepHierarchyThreshold = ParseThreshold(_depthThreshold.Text, "depth"),
        LongNameThreshold = ParseThreshold(_nameThreshold.Text, "name length"),
        RelativePathLengthThreshold = ParseThreshold(_pathThreshold.Text, "relative path length"),
    };

    static int ParseThreshold(string text, string label) =>
        int.TryParse(text.Trim(), out int value) && value >= 0
            ? value
            : throw new FormatException($"The {label} threshold must be a non-negative whole number.");

    void StartAnalysis(ScanAnomalyOptions options)
    {
        ScanResultManaged? result = _result;
        if (result is null) return;
        CancelAnalysis();
        _lastOptions = options;
        _analysisCompleted = false;
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _analysisCancellation = cancellation;
        long generation = Volatile.Read(ref _generation);
        _status.Text = $"Analyzing {result.Nodes.Length:N0} scan items...";
        _ = AnalyzeAsync(result, options, cancellation, token, generation);
    }

    async Task AnalyzeAsync(
        ScanResultManaged result,
        ScanAnomalyOptions options,
        CancellationTokenSource cancellation,
        CancellationToken token,
        long generation)
    {
        try
        {
            IReadOnlyList<ScanAnomaly> anomalies = await Task.Run(
                () => ScanAnomalyFinder.Find(result, options, token), token);
            if (generation != Volatile.Read(ref _generation) || token.IsCancellationRequested) return;
            _allAnomalies = anomalies;
            _analysisCompleted = true;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _items.ItemsSource = null;
                _status.Text = $"Unable to analyze scan anomalies: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, cancellation))
                _analysisCancellation = null;
            cancellation.Dispose();
        }
    }

    void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_items is not null && _analysisCompleted) ApplyFilter();
    }

    void ApplyFilter()
    {
        ScanAnomalyKind? kind = _kindFilter.SelectedIndex switch
        {
            1 => ScanAnomalyKind.DeepHierarchy,
            2 => ScanAnomalyKind.LongName,
            3 => ScanAnomalyKind.LongPath,
            4 => ScanAnomalyKind.TroublesomeWindowsName,
            _ => null,
        };
        ScanAnomaly[] visible = (kind is null
                ? _allAnomalies
                : _allAnomalies.Where(item => item.Kind == kind.Value))
            .ToArray();
        _items.ItemsSource = visible;
        _status.Text = kind is null
            ? $"{visible.Length:N0} anomalies found."
            : $"{visible.Length:N0} {FormatKind(kind.Value)} anomalies shown " +
              $"({_allAnomalies.Count:N0} total).";
    }

    static string FormatKind(ScanAnomalyKind kind) => kind switch
    {
        ScanAnomalyKind.DeepHierarchy => "deep hierarchy",
        ScanAnomalyKind.LongName => "long name",
        ScanAnomalyKind.LongPath => "long relative path",
        ScanAnomalyKind.TroublesomeWindowsName => "troublesome Windows name",
        _ => "scan",
    };

    void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(_items, source) is DataGridRow &&
            _items.SelectedItem is ScanAnomaly anomaly)
            NodeActivated?.Invoke(anomaly.NodeIndex);
    }

    void CancelAnalysis()
    {
        Interlocked.Increment(ref _generation);
        CancellationTokenSource? cancellation = _analysisCancellation;
        _analysisCancellation = null;
        cancellation?.Cancel();
    }
}
