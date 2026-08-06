using System.Security.Principal;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
    ScanResultManaged?       _previousResult;
    ScanResultMetrics?       _metrics;
    ScanNavigation?          _navigation;
    IReadOnlyList<TargetScanResult> _targets = [];
    SizeTreeView?            _treeView;
    Treemap?                 _treemap;
    Stopwatch?               _scanClock;
    LocationHistoryStore?    _locationHistory;
    ShellItemActionsMenu?    _shellActionsMenu;
    bool                     _paused;
    bool                     _scanInProgress;
    bool                     _diagnosticsInProgress;
    bool                     _synchronizingTreeSelection;
    int                      _resultGeneration;

    public MainWindow()
    {
        InitializeComponent();
        SetupControls();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnWindowNavigationKeyDown));
        Loaded += OnLoaded;
    }

    void SetupControls()
    {
        _treeView = new SizeTreeView();
        _treeHost.Child = _treeView;
        _treeView.NodeSelected += OnTreeNodeSelected;
        _treeView.NodeActivated += OnTreeNodeActivated;
        _shellActionsMenu = new ShellItemActionsMenu();
        _shellActionsMenu.ActionFailed += OnShellActionFailed;
        _treeView.ContextMenu = _shellActionsMenu;

        _treemap = new Treemap();
        _treemapHost.Child = _treemap;
        _treemap.PathChanged += OnTreemapPathChanged;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hide elevation badge if running elevated.
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        _elevBadge.Visibility  = elevated ? Visibility.Collapsed : Visibility.Visible;
        _emptyState.Visibility = Visibility.Visible;
        try
        {
            string historyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SizeMonitor", "locations.json");
            _locationHistory = new LocationHistoryStore(historyPath);
            await _locationHistoryView.SetStoreAsync(_locationHistory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidDataException)
        {
            Logger.Error("could not load location history", ex);
        }
    }

    async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_scanInProgress) return;
        string[] paths = ParsePaths(_pathBox.Text);
        if (paths.Length == 0) return;

        ScanOptions? scanOptions;
        try
        {
            scanOptions = BuildScanOptions();
            scanOptions?.Validate();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _statSize.Text = $"Invalid scan options: {ex.Message}";
            return;
        }

        int generation          = ++_resultGeneration;
        _scanInProgress         = true;
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
        _statVolume.Text        = "";
        _statSelection.Text     = "";
        _distributionView.SetResult(null);
        if (_shellActionsMenu is not null) _shellActionsMenu.ItemPath = null;
        _saveSnapshotMenuItem.IsEnabled = false;
        _exportMenuItem.IsEnabled = false;
        _openSnapshotMenuItem.IsEnabled = false;
        _diagnosticsMenuItem.IsEnabled = false;
        _statCurrent.Text       = "Starting scan...";
        _scanProgress.Value     = 0;
        _scanProgress.IsIndeterminate = true;
        _scanProgress.Visibility = Visibility.Visible;
        _paused = false;
        _btnPause.Content = "Pause";

        _cts = new CancellationTokenSource();
        Stopwatch scanClock = Stopwatch.StartNew();
        _scanClock = scanClock;
        var targetProgress = new Dictionary<string, ScanProgress>(StringComparer.OrdinalIgnoreCase);

        var progress = new Progress<TargetScanProgress>(p =>
        {
            targetProgress[p.Path] = p.Current;
            ulong files = SumSaturating(targetProgress.Values.Select(value => value.FilesVisited));
            ulong dirs = SumSaturating(targetProgress.Values.Select(value => value.DirsVisited));
            ulong bytes = SumSaturating(targetProgress.Values.Select(value => value.BytesSeen));
            _statFiles.Text = $"{files:N0} files, {dirs:N0} dirs · {SizeFormatter.FormatBytes(bytes)} scanned";
            _statCurrent.Text = p.Path;
            _statTime.Text = $"Elapsed {scanClock.Elapsed:g}";
            _statTimeSep.Visibility = Visibility.Visible;
            if (p.CompletedTargets > 0)
            {
                _scanProgress.IsIndeterminate = false;
                _scanProgress.Value = 100d * p.CompletedTargets / p.TotalTargets;
                double secondsPerTarget = scanClock.Elapsed.TotalSeconds / p.CompletedTargets;
                double remaining = secondsPerTarget * (p.TotalTargets - p.CompletedTargets);
                _statTime.Text = remaining > 0
                    ? $"Elapsed {scanClock.Elapsed:g} · ETA {TimeSpan.FromSeconds(remaining):g}"
                    : $"Elapsed {scanClock.Elapsed:g} · Finishing...";
            }
        });

        try
        {
            _multiSession = new MultiScanSession(Math.Min(paths.Length, Math.Max(1, Environment.ProcessorCount / 2)));
            IReadOnlyList<TargetScanOutcome> outcomes =
                await _multiSession.ScanOutcomesAsync(paths, progress, _cts.Token, scanOptions);
            TargetScanResult[] targetResults = outcomes.Where(outcome => outcome.Succeeded)
                .Select(outcome => new TargetScanResult(outcome.Path, outcome.Scanner, outcome.Result!))
                .ToArray();
            TargetScanOutcome[] failures = outcomes.Where(outcome => !outcome.Succeeded).ToArray();
            if (targetResults.Length == 0)
                throw new AggregateException("Every scan target failed.",
                    failures.Select(failure => failure.Error ?? new IOException($"Scan failed: {failure.Path}")));
            ScanResultManaged result = ScanResultCombiner.CombineTargets(targetResults);
            if (generation == _resultGeneration)
            {
                await OnScanCompleteAsync(result, targetResults, generation);
                if (_locationHistory is not null)
                {
                    try
                    {
                        foreach (TargetScanResult target in targetResults)
                            await _locationHistory.TouchAsync(target.Path, cancellationToken: CancellationToken.None);
                        _locationHistoryView.RefreshLocations();
                    }
                    catch (Exception historyError) when (historyError is IOException or UnauthorizedAccessException
                                                          or InvalidDataException or ArgumentException)
                    {
                        Logger.Error("could not update location history", historyError);
                        _statCurrent.Text = $"Scan complete; location history was not updated: {historyError.Message}";
                    }
                }
                if (failures.Length > 0)
                {
                    foreach (TargetScanOutcome failure in failures)
                        Logger.Error($"scan target failed: {failure.Path}",
                            failure.Error ?? new IOException("Unknown scan failure."));
                    _statCurrent.Text = $"{failures.Length:N0} of {outcomes.Count:N0} targets failed; successful results are shown.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            _statSize.Text          = "Cancelled";
            _statFiles.Text         = "";
            _statTime.Text          = "";
            _statTimeSep.Visibility = Visibility.Collapsed;
            _statCurrent.Text       = "";
            _emptyState.Visibility  = _result is { Nodes.Length: > 0 }
                ? Visibility.Collapsed : Visibility.Visible;
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
            _scanInProgress = false;
            _btnScan.IsEnabled   = true;
            _btnPause.IsEnabled  = false;
            _btnCancel.IsEnabled = false;
            _openSnapshotMenuItem.IsEnabled = true;
            _saveSnapshotMenuItem.IsEnabled = _result is not null;
            _exportMenuItem.IsEnabled = _result is { Nodes.Length: > 0 };
            _diagnosticsMenuItem.IsEnabled = !_diagnosticsInProgress && !_scanInProgress;
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

    async Task OnScanCompleteAsync(ScanResultManaged result, IReadOnlyList<TargetScanResult> targets, int generation)
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
        await DisplayResultAsync(result, generation, targets);
        await UpdateVolumeStatusAsync(targets, generation);
    }

    void OnWindowNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        bool handled = e.SystemKey switch
        {
            Key.Left => _navigationBar.GoBack(),
            Key.Right => _navigationBar.GoForward(),
            _ => false,
        };
        if (handled) e.Handled = true;
    }

    async Task<bool> DisplayResultAsync(ScanResultManaged result, int generation,
        IReadOnlyList<TargetScanResult>? targets = null)
    {
        (ScanResultMetrics Metrics, ScanNavigation? Navigation) derived = await Task.Run(() =>
        {
            ScanResultMetrics metrics = ScanResultMetrics.Calculate(result);
            ScanNavigation? navigation = result.Nodes.Length == 0
                ? null
                : new ScanNavigation(new ScanNavigationIndex(result));
            return (metrics, navigation);
        });
        if (generation != _resultGeneration) return false;

        if (_result is not null && !ReferenceEquals(_result, result))
            _previousResult = _result;
        _result = result;
        _targets = targets ?? [];
        if (_shellActionsMenu is not null) _shellActionsMenu.ItemPath = null;
        _metrics = derived.Metrics;
        _navigation = derived.Navigation;
        _treeView?.Populate(result);
        _searchView.SetResult(result);
        _navigationBar.SetNavigation(_navigation);
        _emptyItemsView.SetResult(result);
        _distributionView.SetResult(result);
        _anomaliesView.SetResult(result);
        _comparisonView.SetResults(_previousResult, result);
        _duplicatesView.SetRoots((targets ?? []).Select(target => target.Path));
        bool hasNodes = result.Nodes.Length > 0;
        _saveSnapshotMenuItem.IsEnabled = true;
        _exportMenuItem.IsEnabled = hasNodes;
        _emptyState.Visibility = hasNodes ? Visibility.Collapsed : Visibility.Visible;
        _treemap?.SetRoot(result, 0);
        if (hasNodes)
            ShowNodeMetrics(0);
        else
            _statSelection.Text = string.Empty;
        return true;
    }

    async Task UpdateVolumeStatusAsync(IReadOnlyList<TargetScanResult> targets, int generation)
    {
        string[] roots = targets
            .Select(target => Path.GetPathRoot(Path.GetFullPath(target.Path)) ?? target.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var volumes = new List<VolumeStorageInfo>();
        foreach (string root in roots)
        {
            try
            {
                VolumeStorageInfo volume = await Task.Run(() => VolumeStorageInfo.Read(root));
                if (!volumes.Any(item => item.RootPath.Equals(volume.RootPath, StringComparison.OrdinalIgnoreCase)))
                    volumes.Add(volume);
            }
            catch (Exception ex)
            {
                Logger.Error($"could not read volume information for {root}", ex);
            }
        }
        if (generation != _resultGeneration) return;
        if (volumes.Count == 0)
        {
            _statVolume.Text = roots.Length == 0 ? string.Empty : "Volume information unavailable";
            return;
        }

        if (volumes.Count == 1)
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
            $"{volumes.Count} volumes · {SizeFormatter.FormatBytes(used)} used of " +
            $"{SizeFormatter.FormatBytes(total)} ({SizeFormatter.FormatBytes(free)} free)";
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
        if (_synchronizingTreeSelection) return;
        if (_result == null) return;
        if (_shellActionsMenu is not null)
            _shellActionsMenu.ItemPath = ResolveFilesystemPath(nodeIndex);
        ShowNodeMetrics(nodeIndex);
        // Only navigate the treemap into directory nodes; selecting a file node
        // would produce an empty treemap (files have no children).
        if (((_result.Nodes[nodeIndex].Flags & ScanNodeFlags.Directory) != 0))
            _treemap?.SetRoot(_result, nodeIndex);
    }

    void OnTreeNodeActivated(uint nodeIndex)
    {
        _navigationBar.NavigateTo(nodeIndex);
        ActivateNode(nodeIndex, selectTree: false);
    }

    void OnSearchNodeActivated(uint nodeIndex)
    {
        if (_result is null || nodeIndex >= _result.Nodes.Length) return;
        _navigationBar.NavigateTo(nodeIndex);
        if (_shellActionsMenu is not null)
            _shellActionsMenu.ItemPath = ResolveFilesystemPath(nodeIndex);
        ActivateNode(nodeIndex);
    }

    void OnNavigationNodeActivated(uint nodeIndex) => ActivateNode(nodeIndex);

    void OnEmptyItemActivated(uint nodeIndex)
    {
        if (_shellActionsMenu is not null)
            _shellActionsMenu.ItemPath = ResolveFilesystemPath(nodeIndex);
        _navigationBar.NavigateTo(nodeIndex);
        ActivateNode(nodeIndex);
        _contentTabs.SelectedIndex = 0;
    }

    void OnAnomalyNodeActivated(uint nodeIndex)
    {
        _navigationBar.NavigateTo(nodeIndex);
        ActivateNode(nodeIndex);
        _contentTabs.SelectedIndex = 0;
    }

    void OnComparisonNodeActivated(ComparisonNodeActivation activation)
    {
        if (!activation.IsCurrentScan)
        {
            _statCurrent.Text = "This item belongs to the previous scan; load it to explore that node.";
            return;
        }
        _navigationBar.NavigateTo(activation.NodeIndex);
        ActivateNode(activation.NodeIndex);
        _contentTabs.SelectedIndex = 0;
    }

    void OnDuplicatePathActivated(string path)
    {
        try { ShellItemActions.Open(path); }
        catch (Exception ex)
        {
            Logger.Error($"could not open duplicate path: {path}", ex);
            _statCurrent.Text = $"Open failed: {ex.Message}";
        }
    }

    string? ResolveFilesystemPath(uint combinedNodeIndex)
    {
        if (combinedNodeIndex == 0 || _targets.Count == 0) return null;

        uint blockStart = 1;
        foreach (TargetScanResult target in _targets)
        {
            uint blockLength = checked((uint)target.Result.Nodes.Length);
            if (combinedNodeIndex >= blockStart && combinedNodeIndex - blockStart < blockLength)
            {
                uint localIndex = combinedNodeIndex - blockStart;
                var segments = new Stack<string>();
                uint current = localIndex;
                while (target.Result.Nodes[current].Parent != uint.MaxValue)
                {
                    segments.Push(target.Result.GetName(current));
                    current = target.Result.Nodes[current].Parent;
                }

                string path = Path.GetFullPath(target.Path);
                foreach (string segment in segments)
                    path = Path.Combine(path, segment);
                return path;
            }
            blockStart = checked(blockStart + blockLength);
        }
        return null;
    }

    void OnShellActionFailed(object? sender, ShellItemActionFailedEventArgs e)
    {
        Logger.Error($"{e.Action} failed for {e.ItemPath}", e.Exception);
        _statCurrent.Text = $"{e.Action} failed: {e.Exception.Message}";
    }

    void OnHistoryPathActivated(string path)
    {
        if (_scanInProgress) return;
        _pathBox.Text = path;
        _contentTabs.SelectedIndex = 0;
        OnScan(_btnScan, new RoutedEventArgs());
    }

    void ActivateNode(uint nodeIndex, bool selectTree = true)
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
        if (selectTree && _treeView is not null)
        {
            _synchronizingTreeSelection = true;
            try { _treeView.SelectNode(nodeIndex); }
            finally { _synchronizingTreeSelection = false; }
        }
    }

    async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        ScanResultManaged result = _result;
        int generation = _resultGeneration;
        _exportMenuItem.IsEnabled = false;
        _statCurrent.Text = "Exporting results...";
        try
        {
            var service = new ScanResultExportService();
            ScanResultExportResult export = await service.ExportAsync(result, this);
            if (generation == _resultGeneration && ReferenceEquals(result, _result) && !_scanInProgress)
                _statCurrent.Text = export.Status == ScanResultExportStatus.Saved
                    ? $"Exported {export.Format} to {export.Path}"
                    : "Export cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or NotSupportedException)
        {
            Logger.Error("result export failed", ex);
            if (generation == _resultGeneration && !_scanInProgress)
                _statCurrent.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            _exportMenuItem.IsEnabled = _result is not null && !_scanInProgress;
        }
    }

    async void OnSaveSnapshot(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        _saveSnapshotMenuItem.IsEnabled = false;
        try
        {
            ScanSnapshotSaveResult saved = await ScanSnapshotService.SaveAsync(_result, this);
            _statCurrent.Text = saved is ScanSnapshotSaveResult.Saved completed
                ? $"Saved snapshot to {completed.Path}"
                : "Snapshot save cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or NotSupportedException)
        {
            Logger.Error("snapshot save failed", ex);
            _statCurrent.Text = $"Snapshot save failed: {ex.Message}";
        }
        finally
        {
            _saveSnapshotMenuItem.IsEnabled = _result is not null;
        }
    }

    async void OnOpenSnapshot(object sender, RoutedEventArgs e)
    {
        int generation = ++_resultGeneration;
        _openSnapshotMenuItem.IsEnabled = false;
        try
        {
            ScanSnapshotOpenResult opened = await ScanSnapshotService.OpenAsync(this);
            if (opened is not ScanSnapshotOpenResult.Loaded loaded) return;
            if (generation != _resultGeneration || _scanInProgress) return;
            if (!await DisplayResultAsync(loaded.Result, generation)) return;
            _statSize.Text = SizeFormatter.FormatBytes(loaded.Result.TotalBytes);
            _statFiles.Text = $"{loaded.Result.FileCount:N0} files, {loaded.Result.DirCount:N0} dirs";
            _statTime.Text = $"{loaded.Result.ElapsedSec:F1}s saved scan";
            _statTimeSep.Visibility = Visibility.Visible;
            _statScanner.Text = "Saved snapshot";
            _statScannerSep.Visibility = Visibility.Visible;
            _statVolume.Text = $"Opened {loaded.Path}";
            _statCurrent.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or NotSupportedException)
        {
            Logger.Error("snapshot open failed", ex);
            if (generation == _resultGeneration && !_scanInProgress)
                _statCurrent.Text = $"Snapshot open failed: {ex.Message}";
        }
        finally
        {
            _openSnapshotMenuItem.IsEnabled = !_scanInProgress;
            _diagnosticsMenuItem.IsEnabled = !_diagnosticsInProgress && !_scanInProgress;
        }
    }

    async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsInProgress || _scanInProgress) return;
        _diagnosticsInProgress = true;
        _diagnosticsMenuItem.IsEnabled = false;
        int generation = _resultGeneration;
        try
        {
            var service = new DiagnosticBundleService();
            DiagnosticBundleSaveResult saved = await service.SaveAsync(this);
            if (generation == _resultGeneration && !_scanInProgress)
                _statCurrent.Text = saved.Status == DiagnosticBundleSaveStatus.Saved
                    ? $"Saved redacted diagnostics to {saved.Path}"
                    : "Diagnostic export cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            Logger.Error("diagnostic export failed", ex);
            if (generation == _resultGeneration && !_scanInProgress)
                _statCurrent.Text = $"Diagnostic export failed: {ex.Message}";
        }
        finally
        {
            _diagnosticsInProgress = false;
            _diagnosticsMenuItem.IsEnabled = !_scanInProgress;
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
