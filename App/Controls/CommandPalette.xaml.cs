using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class CommandPalette : UserControl
{
    AppCommandRegistry? _registry;
    bool _executing;
    public event Action? CloseRequested;

    public CommandPalette() { InitializeComponent(); _status.Text = "No command registry configured."; }
    public void SetRegistry(AppCommandRegistry? registry) { _registry = registry; Refresh(); }
    public void Open() { _query.Text = string.Empty; Refresh(); _query.Focus(); Keyboard.Focus(_query); }

    void OnQueryChanged(object sender, TextChangedEventArgs e) => Refresh();
    void Refresh()
    {
        IReadOnlyList<AppCommandMatch> matches = _registry?.Search(_query.Text) ?? [];
        _results.ItemsSource = matches; _results.SelectedIndex = matches.Count > 0 ? 0 : -1;
        _status.Text = _registry is null ? "No command registry configured." : $"{matches.Count:N0} available commands · Enter to run · Escape to close";
    }
    async Task ExecuteSelectedAsync()
    {
        if (_executing || _registry is null || _results.SelectedItem is not AppCommandMatch match) return;
        _executing = true; IsEnabled = false; _status.Text = $"Running {match.Command.Title}…";
        try
        {
            if (await _registry.ExecuteAsync(match.Command.Id)) CloseRequested?.Invoke();
            else _status.Text = "That command is not currently available.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { _status.Text = $"Command failed: {ex.Message}"; }
        finally { _executing = false; IsEnabled = true; }
    }
    void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _results.Items.Count > 0) { _results.SelectedIndex = Math.Min(_results.SelectedIndex + 1, _results.Items.Count - 1); _results.ScrollIntoView(_results.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Up && _results.Items.Count > 0) { _results.SelectedIndex = Math.Max(0, _results.SelectedIndex - 1); _results.ScrollIntoView(_results.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Enter) { _ = ExecuteSelectedAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseRequested?.Invoke(); e.Handled = true; }
    }
    void OnResultKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { _ = ExecuteSelectedAsync(); e.Handled = true; } }
    void OnResultDoubleClick(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) _ = ExecuteSelectedAsync(); }
}
