using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class CleanupRulesView : UserControl
{
    CancellationTokenSource? _previewCancellation;
    ScanResultManaged? _result;
    CleanupPreview? _preview;
    CleanupFindingNavigator? _navigator;
    long _generation;
    readonly CleanupQueueStore _queueStore = new(AppDataPaths.CleanupQueue);
    CleanupQueueDocument _queue = new();

    public event Action<uint>? NodeActivated;

    public CleanupRulesView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPreview();
        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _queue = await _queueStore.LoadAsync();
            _confirmationThreshold.Text = _queue.TypedConfirmationThresholdBytes.ToString();
            UpdateQueueStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _status.Text = $"Cleanup queue could not be loaded: {ex.Message}"; }
    }

    async void OnQueueSelected(object sender, RoutedEventArgs e)
    {
        if (_preview is null || _items.SelectedItem is not CleanupPreviewRow row)
        { _status.Text = "Select a cleanup recommendation before marking it for later."; return; }
        CleanupPreviewItem? finding = _preview.Items.FirstOrDefault(item => item.NodeIndex == row.NodeIndex);
        if (finding is null) return;
        try
        {
            _queue = await _queueStore.AddOrUpdateAsync(new CleanupQueueEntry
            {
                Path = finding.Path, EstimatedBytes = finding.ReclaimableBytes, Risk = finding.Risk,
                Note = _queueNote.Text, Tags = _queueTags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            });
            UpdateQueueStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        { _status.Text = $"Item was not queued: {ex.Message}"; }
    }

    async void OnSaveThreshold(object sender, RoutedEventArgs e)
    {
        if (!ulong.TryParse(_confirmationThreshold.Text, out ulong threshold))
        { _status.Text = "Confirmation threshold must be a non-negative byte count."; return; }
        try { _queue = await _queueStore.SetThresholdAsync(threshold); UpdateQueueStatus(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _status.Text = $"Threshold was not saved: {ex.Message}"; }
    }

    async void OnRemoveQueued(object sender, RoutedEventArgs e)
    {
        if (_items.SelectedItem is not CleanupPreviewRow row)
        { _status.Text = "Select a cleanup recommendation before removing its mark."; return; }
        try { _queue = await _queueStore.RemoveAsync(row.Path); UpdateQueueStatus(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _status.Text = $"Queue mark was not removed: {ex.Message}"; }
    }

    void UpdateQueueStatus()
    {
        CleanupSafetyAssessment plan = CleanupQueueStore.AssessPlan(_queue);
        string confirmation = plan.RequiresTypedConfirmation
            ? $" Any future destructive workflow must require the exact phrase: {plan.RequiredConfirmation}."
            : string.Empty;
        _status.Text = $"Cleanup queue: {_queue.Items.Count:N0} marked item(s). Planning only; nothing has been changed.{confirmation}";
    }

    public void SetResult(ScanResultManaged? result)
    {
        CancelPreview();
        _result = result;
        _preview = null;
        _navigator = null;
        _items.ItemsSource = null;
        UpdateNavigatorButtons();
        _status.Text = result is null
            ? "Load or paste rules, then run a scan to preview."
            : $"Ready to preview rules against {result.Nodes.Length:N0} scan items.";
    }

    async void OnLoadJson(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load cleanup rules",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > CleanupRuleEngine.MaximumRuleFileBytes)
                throw new InvalidDataException("Cleanup rule JSON exceeds the 1 MiB limit.");
            _ruleJson.Text = await File.ReadAllTextAsync(
                dialog.FileName, Encoding.UTF8, CancellationToken.None);
            _status.Text = $"Loaded {Path.GetFileName(dialog.FileName)}. Select Preview only to evaluate it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = $"Unable to load cleanup rules: {ex.Message}";
        }
    }

    void OnPreview(object sender, RoutedEventArgs e)
    {
        ScanResultManaged? result = _result;
        if (result is null)
        {
            _status.Text = "Run a scan before previewing cleanup rules.";
            return;
        }

        CleanupRuleSet rules;
        try { rules = CleanupRuleEngine.Parse(_ruleJson.Text); }
        catch (InvalidDataException ex)
        {
            _status.Text = ex.Message;
            return;
        }
        StartPreview(result, rules);
    }

    void StartPreview(ScanResultManaged result, CleanupRuleSet rules)
    {
        CancelPreview();
        _preview = null;
        _navigator = null;
        _items.ItemsSource = null;
        UpdateNavigatorButtons();
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _previewCancellation = cancellation;
        long generation = Volatile.Read(ref _generation);
        _status.Text = $"Previewing {rules.Rules.Count:N0} rules against {result.Nodes.Length:N0} items…";
        _ = PreviewAsync(result, rules, cancellation, token, generation);
    }

    async Task PreviewAsync(
        ScanResultManaged result,
        CleanupRuleSet rules,
        CancellationTokenSource cancellation,
        CancellationToken token,
        long generation)
    {
        try
        {
            CleanupPreview preview = await Task.Run(
                () => CleanupRuleEngine.Preview(result, rules, token), token);
            if (generation != Volatile.Read(ref _generation) || token.IsCancellationRequested) return;
            _preview = preview;
            _navigator = new CleanupFindingNavigator(preview);
            ApplyFilters();
            UpdateNavigatorButtons();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
                                      RegexMatchTimeoutException or OverflowException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _items.ItemsSource = null;
                _status.Text = $"Unable to preview cleanup rules: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
                _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_items is not null && _preview is not null) ApplyFilters();
    }

    void ApplyFilters()
    {
        CleanupPreview? preview = _preview;
        if (preview is null) return;
        CleanupRisk? risk = _riskFilter.SelectedIndex switch
        {
            1 => CleanupRisk.Low,
            2 => CleanupRisk.Medium,
            3 => CleanupRisk.High,
            _ => null,
        };
        string rule = _ruleFilter.Text.Trim();
        string reason = _reasonFilter.Text.Trim();
        CleanupPreviewRow[] visible = preview.Items
            .Where(item => risk is null || item.Risk == risk)
            .Where(item => rule.Length == 0 || item.RuleIds.Any(value =>
                value.Contains(rule, StringComparison.OrdinalIgnoreCase)))
            .Where(item => reason.Length == 0 || item.Reasons.Any(value =>
                value.Contains(reason, StringComparison.OrdinalIgnoreCase)))
            .Select(CleanupPreviewRow.From)
            .ToArray();
        _items.ItemsSource = visible;
        _status.Text = $"Showing {visible.Length:N0} of {preview.Items.Count:N0} matches · " +
            $"{preview.ReclaimableBytes:N0} estimated bytes · risk: " +
            $"{preview.LowRiskCount:N0} low, {preview.MediumRiskCount:N0} medium, " +
            $"{preview.HighRiskCount:N0} high. Preview only; nothing was changed.";
    }

    void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(_items, source) is DataGridRow &&
            _items.SelectedItem is CleanupPreviewRow item)
            NodeActivated?.Invoke(item.NodeIndex);
    }

    void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_preview is null || _items.SelectedItem is not CleanupPreviewRow row)
        {
            _explanation.Text = "Select a recommendation to see why it matched and what to review.";
            return;
        }
        CleanupPreviewItem? item = _preview.Items.FirstOrDefault(value => value.NodeIndex == row.NodeIndex);
        if (item is null) return;
        CleanupRecommendationExplanation explanation = CleanupRecommendationExplainer.Explain(item);
        _explanation.Text = explanation.Summary + " Why: " + string.Join(" ", explanation.Reasons) +
            " Review: " + string.Join(" ", explanation.Limitations);
    }

    void OnNextLargestFinding(object sender, RoutedEventArgs e) =>
        RevealFinding(_navigator?.RevealNextLargest());

    void OnPreviousFinding(object sender, RoutedEventArgs e) =>
        RevealFinding(_navigator?.MovePrevious());

    void RevealFinding(CleanupPreviewItem? finding)
    {
        if (finding is null) { UpdateNavigatorButtons(); return; }
        _riskFilter.SelectedIndex = 0;
        _ruleFilter.Text = string.Empty;
        _reasonFilter.Text = string.Empty;
        ApplyFilters();
        CleanupPreviewRow? row = (_items.ItemsSource as IEnumerable<CleanupPreviewRow>)?
            .FirstOrDefault(value => value.NodeIndex == finding.NodeIndex);
        if (row is not null)
        {
            _items.SelectedItem = row;
            _items.ScrollIntoView(row);
        }
        NodeActivated?.Invoke(finding.NodeIndex);
        UpdateNavigatorButtons();
    }

    void UpdateNavigatorButtons()
    {
        if (_nextLargestFinding is null || _previousFinding is null) return;
        _nextLargestFinding.IsEnabled = _navigator?.CanRevealNext == true;
        _previousFinding.IsEnabled = _navigator?.CanMovePrevious == true;
    }

    void CancelPreview()
    {
        Interlocked.Increment(ref _generation);
        CancellationTokenSource? cancellation = _previewCancellation;
        _previewCancellation = null;
        cancellation?.Cancel();
    }

    sealed record CleanupPreviewRow(
        uint NodeIndex,
        string Path,
        ulong ReclaimableBytes,
        CleanupRisk Risk,
        string RuleText,
        string ReasonText)
    {
        public static CleanupPreviewRow From(CleanupPreviewItem item) => new(
            item.NodeIndex, item.Path, item.ReclaimableBytes, item.Risk,
            string.Join(", ", item.RuleIds), string.Join("; ", item.Reasons));
    }
}
