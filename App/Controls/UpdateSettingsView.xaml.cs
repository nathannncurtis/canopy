using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class UpdateSettingsView : UserControl
{
    static readonly Uri ManifestUri = new("https://raw.githubusercontent.com/nathannncurtis/canopy/main/update-manifest.json");
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly ApplicationUpdateService _updates;
    ApplicationUpdate? _available;
    string? _downloadedPath;

    public UpdateSettingsView()
    {
        InitializeComponent();
        _updates = new(_http, ManifestUri, TimeSpan.FromSeconds(10));
        _mode.Text = AppDataPaths.IsPortable ? "Portable mode is active." : "Installed mode is active.";
        _dataPath.Text = $"Application data: {AppDataPaths.DataDirectory}";
        _status.Text = $"Current version: {CurrentVersion()}";
    }

    async void OnCheck(object sender, RoutedEventArgs e)
    {
        _check.IsEnabled = false; _download.Visibility = Visibility.Collapsed; _open.Visibility = Visibility.Collapsed;
        _status.Text = "Checking securely…"; _notes.Visibility = Visibility.Collapsed;
        try
        {
            UpdateCheckResult result = await _updates.CheckAsync(CurrentVersion(),
                _channel.SelectedIndex == 0 ? UpdateChannel.Stable : UpdateChannel.Preview);
            _available = result.Update;
            _status.Text = result.Message;
            if (_available is not null)
            {
                _notes.Text = string.IsNullOrWhiteSpace(_available.ReleaseNotes) ? "No release notes were supplied." : _available.ReleaseNotes;
                _notes.Visibility = Visibility.Visible;
                _download.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException)
        { _available = null; _status.Text = $"Update check failed: {ex.Message}"; }
        finally { _check.IsEnabled = true; }
    }

    async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_available is null) return;
        var dialog = new SaveFileDialog { Title = "Save verified Canopy update", FileName = $"Canopy-{_available.Version}.exe", Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _download.IsEnabled = false; _status.Text = "Downloading and verifying SHA-256…";
        try
        {
            await _updates.DownloadAsync(_available, dialog.FileName);
            _downloadedPath = dialog.FileName;
            _status.Text = "Download verified. Canopy has not opened or installed it.";
            _open.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { _downloadedPath = null; _open.Visibility = Visibility.Collapsed; _status.Text = $"Update download failed: {ex.Message}"; }
        finally { _download.IsEnabled = true; }
    }

    void OnOpenInstaller(object sender, RoutedEventArgs e)
    {
        if (_downloadedPath is null || !File.Exists(_downloadedPath)) { _status.Text = "The verified download is no longer available."; return; }
        if (System.Windows.MessageBox.Show("Open the verified update now? The installer will ask before making changes. Canopy will not install it automatically.", "Canopy update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(_downloadedPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = $"Could not open the update: {ex.Message}"; }
    }

    static SemanticVersion CurrentVersion()
    {
        Version version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
        return new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }
}
