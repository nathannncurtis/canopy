using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class CleanupRulesView : UserControl
{
    CancellationTokenSource? _previewCancellation;
    ScanResultManaged? _result;
    CleanupPreview? _preview;
    long _generation;

    public event Action<uint>? NodeActivated;

    public CleanupRulesView()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPreview();
    }

    public void SetResult(ScanResultManaged? result)
    {
        CancelPreview();
        _result = result;
        _preview = null;
        _items.ItemsSource = null;
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
        _items.ItemsSource = null;
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
            ApplyFilters();
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
