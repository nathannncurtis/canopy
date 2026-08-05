using System.Windows;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class SearchResultsView : UserControl
{
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    CancellationTokenSource? _searchCancellation;
    ScanResultManaged? _result;
    long _searchGeneration;
    readonly ScanFilterPresetStore _presetStore = new(AppDataPaths.FilterPresets);
    bool _applyingPreset;

    public event Action<uint>? NodeActivated;

    public SearchResultsView()
    {
        InitializeComponent();
        _debounce.Tick += OnDebounceTick;
        Loaded += OnLoaded;
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

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetPresets(await _presetStore.LoadAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = ex.Message;
        }
    }

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

    void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingPreset || _presetBox.SelectedItem is not ScanFilterPreset preset) return;
        _applyingPreset = true;
        try
        {
            ScanQuery query = preset.Query;
            _presetNameBox.Text = preset.Name;
            _regexCheck.IsChecked = !string.IsNullOrWhiteSpace(query.RegexPattern);
            _queryBox.Text = query.RegexPattern ?? query.Text ?? string.Empty;
            _extensionBox.Text = string.Join(", ", query.Extensions);
            _minimumBox.Text = FormatOptionalSize(query.MinimumSize);
            _maximumBox.Text = FormatOptionalSize(query.MaximumSize);
            _kindBox.SelectedIndex = query.Kinds switch
            {
                ScanItemKinds.Files => 1,
                ScanItemKinds.Directories => 2,
                _ => 0,
            };
        }
        finally
        {
            _applyingPreset = false;
        }
        ScheduleSearch(immediate: true);
    }

    async void OnSavePreset(object sender, RoutedEventArgs e)
    {
        string name = _presetNameBox.Text.Trim();
        if (name.Length == 0 && _presetBox.SelectedItem is ScanFilterPreset selected)
            name = selected.Name;
        try
        {
            IReadOnlyList<ScanFilterPreset> presets = await _presetStore.UpsertAsync(
                new ScanFilterPreset(name, BuildQuery()));
            SetPresets(presets, name);
            _status.Text = $"Saved preset '{name}'.";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException
                                      or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = ex.Message;
        }
    }

    async void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (_presetBox.SelectedItem is not ScanFilterPreset selected) return;
        try
        {
            var result = await _presetStore.DeleteAsync(selected.Name);
            SetPresets(result.Presets);
            _presetNameBox.Text = string.Empty;
            _status.Text = result.Removed ? $"Deleted preset '{selected.Name}'." : "Preset not found.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = ex.Message;
        }
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

    static string FormatOptionalSize(ulong? bytes) =>
        bytes is null ? string.Empty : $"{bytes.Value} B";

    void SetPresets(IReadOnlyList<ScanFilterPreset> presets, string? selectName = null)
    {
        _applyingPreset = true;
        try
        {
            _presetBox.ItemsSource = presets;
            _presetBox.SelectedItem = selectName is null
                ? null
                : presets.FirstOrDefault(item =>
                    string.Equals(item.Name, selectName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _applyingPreset = false;
        }
    }

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
