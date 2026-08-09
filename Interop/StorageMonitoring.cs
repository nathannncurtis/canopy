using System.Text.Json;
using System.Threading.Channels;

namespace SizeMonitor.Interop;

public sealed record FreeSpaceThreshold(
    ulong? MinimumFreeBytes = null,
    double? MinimumFreePercent = null,
    ulong RecoveryHysteresisBytes = 512ul * 1024 * 1024,
    double RecoveryHysteresisPercent = 1,
    TimeSpan? Cooldown = null)
{
    public TimeSpan EffectiveCooldown => Cooldown ?? TimeSpan.FromHours(1);

    public void Validate()
    {
        if (MinimumFreeBytes is null && MinimumFreePercent is null)
            throw new ArgumentException("At least one free-space threshold is required.");
        if (MinimumFreePercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MinimumFreePercent));
        if (RecoveryHysteresisPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(RecoveryHysteresisPercent));
        if (EffectiveCooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Cooldown));
    }
}

public sealed record FreeSpaceAlert(
    string RootPath,
    DateTimeOffset ObservedUtc,
    ulong FreeBytes,
    double FreePercent,
    bool IsRecovery,
    string Message);

public sealed class FreeSpaceAlertEvaluator(FreeSpaceThreshold threshold)
{
    readonly Dictionary<string, AlertState> _states = new(StringComparer.OrdinalIgnoreCase);

    public FreeSpaceThreshold Threshold { get; } = Validate(threshold);

    public FreeSpaceAlert? Evaluate(VolumeStorageInfo volume, DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(volume);
        observedUtc = observedUtc.ToUniversalTime();
        double percent = volume.TotalBytes == 0 ? 0 : 100d * volume.FreeBytes / volume.TotalBytes;
        bool critical = IsBelow(volume.FreeBytes, percent);
        _states.TryGetValue(volume.RootPath, out AlertState state);

        if (critical)
        {
            bool cooldownElapsed = state.LastAlertUtc is null ||
                observedUtc - state.LastAlertUtc.Value >= Threshold.EffectiveCooldown;
            if (!state.Latched && cooldownElapsed)
            {
                _states[volume.RootPath] = new(true, observedUtc);
                return new(volume.RootPath, observedUtc, volume.FreeBytes, percent, false,
                    $"{volume.RootPath} is low on space: {percent:F1}% free.");
            }
            return null;
        }

        if (state.Latched && IsRecovered(volume.FreeBytes, percent))
        {
            _states[volume.RootPath] = new(false, state.LastAlertUtc);
            return new(volume.RootPath, observedUtc, volume.FreeBytes, percent, true,
                $"{volume.RootPath} free space recovered to {percent:F1}%.");
        }
        return null;
    }

    bool IsBelow(ulong bytes, double percent) =>
        (Threshold.MinimumFreeBytes is { } minimumBytes && bytes <= minimumBytes) ||
        (Threshold.MinimumFreePercent is { } minimumPercent && percent <= minimumPercent);

    bool IsRecovered(ulong bytes, double percent) =>
        (Threshold.MinimumFreeBytes is not { } minimumBytes ||
            bytes > SaturatingAdd(minimumBytes, Threshold.RecoveryHysteresisBytes)) &&
        (Threshold.MinimumFreePercent is not { } minimumPercent ||
            percent > minimumPercent + Threshold.RecoveryHysteresisPercent);

