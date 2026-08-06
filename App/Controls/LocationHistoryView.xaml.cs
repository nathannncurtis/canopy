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
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
            }
        };
    }

    public LocationHistoryStore? Store => _store;

    public async Task SetStoreAsync(LocationHistoryStore? store, bool load = true,
        CancellationToken cancellationToken = default)
    {
        _store = store;
        if (store is not null && load)
            await store.LoadAsync(cancellationToken);
        RefreshLocations();
    }

    public void RefreshLocations()
    {
        IReadOnlyList<ScanLocation> locations = _store?.Locations ?? [];
        _locations.ItemsSource = locations.Select(LocationItem.From).ToArray();
        _status.Text = _store is null
            ? "Location history is unavailable."
            : locations.Count == 0 ? "No favorite or recent locations yet."
            : $"{locations.Count(x => x.IsFavorite):N0} favorites, {locations.Count(x => !x.IsFavorite):N0} recent";
        SetActionState(null);
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

    async void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        string path = SelectedOrTypedPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            LocationItem? selected = _locations.SelectedItem as LocationItem;
            if (selected?.IsFavorite == true)
                await _store.UnpinAsync(path, _lifetime.Token);
            else
                await _store.PinAsync(path, cancellationToken: _lifetime.Token);
            RefreshLocations();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            _status.Text = ex.Message;
        }
    }

    async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_store is null || _locations.SelectedItem is not LocationItem selected) return;
        try
        {
            await _store.RemoveAsync(selected.Path, _lifetime.Token);
            RefreshLocations();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = ex.Message;
        }
    }

    async Task ActivateAsync(string path)
    {
        if (_store is null)
        {
            _status.Text = "Location history is unavailable.";
            return;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = "Enter or select a location first.";
            return;
        }
        try
        {
            string normalized = LocationHistoryStore.NormalizeWindowsPath(path);
            await _store.TouchAsync(normalized, cancellationToken: _lifetime.Token);
            RefreshLocations();
            _pathBox.Text = normalized;
            PathActivated?.Invoke(normalized);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or InvalidDataException or OverflowException)
        {
            _status.Text = ex.Message;
        }
    }

    string SelectedOrTypedPath() => (_locations.SelectedItem as LocationItem)?.Path ?? _pathBox.Text;

    void SetActionState(LocationItem? selected)
    {
        _pinButton.IsEnabled = _store is not null;
        _pinButton.Content = selected?.IsFavorite == true ? "Unfavorite" : "Favorite";
        _removeButton.IsEnabled = selected is not null && _store is not null;
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
