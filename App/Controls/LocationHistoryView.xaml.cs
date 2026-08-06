using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class LocationHistoryView : UserControl
{
    LocationHistoryStore? _store;
    CancellationTokenSource _lifetime = new();
    readonly SemaphoreSlim _operationGate = new(1, 1);
    bool _operationInFlight;

    public event Action<string>? PathActivated;

    public LocationHistoryView()
    {
        InitializeComponent();
        SetActionState(null);
        Unloaded += (_, _) => _lifetime.Cancel();
        Loaded += (_, _) =>
        {
            if (_lifetime.IsCancellationRequested)
            {
                _lifetime = new CancellationTokenSource();
            }
        };
    }

    public LocationHistoryStore? Store => _store;

    public async Task SetStoreAsync(LocationHistoryStore? store, bool load = true,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            if (store is not null && load)
                await store.LoadAsync(cancellationToken);
            _store = store;
            RefreshLocations();
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    public void RefreshLocations()
    {
        string? selectedPath = (_locations.SelectedItem as LocationItem)?.Path;
        IReadOnlyList<ScanLocation> locations = _store?.Locations ?? [];
        LocationItem[] items = locations.Select(LocationItem.From).ToArray();
        _locations.ItemsSource = items;
        if (selectedPath is not null)
            _locations.SelectedItem = items.FirstOrDefault(item =>
                string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        _status.Text = _store is null
            ? "Location history is unavailable."
            : locations.Count == 0 ? "No favorite or recent locations yet."
            : $"{locations.Count(x => x.IsFavorite):N0} favorites, {locations.Count(x => !x.IsFavorite):N0} recent";
        SetActionState(_locations.SelectedItem as LocationItem);
    }

    async void OnOpen(object sender, RoutedEventArgs e) => await ActivateAsync(_pathBox.Text);

    async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ActivateAsync(_pathBox.Text);
    }

    async void OnLocationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_locations.SelectedItem is LocationItem item) await ActivateAsync(item.Path);
    }

    async void OnLocationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _locations.SelectedItem is not LocationItem item) return;
        e.Handled = true;
        await ActivateAsync(item.Path);
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = _locations.SelectedItem as LocationItem;
        if (selected is not null) _pathBox.Text = selected.Path;
        SetActionState(selected);
    }

    void OnPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_locations is null) return;
        LocationItem? selected = _locations.SelectedItem as LocationItem;
        if (selected is not null &&
            !string.Equals(selected.Path, _pathBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            _locations.SelectedItem = null;
        SetActionState(_locations.SelectedItem as LocationItem);
    }

    async void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async token =>
        {
            LocationHistoryStore? store = _store;
            if (store is null || !TryNormalizeInput(_pathBox.Text, out string path)) return;
            bool isFavorite = store.Locations.Any(item => item.IsFavorite &&
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (isFavorite)
                await store.UnpinAsync(path, token);
            else
                await store.PinAsync(path, cancellationToken: token);
            RefreshLocations();
            _pathBox.Text = path;
        });
    }

    async void OnRemove(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async token =>
        {
            if (_store is null || _locations.SelectedItem is not LocationItem selected) return;
            await _store.RemoveAsync(selected.Path, token);
            RefreshLocations();
        });
    }

    async Task ActivateAsync(string path)
    {
        if (_store is null)
        {
            _status.Text = "Location history is unavailable.";
            return;
        }
        await RunOperationAsync(token =>
        {
            LocationHistoryStore? store = _store;
            if (store is null || !TryNormalizeInput(path, out string normalized)) return Task.CompletedTask;
            _pathBox.Text = normalized;
            PathActivated?.Invoke(normalized);
            return Task.CompletedTask;
        });
    }

    void SetActionState(LocationItem? selected)
    {
        if (_openButton is null || _pinButton is null || _removeButton is null) return;
        _openButton.IsEnabled = !_operationInFlight && _store is not null;
        _pinButton.IsEnabled = !_operationInFlight && _store is not null;
        _pinButton.Content = selected?.IsFavorite == true ? "Unfavorite" : "Favorite";
        _removeButton.IsEnabled = !_operationInFlight && selected is not null && _store is not null;
    }

    async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        CancellationToken token = _lifetime.Token;
        try
        {
            await _operationGate.WaitAsync(token);
            SetBusy(true);
            try { await operation(token); }
            finally
            {
                SetBusy(false);
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or InvalidDataException or OverflowException)
        {
            _status.Text = ex.Message;
        }
    }

    bool TryNormalizeInput(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = "Enter or select a location first.";
            return false;
        }
        try { normalized = LocationHistoryStore.NormalizeWindowsPath(path); }
        catch (ArgumentException ex)
        {
            _status.Text = ex.Message;
            return false;
        }
        if (!Path.IsPathFullyQualified(normalized))
        {
            _status.Text = "Enter a fully qualified location, such as C:\\Data or \\\\server\\share.";
            return false;
        }
        if (normalized.IndexOfAny(['*', '?']) >= 0)
        {
            _status.Text = "Location paths cannot contain wildcard characters.";
            return false;
        }
        return true;
    }

    void SetBusy(bool busy)
    {
        _operationInFlight = busy;
        SetActionState(_locations.SelectedItem as LocationItem);
    }

    sealed record LocationItem(string Path, string DisplayName, bool IsFavorite,
        string FavoriteMarker, string FavoriteDescription, string LastUsed, ulong UseCount)
    {
        public static LocationItem From(ScanLocation location) => new(location.Path,
            location.DisplayName, location.IsFavorite, location.IsFavorite ? "★" : "",
            location.IsFavorite ? "Favorite" : "Recent location",
            location.LastUsedUtc.ToLocalTime().ToString("g"), location.UseCount);
    }
}
