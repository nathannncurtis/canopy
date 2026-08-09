using System.Diagnostics;
using System.Text.Json;

namespace SizeMonitor.Interop;

public sealed class IsolatedWorkerException(string path, int exitCode, string diagnostics)
    : IOException($"The isolated scanner for '{path}' exited unexpectedly ({exitCode}). " +
        (string.IsNullOrWhiteSpace(diagnostics) ? "The native engine may have crashed." : diagnostics))
{
    public string TargetPath { get; } = path;
    public int ExitCode { get; } = exitCode;
    public string Diagnostics { get; } = diagnostics;
}

public sealed record IsolatedTargetUpdate(string Path, ScanProgress Progress,
    TargetScanOutcome? CompletedOutcome, int CompletedTargets, int TotalTargets);

/// <summary>Contains native failures inside short-lived worker processes.</summary>
public sealed class IsolatedMultiScanSession(string workerExecutable, int maxConcurrency = 2) : IAsyncDisposable
{
    readonly object _gate = new();
    readonly List<Process> _workers = [];
    CancellationTokenSource? _runCancellation;
    bool _disposed;

    public async Task<IReadOnlyList<TargetScanOutcome>> ScanOutcomesAsync(IEnumerable<string> paths,
        IProgress<IsolatedTargetUpdate>? progress = null, CancellationToken cancellationToken = default,
        ScanOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string executable = Path.GetFullPath(workerExecutable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The isolated scan worker is unavailable.", executable);
        options?.Validate();
        string[] targets = paths.Select(MultiScanSession.NormalizeTarget).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) throw new ArgumentException("At least one path is required.", nameof(paths));
        lock (_gate)
        {
            if (_runCancellation is not null) throw new InvalidOperationException("This coordinator already has an active scan.");
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        using var concurrency = new SemaphoreSlim(maxConcurrency);
        int completed = 0;
        try
        {
            return await Task.WhenAll(targets.Select(async path =>
            {
                await concurrency.WaitAsync(_runCancellation.Token).ConfigureAwait(false);
                try
                {
                    TargetScanOutcome outcome = await RunWorkerAsync(executable, path, options, value =>
                        progress?.Report(new(path, value, null, Volatile.Read(ref completed), targets.Length)), _runCancellation.Token).ConfigureAwait(false);
                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new(path, outcome.Result is null ? new(0, 0, 0) : new(outcome.Result.DirCount, outcome.Result.FileCount, outcome.Result.TotalBytes, ScanPhase.Complete, true), outcome, done, targets.Length));
                    return outcome;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !_runCancellation.IsCancellationRequested)
                {
                    int done = Interlocked.Increment(ref completed);
                    var outcome = new TargetScanOutcome(path, ScannerKind.Unknown, null, ex);
                    progress?.Report(new(path, new ScanProgress(0, 0, 0), outcome, done, targets.Length));
                    return outcome;
                }
                finally { concurrency.Release(); }
            })).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) { _runCancellation?.Dispose(); _runCancellation = null; }
        }
    }

    async Task<TargetScanOutcome> RunWorkerAsync(string executable, string path, ScanOptions? options,
        Action<ScanProgress> onProgress, CancellationToken token)
    {
        string directory = Path.Combine(Path.GetTempPath(), "canopy-isolated-scans");
        Directory.CreateDirectory(directory);
        string snapshot = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".canopy");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--isolation-worker"); start.ArgumentList.Add("--path"); start.ArgumentList.Add(path);
        start.ArgumentList.Add("--worker-snapshot"); start.ArgumentList.Add(snapshot);
        AddOptions(start.ArgumentList, options);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new IOException("Could not start the isolated scanner.");
            lock (_gate) _workers.Add(process);
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            Task<string> errors = process.StandardError.ReadToEndAsync(token);
            ScannerKind scanner = ScannerKind.Unknown;
            while (await process.StandardOutput.ReadLineAsync(token) is string line)
            {
                if (line.Length > 64 * 1024) throw new InvalidDataException("The isolated worker emitted an oversized message.");
                using JsonDocument json = JsonDocument.Parse(line);
                string? type = json.RootElement.GetProperty("type").GetString();
                if (type == "progress") onProgress(new(json.RootElement.GetProperty("dirs").GetUInt64(),
                    json.RootElement.GetProperty("files").GetUInt64(), json.RootElement.GetProperty("bytes").GetUInt64()));
                else if (type == "complete" && Enum.TryParse(json.RootElement.GetProperty("scanner").GetString(), out ScannerKind parsed)) scanner = parsed;
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string diagnostics = (await errors.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0) throw new IsolatedWorkerException(path, process.ExitCode, diagnostics);
            ScanResultManaged result = await ScanSnapshotStore.LoadAsync(snapshot, token).ConfigureAwait(false);
            return new(path, scanner, result, null);
        }
        finally
        {
            lock (_gate) _workers.Remove(process);
            try { File.Delete(snapshot); } catch (IOException) { }
        }
    }

    static void AddOptions(System.Collections.ObjectModel.Collection<string> args, ScanOptions? options)
    {
        if (options is null) return;
        foreach (string value in options.ExcludedPatterns) { args.Add("--exclude"); args.Add(value); }
        foreach (string value in options.ExcludedExtensions) { args.Add("--exclude-ext"); args.Add(value); }
        if (options.MinimumFileSize > 0) { args.Add("--min-size"); args.Add(options.MinimumFileSize.ToString()); }
        if (options.MaximumFileSize is ulong maximum) { args.Add("--max-size"); args.Add(maximum.ToString()); }
        if (options.MaximumDepth is uint depth) { args.Add("--max-depth"); args.Add(depth.ToString()); }
        if (options.WorkerThreads is uint workers) { args.Add("--workers"); args.Add(workers.ToString()); }
        if (!options.IncludeHidden) args.Add("--exclude-hidden"); if (!options.IncludeSystem) args.Add("--exclude-system");
        if (!options.IncludeTemporary) args.Add("--exclude-temporary"); if (!options.IncludeReparsePoints) args.Add("--exclude-reparse");
        if (options.ForceDirectoryScanner) args.Add("--force-directory");
    }

    public void Cancel() { lock (_gate) { _runCancellation?.Cancel(); foreach (Process worker in _workers.ToArray()) try { if (!worker.HasExited) worker.Kill(true); } catch (InvalidOperationException) { } } }
    public ValueTask DisposeAsync() { if (_disposed) return ValueTask.CompletedTask; _disposed = true; Cancel(); return ValueTask.CompletedTask; }
}
