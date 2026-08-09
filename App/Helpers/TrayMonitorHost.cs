using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;
using SizeMonitor.Interop;

namespace SizeMonitor.Helpers;

public sealed class TrayMonitorHost : IAsyncDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _pauseItem;
    readonly Forms.ToolStripMenuItem _historyItem;
    readonly Forms.ToolStripMenuItem _scanItem;
    readonly Action _open;
    readonly Action<string> _scan;
    readonly DriveHistoryStore _history;
    readonly Dictionary<string, DriveMonitorUpdate> _latest = new(StringComparer.OrdinalIgnoreCase);
    readonly string[] _roots;
    CancellationTokenSource? _monitorCancellation;
    Task? _monitorTask;
    bool _disposed;

    public TrayMonitorHost(Action open, Action<string> scan, Func<Task> exit)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        ArgumentNullException.ThrowIfNull(exit);
        _history = new DriveHistoryStore(AppDataPaths.DriveHistory,
            new(MaximumPointsPerDrive: 2_880, MaximumAge: TimeSpan.FromDays(30)));
        _roots = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => drive.RootDirectory.FullName)
            .Take(32).ToArray();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Canopy", null, (_, _) => _open());
        _scanItem = new Forms.ToolStripMenuItem("Scan drive");
        menu.Items.Add(_scanItem);
        _historyItem = new Forms.ToolStripMenuItem("Drive history");
        menu.Items.Add(_historyItem);
        _pauseItem = new Forms.ToolStripMenuItem("Pause monitoring", null, (_, _) => ToggleMonitoring());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Canopy", null, async (_, _) => await exit());

        Icon icon;
        try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { icon = SystemIcons.Application; }
        _icon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Canopy storage monitor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _open();
        foreach (string root in _roots)
            _scanItem.DropDownItems.Add(root, null, (_, _) => _scan(root));
    }

    public async Task StartAsync()
    {
        ThrowIfDisposed();
        try { await _history.LoadAsync().ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Logger.Error("drive history could not be loaded", ex); }
        StartMonitoring();
    }

    void StartMonitoring()
    {
        if (_roots.Length == 0 || _monitorTask is not null) return;
        _monitorCancellation = new CancellationTokenSource();
        var service = new DriveMonitorService(_roots, _history,
            new(MinimumFreeBytes: 10ul * 1024 * 1024 * 1024,
                MinimumFreePercent: 10,
                RecoveryHysteresisBytes: 2ul * 1024 * 1024 * 1024,
                RecoveryHysteresisPercent: 2,
                Cooldown: TimeSpan.FromHours(1)),
            TimeSpan.FromMinutes(1));
        var progress = new Progress<DriveMonitorUpdate>(OnUpdate);
        _monitorTask = MonitorAsync(service, progress, _monitorCancellation.Token);
        _pauseItem.Text = "Pause monitoring";
    }

    static async Task MonitorAsync(DriveMonitorService service, IProgress<DriveMonitorUpdate> progress,
        CancellationToken token)
    {
        try { await service.RunAsync(progress, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Logger.Error("tray capacity monitoring stopped", ex); }
    }

    void OnUpdate(DriveMonitorUpdate update)
    {
        if (_disposed) return;
        _latest[update.Volume.RootPath] = update;
        string summary = string.Join(" · ", _latest.Values.OrderBy(item => item.Volume.RootPath)
            .Select(item => $"{item.Volume.RootPath} {item.History.FreePercent:F0}% free"));
        _icon.Text = summary.Length is > 0 and <= 63 ? summary : "Canopy storage monitor";
        RebuildHistoryMenu();
        if (update.Alert is { } alert)
        {
            _icon.BalloonTipTitle = alert.IsRecovery ? "Storage recovered" : "Low disk space";
            _icon.BalloonTipText = alert.Message;
            _icon.BalloonTipIcon = alert.IsRecovery ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning;
            _icon.ShowBalloonTip(5_000);
        }
    }

    void RebuildHistoryMenu()
    {
        _historyItem.DropDownItems.Clear();
        foreach (string root in _roots)
        {
            IReadOnlyList<DriveHistoryPoint> points = _history.GetDrive(root, 120);
            string text = points.Count == 0
                ? $"{root} — no samples"
                : $"{root} — {points[^1].FreePercent:F1}% free ({points.Count} points)";
            var item = new Forms.ToolStripMenuItem(text) { Enabled = points.Count > 0 };
            item.Click += (_, _) => new DriveHistoryWindow(root, _history.GetDrive(root, 120)).Show();
            _historyItem.DropDownItems.Add(item);
        }
    }

    async void ToggleMonitoring()
    {
        if (_monitorTask is null)
        {
            StartMonitoring();
            return;
        }
        CancellationTokenSource cancellation = _monitorCancellation!;
        Task monitor = _monitorTask;
        cancellation.Cancel();
        try { await monitor.ConfigureAwait(true); } catch { }
        cancellation.Dispose();
        _monitorCancellation = null;
        _monitorTask = null;
        _pauseItem.Text = "Resume monitoring";
        _icon.Text = "Canopy storage monitor (paused)";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorCancellation?.Cancel();
        if (_monitorTask is not null)
            try { await _monitorTask.ConfigureAwait(true); } catch { }
        _monitorCancellation?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
