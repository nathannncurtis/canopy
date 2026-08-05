using System.Security.Principal;
using System.Diagnostics;
using System.IO;
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
    ScanResultMetrics?       _metrics;
    ScanNavigation?          _navigation;
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

        ScanOptions? scanOptions;
        try
        {
            scanOptions = BuildScanOptions();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _statSize.Text = $"Invalid scan options: {ex.Message}";
            return;
        }

        _btnScan.IsEnabled      = false;
        _btnPause.IsEnabled     = true;
        _btnCancel.IsEnabled    = true;
        _emptyState.Visibility  = Visibility.Collapsed;
        _searchView.SetResult(null);
        _statSize.Text          = "Scanning...";
        _statFiles.Text         = "";
        _statTime.Text          = "";
        _statTimeSep.Visibility = Visibility.Collapsed;
        _statScannerSep.Visibility = Visibility.Collapsed;
        _statScanner.Text       = "";
        _statVolume.Text        = "";
        _statSelection.Text     = "";
        _metrics                = null;
        _navigation             = null;
        _navigationBar.SetNavigation(null);
        _emptyItemsView.SetResult(null);
        _btnExport.IsEnabled    = false;
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
                await _multiSession.ScanAsync(paths, progress, _cts.Token, scanOptions);
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
        _metrics = ScanResultMetrics.Calculate(result);
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
        UpdateVolumeStatus(targets);

        _treeView?.Populate(result);
        _treemap?.SetRoot(result, 0);
        _searchView.SetResult(result);
        _navigation = result.Nodes.Length == 0
            ? null
            : new ScanNavigation(new ScanNavigationIndex(result));
        _navigationBar.SetNavigation(_navigation);
        _emptyItemsView.SetResult(result);
        _btnExport.IsEnabled = result.Nodes.Length > 0;
        if (result.Nodes.Length > 0) ShowNodeMetrics(0);
    }

    void UpdateVolumeStatus(IReadOnlyList<TargetScanResult> targets)
    {
        try
        {
            VolumeStorageInfo[] volumes = targets
                .Select(target => VolumeStorageInfo.Read(target.Path))
                .GroupBy(volume => volume.RootPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (volumes.Length == 0) return;

            if (volumes.Length == 1)
            {
                VolumeStorageInfo volume = volumes[0];
                string identity = string.IsNullOrWhiteSpace(volume.Label)
                    ? volume.RootPath
                    : $"{volume.Label} ({volume.RootPath})";
                _statVolume.Text =
                    $"{identity} · {volume.FileSystem} · " +
                    $"{SizeFormatter.FormatBytes(volume.UsedBytes)} used of " +
                    $"{SizeFormatter.FormatBytes(volume.TotalBytes)} " +
                    $"({SizeFormatter.FormatBytes(volume.FreeBytes)} free) · " +
                    $"{SizeFormatter.FormatBytes(volume.ClusterSize)} clusters · " +
                    $"serial {volume.SerialNumberText}";
                return;
            }

            ulong total = SumSaturating(volumes.Select(volume => volume.TotalBytes));
            ulong free = SumSaturating(volumes.Select(volume => volume.FreeBytes));
            ulong used = total >= free ? total - free : 0;
            _statVolume.Text =
                $"{volumes.Length} volumes · {SizeFormatter.FormatBytes(used)} used of " +
                $"{SizeFormatter.FormatBytes(total)} ({SizeFormatter.FormatBytes(free)} free)";
        }
        catch (Exception ex)
        {
            Logger.Error("could not read volume information", ex);
            _statVolume.Text = "Volume information unavailable";
        }
    }

    static ulong SumSaturating(IEnumerable<ulong> values)
    {
        ulong total = 0;
        foreach (ulong value in values)
            total = ulong.MaxValue - total < value ? ulong.MaxValue : total + value;
        return total;
    }

    static string[] ParsePaths(string text) => text
        .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => path.Trim('"'))
        .Where(path => path.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    ScanOptions? BuildScanOptions()
    {
        string[] patterns = ParseOptionList(_excludePatternsBox.Text);
        string[] extensions = ParseOptionList(_excludeExtensionsBox.Text);
        ulong? minimumSize = ParseOptionalSize(_minimumSizeBox.Text, "minimum size");
        ulong? maximumSize = ParseOptionalSize(_maximumSizeBox.Text, "maximum size");
        uint? maximumDepth = ParseOptionalUInt(_maximumDepthBox.Text, "maximum depth");
        uint? workerThreads = ParseOptionalUInt(_workerThreadsBox.Text, "worker threads");
        bool hasOptions = patterns.Length > 0 || extensions.Length > 0 ||
            minimumSize.HasValue || maximumSize.HasValue || maximumDepth.HasValue ||
            workerThreads.HasValue || _excludeHidden.IsChecked == true ||
            _excludeSystem.IsChecked == true || _excludeTemporary.IsChecked == true ||
            _excludeReparse.IsChecked == true;
        if (!hasOptions) return null;

        return new ScanOptions
        {
            ExcludedPatterns = patterns,
            ExcludedExtensions = extensions,
            MinimumFileSize = minimumSize ?? 0,
            MaximumFileSize = maximumSize,
            MaximumDepth = maximumDepth,
            WorkerThreads = workerThreads,
            IncludeHidden = _excludeHidden.IsChecked != true,
            IncludeSystem = _excludeSystem.IsChecked != true,
            IncludeTemporary = _excludeTemporary.IsChecked != true,
            IncludeReparsePoints = _excludeReparse.IsChecked != true,
            ForceDirectoryScanner = true,
        };
    }

    static string[] ParseOptionList(string text) => text
        .Split([';', ',', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    static ulong? ParseOptionalSize(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (ByteSizeParser.TryParse(text, out ulong bytes)) return bytes;
        throw new FormatException($"'{text}' is not a valid {name}.");
    }

    static uint? ParseOptionalUInt(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (uint.TryParse(text, out uint value) && value > 0) return value;
        throw new FormatException($"'{text}' is not a valid {name}.");
    }

    void OnTreeNodeSelected(uint nodeIndex)
    {
        if (_result == null) return;
        _navigationBar.NavigateTo(nodeIndex);
        ShowNodeMetrics(nodeIndex);
        // Only navigate the treemap into directory nodes; selecting a file node
        // would produce an empty treemap (files have no children).
        if (((_result.Nodes[nodeIndex].Flags & ScanNodeFlags.Directory) != 0))
            _treemap?.SetRoot(_result, nodeIndex);
    }

    void OnSearchNodeActivated(uint nodeIndex)
    {
        if (_result is null || nodeIndex >= _result.Nodes.Length) return;
        _navigationBar.NavigateTo(nodeIndex);
        ActivateNode(nodeIndex);
    }

    void OnNavigationNodeActivated(uint nodeIndex) => ActivateNode(nodeIndex);

    void OnEmptyItemActivated(uint nodeIndex)
    {
        _navigationBar.NavigateTo(nodeIndex);
        ActivateNode(nodeIndex);
        _contentTabs.SelectedIndex = 0;
    }

    void ActivateNode(uint nodeIndex)
    {
        if (_result is null || nodeIndex >= _result.Nodes.Length) return;
        ShowNodeMetrics(nodeIndex);
        ScanNode node = _result.Nodes[nodeIndex];
        uint navigationRoot = (node.Flags & ScanNodeFlags.Directory) != 0
            ? nodeIndex
            : node.Parent;
        if (navigationRoot != uint.MaxValue)
        {
            _treemap?.SetRoot(_result, navigationRoot);
            _contentTabs.SelectedIndex = 0;
        }
    }

    async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        _btnExport.IsEnabled = false;
        _statCurrent.Text = "Exporting results...";
        try
        {
            var service = new ScanResultExportService();
            ScanResultExportResult export = await service.ExportAsync(_result, this);
            _statCurrent.Text = export.Status == ScanResultExportStatus.Saved
                ? $"Exported {export.Format} to {export.Path}"
                : "Export cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or NotSupportedException)
        {
            Logger.Error("result export failed", ex);
            _statCurrent.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            _btnExport.IsEnabled = _result is not null;
        }
    }

    void ShowNodeMetrics(uint nodeIndex)
    {
        if (_result is null || _metrics is null || nodeIndex >= _result.Nodes.Length)
            return;
        ScanNodeMetrics metrics = _metrics[nodeIndex];
        string average = metrics.FileCount == 0
            ? "no files"
            : $"{SizeFormatter.FormatBytes(metrics.AverageFileSize)} average";
        _statSelection.Text =
            $"{_result.GetName(nodeIndex)} · {metrics.FileCount:N0} files, " +
            $"{metrics.DirectoryCount:N0} dirs · {average} · " +
            $"depth {metrics.DescendantDepth:N0} · " +
            $"{metrics.PercentageOfParent:F1}% of parent · " +
            $"{metrics.PercentageOfScan:F1}% of scan";
    }

    void OnTreemapPathChanged(IReadOnlyList<string> path)
    {
        Title = path.Count > 0 ? $"Size Monitor — {string.Join(" > ", path)}" : "Size Monitor";
    }
}
