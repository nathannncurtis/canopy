using System.Windows;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class SearchResultsView : UserControl
{
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    CancellationTokenSource? _searchCancellation;
    ScanResultManaged? _result;
    long _searchGeneration;

    public event Action<uint>? NodeActivated;

    public SearchResultsView()
    {
        InitializeComponent();
        _debounce.Tick += OnDebounceTick;
        Unloaded += (_, _) => CancelSearch();
    }

    public void SetResult(ScanResultManaged? result)
    {
        _result = result;
        _results.ItemsSource = null;
        _status.Text = result is null
            ? "Run a scan to search its results."
            : $"Ready to search {result.Nodes.Length:N0} items. Enter a filter to begin.";
    }

    void OnFilterChanged(object sender, RoutedEventArgs e) => ScheduleSearch(immediate: false);

    void OnApply(object sender, RoutedEventArgs e) => ScheduleSearch(immediate: true);

    void OnClear(object sender, RoutedEventArgs e)
    {
        _queryBox.Text = string.Empty;
        _extensionBox.Text = string.Empty;
        _minimumBox.Text = string.Empty;
        _maximumBox.Text = string.Empty;
        _regexCheck.IsChecked = false;
        _kindBox.SelectedIndex = 0;
        CancelSearch();
        _results.ItemsSource = null;
        if (_result is not null)
            _status.Text = $"Ready to search {_result.Nodes.Length:N0} items. Enter a filter to begin.";
    }

    void ScheduleSearch(bool immediate)
    {
        if (!IsLoaded || _result is null) return;
        _debounce.Stop();
        if (!HasCriteria())
        {
            CancelSearch();
            _results.ItemsSource = null;
            _status.Text = $"Ready to search {_result.Nodes.Length:N0} items. Enter a filter to begin.";
            return;
        }
        if (immediate)
            _ = RunSearchAsync();
        else
            _debounce.Start();
    }

    void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _ = RunSearchAsync();
    }

    async Task RunSearchAsync()
    {
        if (_result is null) return;
        CancelSearch();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        long generation = Interlocked.Increment(ref _searchGeneration);

        try
        {
            ScanQuery query = BuildQuery();
            _status.Text = "Searching...";
            IReadOnlyList<ScanSearchResult> matches = await Task.Run(
                () => ScanResultQuery.Search(_result, query, cancellation.Token),
                cancellation.Token);
            if (generation != Volatile.Read(ref _searchGeneration)) return;
            _results.ItemsSource = matches;
            _status.Text = $"{matches.Count:N0} matches";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException
                                      or RegexMatchTimeoutException or OverflowException)
        {
            if (generation == Volatile.Read(ref _searchGeneration))
            {
                _results.ItemsSource = null;
                _status.Text = ex.Message;
            }
        }
    }

    ScanQuery BuildQuery() => new()
    {
        Text = _regexCheck.IsChecked == true ? null : NullIfWhiteSpace(_queryBox.Text),
        RegexPattern = _regexCheck.IsChecked == true ? NullIfWhiteSpace(_queryBox.Text) : null,
        Extensions = _extensionBox.Text.Split([',', ';', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        MinimumSize = ParseOptionalSize(_minimumBox.Text, "minimum"),
        MaximumSize = ParseOptionalSize(_maximumBox.Text, "maximum"),
        Kinds = _kindBox.SelectedIndex switch
        {
            1 => ScanItemKinds.Files,
            2 => ScanItemKinds.Directories,
            _ => ScanItemKinds.All,
        },
    };

    bool HasCriteria() =>
        !string.IsNullOrWhiteSpace(_queryBox.Text) ||
        !string.IsNullOrWhiteSpace(_extensionBox.Text) ||
        !string.IsNullOrWhiteSpace(_minimumBox.Text) ||
        !string.IsNullOrWhiteSpace(_maximumBox.Text) ||
        _kindBox.SelectedIndex is 1 or 2;

    static ulong? ParseOptionalSize(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (ByteSizeParser.TryParse(text, out ulong bytes)) return bytes;
        throw new FormatException($"The {label} size is invalid.");
    }

    static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_results.SelectedItem is ScanSearchResult result)
            NodeActivated?.Invoke(result.NodeIndex);
    }

    void CancelSearch()
    {
        _debounce.Stop();
        Interlocked.Increment(ref _searchGeneration);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }
}
