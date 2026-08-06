using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public enum DuplicateRetainPolicy { Newest, Oldest, ShortestPath }

public sealed class DuplicateFileRow : INotifyPropertyChanged
{
    bool _isRecommendedRetain;
    public required int GroupNumber { get; init; }
    public required long Size { get; init; }
    public required ulong ReclaimableBytes { get; init; }
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public string SizeText => SizeFormatter.FormatBytes((ulong)Size);
    public string ReclaimableText => SizeFormatter.FormatBytes(ReclaimableBytes);
    public bool IsRecommendedRetain
    {
        get => _isRecommendedRetain;
        internal set { if (_isRecommendedRetain == value) return; _isRecommendedRetain = value; OnChanged(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public partial class DuplicateFilesView : UserControl
{
    CancellationTokenSource? _cancellation;
    string[] _roots = [];
    IReadOnlyList<DuplicateFileGroup> _groups = [];
    DuplicateFileRow[] _rows = [];
    long _generation;

    public DuplicateFilesView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Cancel();
        UpdateReadyStatus();
    }

    public event Action<string>? PathActivated;

    public IReadOnlyList<string> Roots => _roots;
    public bool IsRunning => _cancellation is not null;
    public DuplicateRetainPolicy RetainPolicy => _policy.SelectedIndex switch
    {
        1 => DuplicateRetainPolicy.Oldest,
        2 => DuplicateRetainPolicy.ShortestPath,
        _ => DuplicateRetainPolicy.Newest,
    };

    public void SetRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Cancel();
        _roots = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _groups = [];
        _rows = [];
        _items.ItemsSource = null;
        UpdateReadyStatus();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_roots.Length == 0) throw new InvalidOperationException("Set at least one duplicate search root first.");
        Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation = cancellation;
        long generation = Interlocked.Increment(ref _generation);
        SetRunning(true);
        _status.Text = _strict.IsChecked == true
            ? "Finding exact duplicates (strict inaccessible-file handling)..."
            : "Finding exact duplicates; inaccessible or changing files will be skipped...";
        try
        {
            var options = new DuplicateFileOptions { IgnoreInaccessible = _strict.IsChecked != true };
            IReadOnlyList<DuplicateFileGroup> groups = await DuplicateFileFinder.FindAsync(
                _roots, options, cancellation.Token);
            if (generation != Volatile.Read(ref _generation) || cancellation.IsCancellationRequested) return;
            _groups = groups;
            RebuildRows();
            ulong reclaimable = groups.Aggregate(0ul, (total, group) =>
                AddSaturating(total, MultiplySaturating((ulong)group.Size, (ulong)(group.Paths.Count - 1))));
            _status.Text = $"{groups.Count:N0} duplicate groups · {_rows.Length:N0} files · " +
                           $"{SizeFormatter.FormatBytes(reclaimable)} reclaimable (no files selected for deletion).";
            RaiseStatusChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _generation)) _status.Text = "Duplicate search cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                Logger.Error("duplicate file search failed", ex);
                _status.Text = _strict.IsChecked == true
                    ? $"Strict duplicate search stopped: {ex.Message}"
                    : $"Duplicate search failed: {ex.Message}";
                RaiseStatusChanged();
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                SetRunning(false);
            }
            cancellation.Dispose();
        }
    }

    public void Cancel()
    {
        Interlocked.Increment(ref _generation);
        CancellationTokenSource? cancellation = _cancellation;
        _cancellation = null;
        cancellation?.Cancel();
        SetRunning(false);
    }

    async void OnStart(object sender, RoutedEventArgs e) => await StartAsync();
    void OnCancel(object sender, RoutedEventArgs e) => Cancel();
    void OnPolicyChanged(object sender, SelectionChangedEventArgs e) { if (_items is not null) RebuildRows(); }

    void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_items.SelectedItem is DuplicateFileRow row) PathActivated?.Invoke(row.Path);
    }

    void RebuildRows()
    {
        var rows = new List<DuplicateFileRow>();
        for (int groupIndex = 0; groupIndex < _groups.Count; groupIndex++)
        {
            DuplicateFileGroup group = _groups[groupIndex];
            string retain = Recommend(group.Paths);
            ulong reclaimable = MultiplySaturating((ulong)group.Size, (ulong)(group.Paths.Count - 1));
            rows.AddRange(group.Paths.Select(path => new DuplicateFileRow
            {
                GroupNumber = groupIndex + 1,
                Size = group.Size,
                ReclaimableBytes = reclaimable,
                Path = path,
                Sha256 = group.Sha256,
                IsRecommendedRetain = path.Equals(retain, StringComparison.OrdinalIgnoreCase),
            }));
        }
        _rows = rows.ToArray();
        _items.ItemsSource = _rows;
    }

    string Recommend(IReadOnlyList<string> paths) => RetainPolicy switch
    {
        DuplicateRetainPolicy.ShortestPath => paths.OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First(),
        DuplicateRetainPolicy.Oldest => paths.OrderBy(SafeLastWrite).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First(),
        _ => paths.OrderByDescending(SafeLastWrite).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First(),
    };

    static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    void SetRunning(bool running)
    {
        _start.IsEnabled = !running && _roots.Length > 0;
        _cancel.IsEnabled = running;
        _strict.IsEnabled = !running;
        _policy.IsEnabled = !running;
        _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateReadyStatus()
    {
        SetRunning(false);
        _status.Text = _roots.Length == 0
            ? "Choose one or more roots to find exact duplicate files."
            : $"Ready to search {_roots.Length:N0} root(s).";
    }

    void RaiseStatusChanged()
    {
        AutomationPeer? peer = UIElementAutomationPeer.FromElement(_status)
            ?? UIElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    static ulong AddSaturating(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    static ulong MultiplySaturating(ulong left, ulong right) =>
        left != 0 && right > ulong.MaxValue / left ? ulong.MaxValue : left * right;
}
