using System.Security.Principal;
using System.Diagnostics;
using System.Windows;
using SizeMonitor.Controls;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;
using Wpf.Ui.Controls;

namespace SizeMonitor;

public partial class MainWindow : FluentWindow
{
    MultiScanSession?        _multiSession;
    CancellationTokenSource? _cts;
    ScanResultManaged?       _result;
    SizeTreeView?            _treeView;
    Treemap?                 _treemap;
    Stopwatch?               _scanClock;
    bool                     _paused;

    public MainWindow()
    {
        InitializeComponent();
        SetupControls();
        Loaded += OnLoaded;
    }

    void SetupControls()
    {
        _treeView = new SizeTreeView();
        _treeHost.Child = _treeView;
        _treeView.NodeSelected += OnTreeNodeSelected;

        _treemap = new Treemap();
        _treemapHost.Child = _treemap;
        _treemap.PathChanged += OnTreemapPathChanged;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hide elevation badge if running elevated.
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        _elevBadge.Visibility  = elevated ? Visibility.Collapsed : Visibility.Visible;
        _emptyState.Visibility = Visibility.Visible;
    }

    async void OnScan(object sender, RoutedEventArgs e)
    {
        string[] paths = ParsePaths(_pathBox.Text);
        if (paths.Length == 0) return;

        _btnScan.IsEnabled      = false;
        _btnPause.IsEnabled     = true;
        _btnCancel.IsEnabled    = true;
        _emptyState.Visibility  = Visibility.Collapsed;
        _statSize.Text          = "Scanning...";
        _statFiles.Text         = "";
        _statTime.Text          = "";
        _statTimeSep.Visibility = Visibility.Collapsed;
        _statScannerSep.Visibility = Visibility.Collapsed;
        _statScanner.Text       = "";
        _statCurrent.Text       = "Starting scan...";
        _scanProgress.Value     = 0;
        _scanProgress.IsIndeterminate = true;
        _scanProgress.Visibility = Visibility.Visible;
        _paused = false;
        _btnPause.Content = "Pause";

        _cts = new CancellationTokenSource();
        Stopwatch scanClock = Stopwatch.StartNew();
        _scanClock = scanClock;

        var progress = new Progress<TargetScanProgress>(p =>
        {
            _statFiles.Text = $"{p.Current.FilesVisited:N0} files, {p.Current.DirsVisited:N0} dirs";
            _statCurrent.Text = p.Path;
            if (p.CompletedTargets > 0)
            {
                _scanProgress.IsIndeterminate = false;
                _scanProgress.Value = 100d * p.CompletedTargets / p.TotalTargets;
                double secondsPerTarget = scanClock.Elapsed.TotalSeconds / p.CompletedTargets;
                double remaining = secondsPerTarget * (p.TotalTargets - p.CompletedTargets);
                _statTime.Text = remaining > 0 ? $"ETA {TimeSpan.FromSeconds(remaining):g}" : "Finishing...";
                _statTimeSep.Visibility = Visibility.Visible;
            }
        });

        try
        {
            _multiSession = new MultiScanSession(Math.Min(paths.Length, Math.Max(1, Environment.ProcessorCount / 2)));
            IReadOnlyList<TargetScanResult> targetResults =
                await _multiSession.ScanAsync(paths, progress, _cts.Token);
            _result = ScanResultCombiner.CombineTargets(targetResults);
            OnScanComplete(_result, targetResults);
        }
        catch (OperationCanceledException)
        {
            _statSize.Text          = "Cancelled";
            _statFiles.Text         = "";
            _statTime.Text          = "";
            _statTimeSep.Visibility = Visibility.Collapsed;
            _statCurrent.Text       = "";
            _emptyState.Visibility  = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Logger.Error($"scan failed: {_pathBox.Text.Trim()}", ex);
            _statSize.Text          = $"Error: {ex.Message}";
            _statFiles.Text         = "";
            _statTime.Text          = "";
            _statTimeSep.Visibility = Visibility.Collapsed;
            _statCurrent.Text       = "";
        }
        finally
        {
            _btnScan.IsEnabled   = true;
            _btnPause.IsEnabled  = false;
            _btnCancel.IsEnabled = false;
            _scanProgress.Visibility = Visibility.Collapsed;
            if (_multiSession is not null)
                await _multiSession.DisposeAsync();
            _multiSession = null;
            _cts?.Dispose();
            _cts = null;
            _scanClock?.Stop();
            _scanClock = null;
        }
    }

    void OnPause(object sender, RoutedEventArgs e)
    {
        if (_multiSession is null) return;
        _paused = !_paused;
        if (_paused)
        {
            _multiSession.Pause();
            _scanClock?.Stop();
            _btnPause.Content = "Resume";
            _statCurrent.Text = "Paused";
        }
        else
        {
            _multiSession.Resume();
            _scanClock?.Start();
            _btnPause.Content = "Pause";
        }
    }

    void OnCancel(object sender, RoutedEventArgs e)
    {
        _multiSession?.Cancel();
        _cts?.Cancel();
    }

    void OnScanComplete(ScanResultManaged result, IReadOnlyList<TargetScanResult> targets)
    {
        _statSize.Text          = Helpers.SizeFormatter.FormatBytes(result.TotalBytes);
        _statFiles.Text         = $"{result.FileCount:N0} files, {result.DirCount:N0} dirs";
        _statTime.Text          = $"{result.ElapsedSec:F1}s";
        _statTimeSep.Visibility = Visibility.Visible;
        int mftCount = targets.Count(target => target.Scanner == ScannerKind.Mft);
        int directoryCount = targets.Count - mftCount;
        _statScanner.Text = targets.Count == 1
            ? $"{targets[0].Scanner} scanner"
            : $"{targets.Count} targets ({mftCount} MFT, {directoryCount} directory)";
        _statScannerSep.Visibility = Visibility.Visible;
        _statCurrent.Text = "";

        _treeView?.Populate(result);
        _treemap?.SetRoot(result, 0);
    }

    static string[] ParsePaths(string text) => text
        .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => path.Trim('"'))
        .Where(path => path.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    void OnTreeNodeSelected(uint nodeIndex)
    {
        if (_result == null) return;
        // Only navigate the treemap into directory nodes; selecting a file node
        // would produce an empty treemap (files have no children).
        if (((_result.Nodes[nodeIndex].Flags & ScanNodeFlags.Directory) != 0))
            _treemap?.SetRoot(_result, nodeIndex);
    }

    void OnTreemapPathChanged(IReadOnlyList<string> path)
    {
        Title = path.Count > 0 ? $"Size Monitor — {string.Join(" > ", path)}" : "Size Monitor";
    }
}
