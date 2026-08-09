using System.IO;
using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class ShortcutSettingsView : UserControl
{
    AppCommandRegistry? _registry;
    ShortcutOverrideStore? _store;
    public event Action? ShortcutsChanged;

    public ShortcutSettingsView() => InitializeComponent();

    public void SetRegistry(AppCommandRegistry registry, ShortcutOverrideStore store)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Refresh();
    }

    void Refresh(string? selectId = null)
    {
        if (_registry is null) return;
        CommandRow[] rows = _registry.Commands.Select(command => new CommandRow(command.Id,
            command.Title, command.Category, _registry.GetShortcut(command.Id) ?? "—",
            command.DefaultShortcut ?? "—")).ToArray();
        _commands.ItemsSource = rows;
        _commands.SelectedItem = rows.FirstOrDefault(row => row.Id == selectId);
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_commands.SelectedItem is CommandRow row)
            _shortcut.Text = row.Shortcut == "—" ? string.Empty : row.Shortcut;
    }

    async void OnApply(object sender, RoutedEventArgs e) =>
        await ChangeAsync(string.IsNullOrWhiteSpace(_shortcut.Text) ? null : _shortcut.Text.Trim());

    async void OnClear(object sender, RoutedEventArgs e) => await ChangeAsync(null);

    async void OnReset(object sender, RoutedEventArgs e)
    {
        if (_commands.SelectedItem is not CommandRow row || _registry is null || _store is null) return;
        var overrides = new Dictionary<string, string?>(_registry.GetOverrides(), StringComparer.OrdinalIgnoreCase);
        string? before = _registry.GetShortcut(row.Id);
        overrides.Remove(row.Id);
        try
        {
            _registry.ResetShortcut(row.Id);
            await _store.SaveAsync(overrides);
            _status.Text = $"Restored the default shortcut for {row.Title}.";
            Refresh(row.Id); ShortcutsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            try { _registry.SetShortcut(row.Id, before); } catch (ArgumentException) { }
            _status.Text = ex.Message;
        }
    }

    async Task ChangeAsync(string? shortcut)
    {
        if (_commands.SelectedItem is not CommandRow row || _registry is null || _store is null)
        { _status.Text = "Select a command first."; return; }
        bool hadOverride = _registry.GetOverrides().ContainsKey(row.Id);
        string? before = _registry.GetShortcut(row.Id);
        try
        {
            _registry.SetShortcut(row.Id, shortcut);
            await _store.SaveAsync(_registry.GetOverrides());
            _status.Text = shortcut is null ? $"Removed the shortcut for {row.Title}." : $"{shortcut} now runs {row.Title}.";
            Refresh(row.Id); ShortcutsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            try
            {
                if (hadOverride) _registry.SetShortcut(row.Id, before);
                else _registry.ResetShortcut(row.Id);
            }
            catch (ArgumentException) { }
            _status.Text = ex.Message;
        }
    }

    sealed record CommandRow(string Id, string Title, string Category, string Shortcut, string DefaultShortcut);
}