    static ulong SaturatingAdd(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    static FreeSpaceThreshold Validate(FreeSpaceThreshold value) { value.Validate(); return value; }
    readonly record struct AlertState(bool Latched, DateTimeOffset? LastAlertUtc);
}

public sealed record DirectoryGrowthPolicy(
    ulong MinimumBaselineBytes,
    ulong MinimumAbsoluteGrowthBytes,
    double MinimumGrowthBytesPerHour)
{
    public void Validate()
    {
        if (MinimumGrowthBytesPerHour < 0 || double.IsNaN(MinimumGrowthBytesPerHour) ||
            double.IsInfinity(MinimumGrowthBytesPerHour))
            throw new ArgumentOutOfRangeException(nameof(MinimumGrowthBytesPerHour));
    }
}

public sealed record DirectoryGrowthAlert(
    string Path,
    DateTimeOffset PreviousUtc,
    DateTimeOffset CurrentUtc,
    ulong PreviousBytes,
    ulong CurrentBytes,
    ulong GrowthBytes,
    double GrowthBytesPerHour);

public static class DirectoryGrowthAlertEvaluator
{
    public static DirectoryGrowthAlert? Evaluate(string path, ScanTrendSample previous,
        ScanTrendSample current, DirectoryGrowthPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        TimeSpan elapsed = current.CapturedUtc - previous.CapturedUtc;
        if (elapsed <= TimeSpan.Zero || previous.TotalBytes < policy.MinimumBaselineBytes ||
            current.TotalBytes <= previous.TotalBytes) return null;
        ulong growth = current.TotalBytes - previous.TotalBytes;
        double hourly = growth / elapsed.TotalHours;
        return growth >= policy.MinimumAbsoluteGrowthBytes && hourly >= policy.MinimumGrowthBytesPerHour
            ? new(path, previous.CapturedUtc, current.CapturedUtc, previous.TotalBytes,
                current.TotalBytes, growth, hourly)
            : null;
    }
}

public sealed record DriveHistoryPoint(string RootPath, DateTimeOffset CapturedUtc,
    ulong TotalBytes, ulong FreeBytes, bool ThresholdEvent = false)
{
    public double FreePercent => TotalBytes == 0 ? 0 : 100d * FreeBytes / TotalBytes;
}

public sealed record DriveHistoryRetention(int MaximumPointsPerDrive = 2_880,
    TimeSpan? MaximumAge = null)
{
    public void Validate()
    {
        if (MaximumPointsPerDrive is < 2 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(MaximumPointsPerDrive));
        if (MaximumAge is { } age && age <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
    }
}

public sealed class DriveHistoryStore
{
    const int SchemaVersion = 1;
    const int MaximumFileBytes = 16 * 1024 * 1024;
    readonly string _path;
    readonly DriveHistoryRetention _retention;
    readonly SemaphoreSlim _gate = new(1, 1);
    List<DriveHistoryPoint> _points = [];

    public DriveHistoryStore(string path, DriveHistoryRetention? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _retention = retention ?? new();
        _retention.Validate();
    }

    public IReadOnlyList<DriveHistoryPoint> Points
    {
        get { _gate.Wait(); try { return Ordered(_points).ToArray(); } finally { _gate.Release(); } }
    }

    public IReadOnlyList<DriveHistoryPoint> GetDrive(string rootPath, int maximumPoints = 120)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (maximumPoints < 1) throw new ArgumentOutOfRangeException(nameof(maximumPoints));
        string normalized = NormalizeRoot(rootPath);
        return Points.Where(point => StringComparer.OrdinalIgnoreCase.Equals(point.RootPath, normalized))
            .OrderByDescending(point => point.CapturedUtc).Take(maximumPoints).OrderBy(point => point.CapturedUtc).ToArray();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) { _points = []; return; }
            if (new FileInfo(_path).Length > MaximumFileBytes)
                throw new InvalidDataException("Drive history is too large.");
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            HistoryDocument? document;
            try { document = await JsonSerializer.DeserializeAsync<HistoryDocument>(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false); }
            catch (JsonException ex) { throw new InvalidDataException("Drive history JSON is invalid.", ex); }
            if (document is null || document.Version != SchemaVersion || document.Points is null)
                throw new InvalidDataException("Drive history schema is unsupported.");
            if (document.Points.Count > 100_000) throw new InvalidDataException("Drive history has too many points.");
            foreach (DriveHistoryPoint point in document.Points) ValidatePoint(point);
            _points = Prune(document.Points, DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(DriveHistoryPoint point, CancellationToken cancellationToken = default)
    {
        ValidatePoint(point);
        point = point with { RootPath = NormalizeRoot(point.RootPath), CapturedUtc = point.CapturedUtc.ToUniversalTime() };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DriveHistoryPoint> updated = Prune([.. _points, point], point.CapturedUtc);
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _points = updated;
        }
        finally { _gate.Release(); }
    }

    List<DriveHistoryPoint> Prune(IEnumerable<DriveHistoryPoint> source, DateTimeOffset now) =>
        source.Where(point => _retention.MaximumAge is not { } age || now - point.CapturedUtc <= age)
            .GroupBy(point => point.RootPath, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderByDescending(point => point.CapturedUtc)
                .Take(_retention.MaximumPointsPerDrive))
            .OrderBy(point => point.CapturedUtc).ThenBy(point => point.RootPath, StringComparer.OrdinalIgnoreCase).ToList();

    async Task SaveAsync(List<DriveHistoryPoint> points, CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new HistoryDocument(SchemaVersion, points),
                    cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, _path, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static string NormalizeRoot(string value) => Path.GetPathRoot(Path.GetFullPath(value))
        ?? throw new InvalidDataException("Drive history root is invalid.");
    static void ValidatePoint(DriveHistoryPoint point)
    {
        if (string.IsNullOrWhiteSpace(point.RootPath) || point.CapturedUtc == default ||
            point.FreeBytes > point.TotalBytes) throw new InvalidDataException("Drive history point is invalid.");
    }
    static IEnumerable<DriveHistoryPoint> Ordered(IEnumerable<DriveHistoryPoint> points) =>
        points.OrderBy(point => point.RootPath, StringComparer.OrdinalIgnoreCase).ThenBy(point => point.CapturedUtc);
    sealed record HistoryDocument(int Version, List<DriveHistoryPoint> Points);
}

public sealed record FolderMonitorOptions(TimeSpan? PollInterval = null,
    TimeSpan? CoalescingWindow = null)
{
    public TimeSpan EffectivePollInterval => PollInterval ?? TimeSpan.FromMinutes(5);
    public TimeSpan EffectiveCoalescingWindow => CoalescingWindow ?? TimeSpan.FromMilliseconds(500);
    public void Validate()
    {
        if (EffectivePollInterval < TimeSpan.FromMilliseconds(100) || EffectivePollInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (EffectiveCoalescingWindow < TimeSpan.Zero || EffectiveCoalescingWindow > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(CoalescingWindow));
    }
}

public enum FolderMonitorReason { Initial, FileSystemChange, WatcherOverflow, PeriodicReconciliation }
public sealed record FolderMonitorSample(string Path, DateTimeOffset CapturedUtc, ulong TotalBytes,
    FolderMonitorReason Reason, DirectoryGrowthAlert? GrowthAlert, string? Error = null);

public sealed class ContinuousFolderMonitor : IAsyncDisposable
{
    readonly string _path;
    readonly Func<string, CancellationToken, Task<ulong>> _measure;
    readonly FolderMonitorOptions _options;
    readonly DirectoryGrowthPolicy? _growthPolicy;
    readonly Channel<FolderMonitorReason> _signals = Channel.CreateBounded<FolderMonitorReason>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    readonly FileSystemWatcher _watcher;
    CancellationTokenSource? _lifetime;
    Task? _run;
    int _overflowed;

    public ContinuousFolderMonitor(string path, Func<string, CancellationToken, Task<ulong>> measure,
        FolderMonitorOptions? options = null, DirectoryGrowthPolicy? growthPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(measure);
        _path = Path.GetFullPath(path);
        _measure = measure;
        _options = options ?? new();
        _options.Validate();
        growthPolicy?.Validate();
        _growthPolicy = growthPolicy;
        _watcher = new FileSystemWatcher(_path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 16 * 1024,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Error += OnError;
    }

    public Task StartAsync(IProgress<FolderMonitorSample> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_run is not null) throw new InvalidOperationException("Folder monitoring is already running.");
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _watcher.EnableRaisingEvents = true;
        _run = RunAsync(progress, _lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_run is null) return;
        _watcher.EnableRaisingEvents = false;
        _lifetime!.Cancel();
        try { await _run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _lifetime = null;
        _run = null;
    }

    async Task RunAsync(IProgress<FolderMonitorSample> progress, CancellationToken token)
    {
        ScanTrendSample? previous = null;
        await ReconcileAsync(FolderMonitorReason.Initial).ConfigureAwait(false);
        while (true)
        {
            FolderMonitorReason reason;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(_options.EffectivePollInterval);
            try
            {
                if (!await _signals.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) break;
                reason = FolderMonitorReason.FileSystemChange;
                while (_signals.Reader.TryRead(out FolderMonitorReason pending))
                    if (pending == FolderMonitorReason.WatcherOverflow) reason = pending;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                reason = FolderMonitorReason.PeriodicReconciliation;
            }
            if (reason != FolderMonitorReason.PeriodicReconciliation)
            {
                if (_options.EffectiveCoalescingWindow > TimeSpan.Zero)
                    await Task.Delay(_options.EffectiveCoalescingWindow, token).ConfigureAwait(false);
                while (_signals.Reader.TryRead(out FolderMonitorReason pending))
                    if (pending == FolderMonitorReason.WatcherOverflow) reason = pending;
            }
            if (Interlocked.Exchange(ref _overflowed, 0) != 0)
                reason = FolderMonitorReason.WatcherOverflow;
            await ReconcileAsync(reason).ConfigureAwait(false);
        }

        async Task ReconcileAsync(FolderMonitorReason reason)
        {
            ulong bytes;
            try { bytes = await _measure(_path, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ScanException)
            {
                try { progress.Report(new(_path, DateTimeOffset.UtcNow, previous?.TotalBytes ?? 0,
                    reason, null, ex.Message)); } catch { }
                return;
            }
            var current = new ScanTrendSample(DateTimeOffset.UtcNow, bytes);
            DirectoryGrowthAlert? alert = previous is not null && _growthPolicy is not null
                ? DirectoryGrowthAlertEvaluator.Evaluate(_path, previous, current, _growthPolicy)
                : null;
            previous = current;
            try { progress.Report(new(_path, current.CapturedUtc, bytes, reason, alert)); } catch { }
        }
    }

    void OnChanged(object sender, FileSystemEventArgs e) => _signals.Writer.TryWrite(FolderMonitorReason.FileSystemChange);
    void OnError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref _overflowed, 1);
        _signals.Writer.TryWrite(FolderMonitorReason.WatcherOverflow);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _watcher.Dispose();
    }
}

public sealed record DriveMonitorUpdate(VolumeStorageInfo Volume, DriveHistoryPoint History,
    FreeSpaceAlert? Alert);

public sealed class DriveMonitorService(
    IEnumerable<string> roots,
    DriveHistoryStore history,
    FreeSpaceThreshold threshold,
    TimeSpan? pollInterval = null,
    Func<string, VolumeStorageInfo>? readVolume = null)
{
    readonly string[] _roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
    readonly FreeSpaceAlertEvaluator _alerts = new(threshold);
    readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMinutes(1);
    readonly Func<string, VolumeStorageInfo> _readVolume = readVolume ?? VolumeStorageInfo.Read;

    public async Task RunAsync(IProgress<DriveMonitorUpdate> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_roots.Length == 0) throw new ArgumentException("At least one drive root is required.");
        if (_pollInterval < TimeSpan.FromMilliseconds(100) || _pollInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(_pollInterval));
        do
        {
            foreach (string root in _roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VolumeStorageInfo volume;
                try { volume = _readVolume(root); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { continue; }
                DateTimeOffset now = DateTimeOffset.UtcNow;
                FreeSpaceAlert? alert = _alerts.Evaluate(volume, now);
                var point = new DriveHistoryPoint(volume.RootPath, now, volume.TotalBytes,
                    volume.FreeBytes, alert is not null);
                await history.AppendAsync(point, cancellationToken).ConfigureAwait(false);
                try { progress.Report(new(volume, point, alert)); } catch { }
            }
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        } while (true);
    }
}
