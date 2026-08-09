using System.Security.Principal;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    IReadOnlyList<string>      _summaryLimitations = [];
    SizeTreeView?            _treeView;
    Treemap?                 _treemap;
    Stopwatch?               _scanClock;
    LocationHistoryStore?    _locationHistory;
    ShellItemActionsMenu?    _shellActionsMenu;
    readonly ScanCompletionNotificationService _notificationService = new();
    readonly AppCommandRegistry _commands = new();
    readonly ShortcutOverrideStore _shortcutStore = new(AppDataPaths.ShortcutOverrides);
    readonly HashSet<string> _dynamicCommandIds = new(StringComparer.OrdinalIgnoreCase);
    bool                     _paused;
    bool                     _scanInProgress;
    bool                     _diagnosticsInProgress;
    bool                     _synchronizingTreeSelection;
    int                      _resultGeneration;

    public MainWindow()
    {
        InitializeComponent();
        SetupControls();
        SetupCommands();
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

    void SetupCommands()
    {
        _commands.Register(new(CanopyCommandIds.PaletteOpen, "Open command palette", "Application",
            "Search and run Canopy commands.", "Ctrl+Shift+P", ["commands"]),
            _ => { OpenCommandPalette(); return Task.CompletedTask; });
        _commands.Register(new(CanopyCommandIds.ScanStart, "Start scan", "Scan",
            "Start scanning the configured paths.", "Ctrl+Enter"),
            _ => { OnScan(this, new RoutedEventArgs()); return Task.CompletedTask; },
            () => !_scanInProgress && !string.IsNullOrWhiteSpace(_pathBox.Text));
        _commands.Register(new(CanopyCommandIds.ScanCancel, "Cancel scan", "Scan",
            "Cancel the active scan."),
            _ => { OnCancel(this, new RoutedEventArgs()); return Task.CompletedTask; },
            () => _scanInProgress);
        _commands.Register(new(CanopyCommandIds.NavigationBack, "Navigate back", "Navigation",
            "Return to the previous result location.", "Alt+Left"),
            _ => { _navigationBar.GoBack(); return Task.CompletedTask; });
        _commands.Register(new(CanopyCommandIds.NavigationForward, "Navigate forward", "Navigation",
            "Move to the next result location.", "Alt+Right"),
            _ => { _navigationBar.GoForward(); return Task.CompletedTask; });
        _commands.Register(new("summary.copy", "Copy shareable summary", "Results",
            "Copy a privacy-safe scan summary.", "Ctrl+Shift+C", ["clipboard"]),
            _ => { OnCopySummary(this, new RoutedEventArgs()); return Task.CompletedTask; },
            () => _result is { Nodes.Length: > 0 });
        _commands.Register(new("help.open", "Open help and concepts", "Help",
            "Explain sizes, elevation, privacy, and result states.", "F1", ["documentation"]),
            _ => { ShowHelp(HelpTopic.Size); return Task.CompletedTask; });
        _commands.Register(new("tour.open", "Open welcome tour", "Help",
            "Restart the guided Canopy introduction.", null, ["onboarding"]),
            _ => { ShowTour(); return Task.CompletedTask; });
        _commands.Register(new(CanopyCommandIds.FavoritesToggle, "Toggle favorite location", "Locations",
            "Pin or unpin the first configured scan path.", "Ctrl+D", ["pin", "bookmark"]),
            async token => await ToggleCurrentFavoriteAsync(token),
            () => _locationHistory is not null && ParsePaths(_pathBox.Text).Length > 0);
        _commands.Register(new(CanopyCommandIds.SavedSearchSave, "Save current search", "Search",
            "Open Search so the current filters can be named and saved.", null, ["preset", "filter"]),
            _ => { SelectTab("Search"); return Task.CompletedTask; });
        _commands.Register(new("shortcuts.edit", "Customize keyboard shortcuts", "Application",
            "View shortcut documentation and change command bindings.", null, ["keys", "hotkeys"]),
            _ => { SelectTab("Shortcuts"); return Task.CompletedTask; });
        _commandPalette.SetRegistry(_commands);
        _shortcutSettingsView.SetRegistry(_commands, _shortcutStore);
        _searchView.PresetsChanged += RegisterSavedSearchCommands;
    }

    void OpenCommandPalette()
    {
        _commandPaletteOverlay.Visibility = Visibility.Visible;
        _commandPalette.Open();
    }

    void CloseCommandPalette() => _commandPaletteOverlay.Visibility = Visibility.Collapsed;

    void ShowHelp(HelpTopic topic)
    {
        _contextualHelpView.ShowTopic(topic);
        _contentTabs.SelectedItem = _helpTab;
        _contextualHelpView.Focus();
    }

    void ShowTour()
    {
        _tourOverlay.Visibility = Visibility.Visible;
        _firstRunTour.Restart();
    }

    void OnOpenHelp(object sender, RoutedEventArgs e) => ShowHelp(HelpTopic.Size);
    void OnOpenTour(object sender, RoutedEventArgs e) => ShowTour();
    void OnTourDismissed() => _tourOverlay.Visibility = Visibility.Collapsed;
    void OnTourCompleted()
    {
        try { TourCompletionStore.MarkComplete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Logger.Error("could not save welcome tour completion", ex);
        }
        _tourOverlay.Visibility = Visibility.Collapsed;
        _pathBox.Focus();
    }

    void ShowResultState(ActionableState state)
    {
        _resultState.SetState(state);
        _resultState.Visibility = Visibility.Visible;
    }

    void OnResultStatePrimary()
    {
        if (_resultState.State?.Kind is ActionableStateKind.Partial or ActionableStateKind.Error)
            OnScan(this, new RoutedEventArgs());
        else
            _pathBox.Focus();
    }

    void OnResultStateSecondary()
    {
        HelpTopic topic = _resultState.State?.Kind switch
        {
            ActionableStateKind.PermissionRequired => HelpTopic.Elevation,
            ActionableStateKind.Error or ActionableStateKind.Partial or ActionableStateKind.NoResults => HelpTopic.States,
            _ => HelpTopic.Size,
        };
        ShowHelp(topic);
    }

    void OnCommandPaletteCloseRequested() => CloseCommandPalette();

    void OnCommandPaletteBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _commandPaletteOverlay)) CloseCommandPalette();
    }

    void OnScreenshotPrivacyChanged(object sender, RoutedEventArgs e)
    {
        if (_screenshotPrivacyOverlay is null || _pathPrivacyMask is null) return;
        bool enabled = _screenshotPrivacyToggle.IsChecked == true;
        _screenshotPrivacyOverlay.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _pathPrivacyMask.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hide elevation badge if running elevated.
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        _elevBadge.Visibility  = elevated ? Visibility.Collapsed : Visibility.Visible;
        ShowResultState(new(ActionableStateKind.Empty, "Choose a location to begin",
            "Enter a path above, or choose a recent location, then start a scan.",
            "Focus path", "Learn how scanning works"));
        if (!TourCompletionStore.IsComplete()) ShowTour();
        try
        {
            string historyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SizeMonitor", "locations.json");
            _locationHistory = new LocationHistoryStore(historyPath);
            await _locationHistoryView.SetStoreAsync(_locationHistory);
            RegisterFavoriteCommands();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidDataException)
        {
            Logger.Error("could not load location history", ex);
        }
        try
        {
            _commands.ApplyOverrides(await _shortcutStore.LoadAsync());
            _shortcutSettingsView.SetRegistry(_commands, _shortcutStore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidDataException)
        {
            Logger.Error("could not load shortcut settings", ex);
            _statCurrent.Text = $"Shortcut settings were not loaded: {ex.Message}";
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
        _resultState.Visibility = Visibility.Collapsed;
        _statSize.Text          = "Scanning...";
        _statFiles.Text         = "";
        _statTime.Text          = "";
        _statTimeSep.Visibility = Visibility.Collapsed;
        _statScannerSep.Visibility = Visibility.Collapsed;
        _statScanner.Text       = "";
        _statVolume.Text        = "";
        _statSelection.Text     = "";
        _distributionView.SetResult(null);
        _cleanupRulesView.SetResult(null);
        _diskUsageSummaryView.SetResult(null);
        if (_shellActionsMenu is not null) _shellActionsMenu.ItemPath = null;
        _saveSnapshotMenuItem.IsEnabled = false;
        _exportMenuItem.IsEnabled = false;
        _copySummaryMenuItem.IsEnabled = false;
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
                string[] limitations = failures.Length == 0
                    ? []
                    : [$"{failures.Length:N0} of {outcomes.Count:N0} scan targets could not be read"];
                await OnScanCompleteAsync(result, targetResults, generation, limitations);
                if (_locationHistory is not null)
                {
                    try
                    {
                        foreach (TargetScanResult target in targetResults)
                            await _locationHistory.TouchAsync(target.Path, cancellationToken: CancellationToken.None);
                        _locationHistoryView.RefreshLocations();
                        RegisterFavoriteCommands();
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
                    ShowResultState(new(ActionableStateKind.Partial, "Some locations could not be scanned",
                        "Successful results are available below. Review permissions, exclusions, and network connections before retrying.",
                        "Retry scan", "Why can scans be partial?"));
                    foreach (TargetScanOutcome failure in failures)
                        Logger.Error($"scan target failed: {failure.Path}",
                            failure.Error ?? new IOException("Unknown scan failure."));
                    _statCurrent.Text = $"{failures.Length:N0} of {outcomes.Count:N0} targets failed; successful results are shown.";
                }
                await NotifyScanCompletionAsync(new ScanCompletionSummary
                {
                    Outcome = failures.Length == 0
                        ? ScanCompletionOutcome.Success : ScanCompletionOutcome.PartialSuccess,
                    TargetCount = outcomes.Count,
                    SuccessfulTargets = targetResults.Length,
                    FailedTargets = failures.Length,
                    FileCount = result.FileCount,
                    DirectoryCount = result.DirCount,
                    TotalBytes = result.TotalBytes,
                    Elapsed = scanClock.Elapsed,
                });
            }
        }
        catch (OperationCanceledException)
        {
            _statSize.Text          = "Cancelled";
            _statFiles.Text         = "";
            _statTime.Text          = "";
            _statTimeSep.Visibility = Visibility.Collapsed;
            _statCurrent.Text       = "";
            if (_result is not { Nodes.Length: > 0 })
                ShowResultState(new(ActionableStateKind.Empty, "Scan cancelled",
                    "No new results were applied. Update the path or options and try again.",
                    "Retry scan", "Scanning help"));
        }
        catch (Exception ex)
        {
            Logger.Error($"scan failed: {_pathBox.Text.Trim()}", ex);
            _statSize.Text          = $"Error: {ex.Message}";
            _statFiles.Text         = "";
            _statTime.Text          = "";
            _statTimeSep.Visibility = Visibility.Collapsed;
            _statCurrent.Text       = "";
            if (_result is not { Nodes.Length: > 0 })
                ShowResultState(new(ActionableStateKind.Error, "The scan could not finish",
                    "Verify that the location exists and is online. Permission errors may require elevation.",
                    "Retry scan", "Troubleshooting", ex.Message));
            await NotifyScanCompletionAsync(new ScanCompletionSummary
            {
                Outcome = ScanCompletionOutcome.Failure,
                TargetCount = paths.Length,
                FailedTargets = paths.Length,
                Elapsed = scanClock.Elapsed,
            });
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
            _copySummaryMenuItem.IsEnabled = _result is { Nodes.Length: > 0 };
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

    async Task NotifyScanCompletionAsync(ScanCompletionSummary summary)
    {
        try
        {
            ScanCompletionNotificationResult notification =
                await _notificationService.NotifyAsync(summary);
            if (notification.DeliveryError is not null)
                Logger.Error("scan completion notification delivery failed", notification.DeliveryError);
        }
        catch (Exception ex)
        {
            Logger.Error("scan completion notification failed", ex);
        }
    }

    async Task ToggleCurrentFavoriteAsync(CancellationToken token)
    {
        if (_locationHistory is null) return;
        string? path = ParsePaths(_pathBox.Text).FirstOrDefault();
        if (path is null) return;
        bool pinned = _locationHistory.Locations.Any(item => item.IsFavorite &&
            string.Equals(item.Path, LocationHistoryStore.NormalizeWindowsPath(path), StringComparison.OrdinalIgnoreCase));
        if (pinned) await _locationHistory.UnpinAsync(path, token);
        else await _locationHistory.PinAsync(path, cancellationToken: token);
        _locationHistoryView.RefreshLocations();
        RegisterFavoriteCommands();
        _statCurrent.Text = pinned ? $"Removed {path} from favorites." : $"Added {path} to favorites.";
    }

    void RegisterFavoriteCommands()
    {
        RemoveDynamicCommands("favorite.");
        if (_locationHistory is null) return;
        foreach (ScanLocation location in _locationHistory.Locations.Where(item => item.IsFavorite))
        {
            string id = "favorite." + StableId(location.Path);
            string path = location.Path;
            _commands.Register(new(id, $"Use favorite: {location.DisplayName}", "Locations",
                $"Set the scan path to {location.DisplayName}.", null, ["favorite", path]),
                _ => { _pathBox.Text = path; SelectTab("Explore"); return Task.CompletedTask; });
            _dynamicCommandIds.Add(id);
        }
        _commandPalette.SetRegistry(_commands);
    }

    void RegisterSavedSearchCommands(IReadOnlyList<ScanFilterPreset> presets)
    {
        RemoveDynamicCommands("saved-search.");
        foreach (ScanFilterPreset preset in presets)
        {
            string id = "saved-search." + StableId(preset.Name);
            string name = preset.Name;
            _commands.Register(new(id, $"Search: {name}", "Saved searches",
                $"Apply the saved '{name}' filters.", null, ["preset", "filter"]),
                async token => { SelectTab("Search"); await _searchView.ApplyPresetAsync(name, token); },
                () => _result is not null);
            _dynamicCommandIds.Add(id);
        }
        _commandPalette.SetRegistry(_commands);
    }

    void RemoveDynamicCommands(string prefix)
    {
        foreach (string id in _dynamicCommandIds.Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        { _commands.Unregister(id); _dynamicCommandIds.Remove(id); }
    }

    void SelectTab(string header)
    {
        foreach (object item in _contentTabs.Items)
            if (item is System.Windows.Controls.TabItem tab && string.Equals(tab.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase))
            { _contentTabs.SelectedItem = tab; break; }
    }

    static string StableId(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

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

    async Task OnScanCompleteAsync(ScanResultManaged result, IReadOnlyList<TargetScanResult> targets,
        int generation, IReadOnlyList<string> limitations)
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
        await DisplayResultAsync(result, generation, targets, limitations);
        await UpdateVolumeStatusAsync(targets, generation);
    }

    void OnWindowNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if (_tourOverlay.Visibility == Visibility.Visible) return;
        if (_commandPaletteOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            CloseCommandPalette();
            e.Handled = true;
            return;
        }
        string? shortcut = ShortcutFrom(e);
        if (shortcut is not null && _commands.FindCommandByShortcut(shortcut) is string commandId)
        {
            _ = _commands.ExecuteAsync(commandId);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        bool handled = e.SystemKey switch
        {
            Key.Left => _navigationBar.GoBack(),
            Key.Right => _navigationBar.GoForward(),
            _ => false,
        };
        if (handled) e.Handled = true;
    }

    static string? ShortcutFrom(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        string keyName = key switch
        {
            Key.Return => "Enter",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.F1 and <= Key.F12 => key.ToString(),
            Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.Escape or Key.Delete or Key.Space => key.ToString(),
            _ => string.Empty,
        };
        if (keyName.Length == 0) return null;
        var parts = new List<string>(5);
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0 && key is < Key.F1 or > Key.F12) return null;
        parts.Add(keyName);
        return string.Join('+', parts);
    }

    async Task<bool> DisplayResultAsync(ScanResultManaged result, int generation,
        IReadOnlyList<TargetScanResult>? targets = null,
        IReadOnlyList<string>? limitations = null)
    {
        Task<SizeNodeView[]> treeProjection = SizeTreeView.PrepareAsync(result);
        (ScanResultMetrics Metrics, ScanNavigation? Navigation) derived = await Task.Run(() =>
        {
            ScanResultMetrics metrics = ScanResultMetrics.Calculate(result);
            ScanNavigation? navigation = result.Nodes.Length == 0
                ? null
                : new ScanNavigation(new ScanNavigationIndex(result));
            return (metrics, navigation);
        });
        SizeNodeView[] projectedTree = await treeProjection;
        if (generation != _resultGeneration) return false;

        if (_result is not null && !ReferenceEquals(_result, result))
            _previousResult = _result;
        _result = result;
        _targets = targets ?? [];
        _summaryLimitations = limitations ?? [];
        if (_shellActionsMenu is not null) _shellActionsMenu.ItemPath = null;
        _metrics = derived.Metrics;
        _navigation = derived.Navigation;
        _treeView?.PopulatePrepared(result, projectedTree);
        _searchView.SetResult(result);
        _navigationBar.SetNavigation(_navigation);
        _emptyItemsView.SetResult(result);
        _distributionView.SetResult(result);
        _anomaliesView.SetResult(result);
        _cleanupRulesView.SetResult(result);
        _diskUsageSummaryView.SetResult(result, limitations: _summaryLimitations);
        _comparisonView.SetResults(_previousResult, result);
        _duplicatesView.SetRoots((targets ?? []).Select(target => target.Path));
        bool hasNodes = result.Nodes.Length > 0;
        _saveSnapshotMenuItem.IsEnabled = true;
        _exportMenuItem.IsEnabled = hasNodes;
        _copySummaryMenuItem.IsEnabled = hasNodes;
        if (hasNodes)
            _resultState.Visibility = Visibility.Collapsed;
        else
            ShowResultState(new(ActionableStateKind.NoResults, "This scan contains no results",
                "The location may be empty, or filters and exclusions may have removed every item.",
                "Review scan options", "Understand empty results"));
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
            if (_result is not null)
                _diskUsageSummaryView.SetResult(_result, limitations: _summaryLimitations);
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
            if (_result is not null)
                _diskUsageSummaryView.SetResult(_result, volume.TotalBytes, _summaryLimitations);
            return;
        }

        ulong total = SumSaturating(volumes.Select(volume => volume.TotalBytes));
        ulong free = SumSaturating(volumes.Select(volume => volume.FreeBytes));
        ulong used = total >= free ? total - free : 0;
        _statVolume.Text =
            $"{volumes.Count} volumes · {SizeFormatter.FormatBytes(used)} used of " +
            $"{SizeFormatter.FormatBytes(total)} ({SizeFormatter.FormatBytes(free)} free)";
        if (_result is not null)
            _diskUsageSummaryView.SetResult(_result, total, _summaryLimitations);
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

    void OnCleanupNodeActivated(uint nodeIndex)
    {
        if (_result is null || nodeIndex >= _result.Nodes.Length) return;
        _navigationBar.NavigateTo(nodeIndex);
        ActivateNode(nodeIndex);
        _contentTabs.SelectedIndex = 0;
    }

    void OnSummaryNodeActivated(uint nodeIndex)
    {
        if (_result is null || nodeIndex >= _result.Nodes.Length) return;
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

    void OnCopySummary(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        try
        {
            string summary = ScanSummaryFormatter.Format(_result, options: new ScanSummaryOptions
            {
                Format = ScanSummaryFormat.Markdown,
                IncludeSensitivePaths = false,
            });
            Clipboard.SetText(summary);
            _statCurrent.Text = "Copied a privacy-safe scan summary.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                                      or System.Runtime.InteropServices.ExternalException)
        {
            Logger.Error("copy scan summary failed", ex);
            _statCurrent.Text = $"Copy summary failed: {ex.Message}";
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
