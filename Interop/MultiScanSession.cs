namespace SizeMonitor.Interop;

public sealed record TargetScanProgress(string Path, ScanProgress Current,
    int CompletedTargets, int TotalTargets);

public sealed record TargetScanResult(string Path, ScannerKind Scanner, ScanResultManaged Result);

public sealed record TargetScanOutcome(string Path, ScannerKind Scanner,
    ScanResultManaged? Result, Exception? Error)
{
    public bool Succeeded => Result is not null && Error is null;
}

/// <summary>Coordinates independent scans without coupling them to a UI.</summary>
public sealed class MultiScanSession : IAsyncDisposable
{
    readonly object _gate = new();
    readonly List<ScanSession> _active = [];
    readonly int _maxConcurrency;
    CancellationTokenSource? _runCancellation;
    Task? _activeRun;
    bool _paused;
    bool _disposed;

    public MultiScanSession(int maxConcurrency = 2)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _maxConcurrency = maxConcurrency;
    }

    // Compatibility surface: returns every success while failures remain available
    // through ScanOutcomesAsync for callers that need per-target diagnostics.
    public async Task<IReadOnlyList<TargetScanResult>> ScanAsync(IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress = null, CancellationToken cancellationToken = default,
        ScanOptions? options = null)
    {
        IReadOnlyList<TargetScanOutcome> outcomes =
            await ScanOutcomesAsync(paths, progress, cancellationToken, options).ConfigureAwait(false);
        return outcomes.Where(x => x.Succeeded)
            .Select(x => new TargetScanResult(x.Path, x.Scanner, x.Result!)).ToArray();
    }

    public Task<IReadOnlyList<TargetScanOutcome>> ScanOutcomesAsync(IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress = null, CancellationToken cancellationToken = default,
        ScanOptions? options = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runCancellation is not null)
                return Task.FromException<IReadOnlyList<TargetScanOutcome>>(
                    new InvalidOperationException("This coordinator already has an active scan."));
            Task<IReadOnlyList<TargetScanOutcome>> run = RunAsync(paths, progress, cancellationToken, options);
            _activeRun = run;
            return run;
        }
    }

    async Task<IReadOnlyList<TargetScanOutcome>> RunAsync(IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress, CancellationToken cancellationToken, ScanOptions? options)
    {
        ScanOptions? scanOptions = options is null ? null : options with
        {
            ExcludedPatterns = options.ExcludedPatterns.ToArray(),
            ExcludedExtensions = options.ExcludedExtensions.ToArray(),
        };
        scanOptions?.Validate();
        string[] targets = paths.Select(NormalizeTarget).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) throw new ArgumentException("At least one path is required.", nameof(paths));

        CancellationTokenSource runCancellation;
        lock (_gate)
        {
            if (_runCancellation is not null) throw new InvalidOperationException("This coordinator already has an active scan.");
            _paused = false;
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCancellation = _runCancellation;
        }

        using var concurrency = new SemaphoreSlim(_maxConcurrency);
        int completed = 0;
        Task<TargetScanOutcome>[] tasks = targets.Select(async path =>
        {
            await concurrency.WaitAsync(runCancellation.Token).ConfigureAwait(false);
            ScanSession? session = null;
            try
            {
                var itemProgress = progress is null ? null : new CallbackProgress<ScanProgress>(value =>
                    progress.Report(new(path, value, Volatile.Read(ref completed), targets.Length)));
                session = await Task.Run(() => ScanSession.Start(path, itemProgress, scanOptions),
                    runCancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _active.Add(session);
                    if (_paused) session.Pause();
                }
                ScanResultManaged result = await session.WaitAsync(runCancellation.Token).ConfigureAwait(false);
                int done = Interlocked.Increment(ref completed);
                progress?.Report(new(path, new(result.DirCount, result.FileCount, result.TotalBytes), done, targets.Length));
                return new TargetScanOutcome(path, session.Scanner, result, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !runCancellation.IsCancellationRequested)
            {
                Interlocked.Increment(ref completed);
                return new TargetScanOutcome(path, session?.Scanner ?? ScannerKind.Unknown, null, ex);
            }
            finally
            {
                if (session is not null)
                {
                    lock (_gate) _active.Remove(session);
                    session.Dispose();
                }
                concurrency.Release();
            }
        }).ToArray();

        try { return await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _paused = false;
                _runCancellation?.Dispose();
                _runCancellation = null;
                _activeRun = null;
            }
        }
    }

    internal static string NormalizeTarget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string trimmed = path.Trim();
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            throw new ArgumentException("A drive path must include a root separator, such as C:\\.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }

    public void Pause() { lock (_gate) { _paused = true; foreach (ScanSession scan in _active) scan.Pause(); } }
    public void Resume() { lock (_gate) { _paused = false; foreach (ScanSession scan in _active) scan.Resume(); } }
    public void Cancel() { lock (_gate) { _runCancellation?.Cancel(); foreach (ScanSession scan in _active) scan.Cancel(); } }

    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_gate) { if (_disposed) return; _disposed = true; run = _activeRun; }
        Cancel();
        if (run is not null)
            try { await run.ConfigureAwait(false); } catch (Exception) { }
    }

    sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value)
        {
            // Never unwind a managed exception through the reverse-P/Invoke frame.
            try { callback(value); }
            catch { }
        }
    }
}
