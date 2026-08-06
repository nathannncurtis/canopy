using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class EmptyItemsView : UserControl
{
    CancellationTokenSource? _findCancellation;
    IReadOnlyList<EmptyScanItem> _allItems = [];
    ScanResultManaged? _result;
    bool _hasCompletedResult;
    long _generation;

    public event Action<uint>? NodeActivated;

    public EmptyItemsView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelFind();
        Loaded += (_, _) =>
        {
            if (_result is not null && !_hasCompletedResult && _findCancellation is null)
                StartFind();
        };
    }

    public void SetResult(ScanResultManaged? result)
    {
        CancelFind();
        _result = result;
        _hasCompletedResult = false;
        _allItems = [];
        _items.ItemsSource = null;

        if (result is null)
        {
            _status.Text = "Run a scan, then explicitly select which uncertain candidates to include.";
            return;
        }

        StartFind();
    }

    void StartFind()
    {
        ScanResultManaged? result = _result;
        if (result is null) return;
        var options = new EmptyItemFinderOptions
        {
            IncludeZeroAllocationFiles = _includeFiles.IsChecked == true,
            AssumeCompleteUnfilteredDirectoryEnumeration = _includeDirectories.IsChecked == true,
        };
        if (!options.IncludeZeroAllocationFiles &&
            !options.AssumeCompleteUnfilteredDirectoryEnumeration)
        {
            _status.Text = "Select a candidate type above; scan data alone cannot prove emptiness.";
            _hasCompletedResult = false;
            return;
        }
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _findCancellation = cancellation;
        long generation = Volatile.Read(ref _generation);
        _status.Text = $"Checking {result.Nodes.Length:N0} scan items...";
        _ = FindAsync(result, options, cancellation, token, generation);
    }

    async Task FindAsync(
        ScanResultManaged result,
        EmptyItemFinderOptions options,
        CancellationTokenSource cancellation,
        CancellationToken token,
        long generation)
    {
        try
        {
            IReadOnlyList<EmptyScanItem> found = await Task.Run(
                () => EmptyItemFinder.Find(result, options, token), token);
            if (generation != Volatile.Read(ref _generation) || token.IsCancellationRequested)
                return;

            _allItems = found;
            _hasCompletedResult = true;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _items.ItemsSource = null;
                _status.Text = $"Unable to find empty items: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_findCancellation, cancellation))
            {
                _findCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_items is not null && _hasCompletedResult)
            ApplyFilter();
    }

    void OnTrustOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_items is null || _result is null) return;
        CancelFind();
        _hasCompletedResult = false;
        _allItems = [];
        _items.ItemsSource = null;
        StartFind();
    }

    void ApplyFilter()
    {
        IEnumerable<EmptyScanItem> filtered = _kindFilter.SelectedIndex switch
        {
            1 => _allItems.Where(item => item.Kind == EmptyItemKind.File),
            2 => _allItems.Where(item => item.Kind == EmptyItemKind.Directory),
            _ => _allItems,
        };
        EmptyScanItem[] visible = filtered.ToArray();
        _items.ItemsSource = visible;

        int fileCount = visible.Count(item => item.Kind == EmptyItemKind.File);
        int directoryCount = visible.Length - fileCount;
        _status.Text = $"{visible.Length:N0} empty items ({fileCount:N0} files, " +
                       $"{directoryCount:N0} directories).";
    }

    void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_items.SelectedItem is EmptyScanItem item)
            NodeActivated?.Invoke(item.NodeIndex);
    }

    void CancelFind()
    {
        Interlocked.Increment(ref _generation);
        CancellationTokenSource? cancellation = _findCancellation;
        _findCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }
}
