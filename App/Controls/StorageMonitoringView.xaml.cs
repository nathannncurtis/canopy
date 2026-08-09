using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class StorageMonitoringView : UserControl
{
    readonly List<ContinuousFolderMonitor> _monitors = [];
    readonly List<Row> _rows = [];

    public StorageMonitoringView()
    {
        InitializeComponent();
        Unloaded += async (_, _) => await StopAsync();
    }

    async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_monitors.Count != 0) return;
        string[] paths;
        try
        {
            paths = _path.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { _status.Text = ex.Message; return; }
        if (paths.Length == 0 || paths.Any(path => !Directory.Exists(path)))
        { _status.Text = "Choose one or more existing folders separated by semicolons."; return; }
        if (!double.TryParse(_minutes.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                out double minutes) || minutes is < 0.01 or > 1_440)
        { _status.Text = "Poll interval must be between 0.01 and 1,440 minutes."; return; }

        try
        {
            foreach (string path in paths)
            {
                var monitor = new ContinuousFolderMonitor(path, MeasureAsync,
                    new(PollInterval: TimeSpan.FromMinutes(minutes),
                        CoalescingWindow: TimeSpan.FromMilliseconds(500)),
                    new(MinimumBaselineBytes: 100ul * 1024 * 1024,
                        MinimumAbsoluteGrowthBytes: 100ul * 1024 * 1024,
                        MinimumGrowthBytesPerHour: 500d * 1024 * 1024));
                _monitors.Add(monitor);
                await monitor.StartAsync(new Progress<FolderMonitorSample>(OnSample));
            }
            _start.IsEnabled = false;
            _stop.IsEnabled = true;
            _path.IsEnabled = false;
            _minutes.IsEnabled = false;
            _status.Text = $"Monitoring {paths.Length:N0} folder(s). Close Canopy to keep monitoring from the tray.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await StopAsync();
            _status.Text = $"Could not start monitoring: {ex.Message}";
        }
    }

    async void OnStop(object sender, RoutedEventArgs e) => await StopAsync();

    async Task StopAsync()
    {
        if (_monitors.Count == 0) return;
        ContinuousFolderMonitor[] monitors = _monitors.ToArray();
        _monitors.Clear();
        foreach (ContinuousFolderMonitor monitor in monitors)
            await monitor.DisposeAsync();
        _start.IsEnabled = true;
        _stop.IsEnabled = false;
        _path.IsEnabled = true;
        _minutes.IsEnabled = true;
        _status.Text = "Folder monitoring stopped.";
    }

    void OnSample(FolderMonitorSample sample)
    {
        _rows.Insert(0, new(sample.Path, sample.CapturedUtc.ToLocalTime().ToString("g"),
            SizeFormatter.FormatBytes(sample.TotalBytes), sample.Reason.ToString(),
            sample.Error ?? (sample.GrowthAlert is { } alert
                ? $"+{SizeFormatter.FormatBytes(alert.GrowthBytes)} ({SizeFormatter.FormatBytes((ulong)alert.GrowthBytesPerHour)}/hour)"
                : string.Empty)));
        if (_rows.Count > 200) _rows.RemoveRange(200, _rows.Count - 200);
        _samples.ItemsSource = null;
        _samples.ItemsSource = _rows;
        _status.Text = sample.Error is null
            ? $"Last reconciled {sample.CapturedUtc.ToLocalTime():T} · {sample.Reason}."
            : $"Reconciliation failed: {sample.Error}";
    }

    static async Task<ulong> MeasureAsync(string path, CancellationToken token)
    {
        using ScanSession session = ScanSession.Start(path, null,
            new ScanOptions { ForceDirectoryScanner = true });
        ScanResultManaged result = await session.WaitAsync(token);
        return result.TotalBytes;
    }

    sealed record Row(string Path, string Captured, string Size, string Reason, string Alert);
}
