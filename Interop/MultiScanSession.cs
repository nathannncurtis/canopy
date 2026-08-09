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

    // Compatibility surface: returns results only when every target succeeds.
    // Call ScanOutcomesAsync to intentionally consume partial results and diagnostics.
    public async Task<IReadOnlyList<TargetScanResult>> ScanAsync(IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress = null, CancellationToken cancellationToken = default,
        ScanOptions? options = null)
    {
        IReadOnlyList<TargetScanOutcome> outcomes =
            await ScanOutcomesAsync(paths, progress, cancellationToken, options).ConfigureAwait(false);
        return MaterializeSuccessfulOutcomes(outcomes);
    }

    internal static IReadOnlyList<TargetScanResult> MaterializeSuccessfulOutcomes(
        IReadOnlyList<TargetScanOutcome> outcomes)
    {
        Exception[] failures = outcomes.Where(item => !item.Succeeded)
            .Select(item => new InvalidOperationException($"Scan target '{item.Path}' failed.", item.Error))
            .ToArray();
        if (failures.Length != 0)
            throw new AggregateException(
                "One or more scan targets failed; partial results were not presented as complete.", failures);
        return outcomes.Select(item => new TargetScanResult(item.Path, item.Scanner, item.Result!)).ToArray();
    }

    public Task<IReadOnlyList<TargetScanOutcome>> ScanOutcomesAsync(IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress = null, CancellationToken cancellationToken = default,
        ScanOptions? options = null)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<TargetScanOutcome>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource runCancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runCancellation is not null)
                return Task.FromException<IReadOnlyList<TargetScanOutcome>>(
                    new InvalidOperationException("This coordinator already has an active scan."));
            _paused = false;
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCancellation = runCancellation;
            _activeRun = completion.Task;
        }
        _ = CompletePublishedRunAsync(paths, progress, options, runCancellation, completion);
        return completion.Task;
    }

    async Task CompletePublishedRunAsync(IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress, ScanOptions? options,
        CancellationTokenSource runCancellation,
        TaskCompletionSource<IReadOnlyList<TargetScanOutcome>> completion)
    {
        await Task.Yield();
        try
        {
            completion.TrySetResult(await RunAdmittedAsync(
                paths, progress, options, runCancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex) when (runCancellation.IsCancellationRequested)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _paused = false;
                    _runCancellation = null;
                    _activeRun = null;
                }
            }
            runCancellation.Dispose();
        }
    }

    async Task<IReadOnlyList<TargetScanOutcome>> RunAdmittedAsync(IEnumerable<string> paths,
        IProgress<TargetScanProgress>? progress, ScanOptions? options,
        CancellationTokenSource runCancellation)
    {
        ScanOptions? scanOptions = options is null ? null : options with
        {
            ExcludedPatterns = options.ExcludedPatterns.ToArray(),
            ExcludedExtensions = options.ExcludedExtensions.ToArray(),
        };
        scanOptions?.Validate();
        string[] targets = paths.Select(NormalizeTarget).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) throw new ArgumentException("At least one path is required.", nameof(paths));

        using var concurrency = new SemaphoreSlim(_maxConcurrency);
        int completed = 0;
        Task<TargetScanOutcome>[] tasks = targets.Select(async path =>
        {
            await concurrency.WaitAsync(runCancellation.Token).ConfigureAwait(false);
            ScanSession? session = null;
            try
            {
                var itemProgress = progress is null ? null : new CallbackProgress<ScanProgress>(value =>
                    ReportSafely(progress,
                        new(path, value, Volatile.Read(ref completed), targets.Length)));
                session = await Task.Run(() => ScanSession.Start(path, itemProgress, scanOptions),
                    runCancellation.Token).ConfigureAwait(false);
                bool pause;
                lock (_gate)
                {
                    _active.Add(session);
                    pause = _paused;
                }
                if (pause) session.Pause();
                ScanResultManaged result = await session.WaitAsync(runCancellation.Token).ConfigureAwait(false);
                int done = Interlocked.Increment(ref completed);
                ReportSafely(progress,
                    new(path, new(result.DirCount, result.FileCount, result.TotalBytes), done, targets.Length));
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

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    internal static string NormalizeTarget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string trimmed = path.Trim();
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            throw new ArgumentException("A drive path must include a root separator, such as C:\\.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }

    public void Pause()
    {
        ScanSession[] active;
        lock (_gate) { _paused = true; active = _active.ToArray(); }
        foreach (ScanSession scan in active) InvokeSnapshotControl(scan.Pause);
    }

    public void Resume()
    {
        ScanSession[] active;
        lock (_gate) { _paused = false; active = _active.ToArray(); }
        foreach (ScanSession scan in active) InvokeSnapshotControl(scan.Resume);
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        ScanSession[] active;
        lock (_gate) { cancellation = _runCancellation; active = _active.ToArray(); }
        if (cancellation is not null) InvokeSnapshotControl(cancellation.Cancel);
        foreach (ScanSession scan in active) InvokeSnapshotControl(scan.Cancel);
    }

    internal static void InvokeSnapshotControl(Action control)
    {
        try { control(); }
        // A run may finish and dispose an object after it was snapshotted under _gate.
        catch (ObjectDisposedException) { }
    }

    internal CancellationToken ActiveRunCancellationToken
    {
        get { lock (_gate) return _runCancellation?.Token ?? default; }
    }

    internal static void ReportSafely<T>(IProgress<T>? progress, T value)
    {
        if (progress is null) return;
        try { progress.Report(value); }
        catch { }
    }

    static void InvokeSafely<T>(Action<T> callback, T value)
    {
        try { callback(value); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_gate) { if (_disposed) return; _disposed = true; run = _activeRun; }
        Cancel();
        if (run is not null)
            try { await run.ConfigureAwait(false); } catch (Exception) { }
    }

    internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value)
        {
            // Never unwind a managed exception through the reverse-P/Invoke frame.
            InvokeSafely(callback, value);
        }
    }
}
