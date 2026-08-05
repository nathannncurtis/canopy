namespace SizeMonitor.Interop;

public sealed record TargetScanProgress(
    string Path,
    ScanProgress Current,
    int CompletedTargets,
    int TotalTargets);

public sealed record TargetScanResult(
    string Path,
    ScannerKind Scanner,
    ScanResultManaged Result);

/// <summary>Coordinates independent scans without coupling them to a UI.</summary>
public sealed class MultiScanSession : IAsyncDisposable
{
    readonly object _gate = new();
    readonly List<ScanSession> _active = [];
    readonly int _maxConcurrency;
    CancellationTokenSource? _runCancellation;
    bool _paused;
    bool _disposed;

    public MultiScanSession(int maxConcurrency = 2)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _maxConcurrency = maxConcurrency;
    }

    public async Task<IReadOnlyList<TargetScanResult>> ScanAsync(
        IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string[] targets = paths.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) throw new ArgumentException("At least one path is required.", nameof(paths));

        CancellationTokenSource runCancellation;
        lock (_gate)
        {
            if (_runCancellation is not null)
                throw new InvalidOperationException("This coordinator already has an active scan.");
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCancellation = _runCancellation;
        }

        using var concurrency = new SemaphoreSlim(_maxConcurrency);
        int completed = 0;
        var tasks = targets.Select(async path =>
        {
            await concurrency.WaitAsync(runCancellation.Token).ConfigureAwait(false);
            try
            {
                var itemProgress = progress is null ? null : new Progress<ScanProgress>(value =>
                    progress.Report(new(path, value, Volatile.Read(ref completed), targets.Length)));
                using var session = ScanSession.Start(path, itemProgress);
                lock (_gate)
                {
                    _active.Add(session);
                    if (_paused) session.Pause();
                }
                try
                {
                    var result = await session.WaitAsync(runCancellation.Token).ConfigureAwait(false);
                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new(path,
                        new(result.DirCount, result.FileCount, result.TotalBytes), done, targets.Length));
                    return new TargetScanResult(path, session.Scanner, result);
                }
                finally
                {
                    lock (_gate) _active.Remove(session);
                }
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();

        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _runCancellation?.Dispose();
                _runCancellation = null;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            foreach (var scan in _active) scan.Pause();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            foreach (var scan in _active) scan.Resume();
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _runCancellation?.Cancel();
            foreach (var scan in _active) scan.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Cancel();
        return ValueTask.CompletedTask;
    }
}
