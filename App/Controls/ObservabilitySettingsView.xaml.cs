using System.IO;
using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class ObservabilitySettingsView : UserControl
{
    readonly ObservabilityConsentStore _store;

    public ObservabilitySettingsView()
    {
        InitializeComponent();
        _store = new ObservabilityConsentStore(AppDataPaths.ObservabilityDirectory);
        Loaded += OnLoaded;
    }

    public event Action<ObservabilityConsent>? ConsentChanged;

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try { Apply(await _store.LoadAsync()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { _status.Text = "Settings could not be loaded; reporting remains disabled."; }
    }

    async void OnSave(object sender, RoutedEventArgs e)
    {
        ObservabilityConsent consent = Read();
        try
        {
            await _store.SaveAsync(consent);
            Logger.Level = consent.LogLevel;
            ConsentChanged?.Invoke(consent);
            _status.Text = "Privacy and diagnostics settings saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = "Settings could not be saved. No consent change was applied."; }
    }

    async void OnDisableAndDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            await _store.DeleteLocalDataAsync();
            var disabled = new ObservabilityConsent();
            Apply(disabled);
            ConsentChanged?.Invoke(disabled);
            _status.Text = "Reporting disabled and local observability data deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = "Local observability data could not be fully deleted."; }
    }

    ObservabilityConsent Read() => new()
    {
        CrashReports = _crashReports.IsChecked == true,
        PerformanceTelemetry = _performance.IsChecked == true,
        LogLevel = (DiagnosticLogLevel)Math.Clamp(_logLevel.SelectedIndex, 0, 3),
    };

    void Apply(ObservabilityConsent consent)
    {
        _crashReports.IsChecked = consent.CrashReports;
        _performance.IsChecked = consent.PerformanceTelemetry;
        _logLevel.SelectedIndex = (int)consent.LogLevel;
        Logger.Level = consent.LogLevel;
    }
}
