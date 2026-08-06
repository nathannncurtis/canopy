using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SizeMonitor.Interop;

namespace SizeMonitor.LocalApi;

public sealed class LocalScanApiHost : IAsyncDisposable
{
    readonly WebApplication _application;
    readonly ConcurrentDictionary<Guid, ScanJob> _jobs = new();
    readonly SemaphoreSlim _scanSlots;
    readonly CancellationTokenSource _lifetime = new();
    bool _started;

    public LocalScanApiHost(int port = 0, int maximumConcurrentScans = 2, string? bearerToken = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (maximumConcurrentScans is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentScans));
        BearerToken = bearerToken ?? GenerateToken();
        if (BearerToken.Length < 32) throw new ArgumentException("Bearer tokens must contain at least 32 characters.", nameof(bearerToken));
        _scanSlots = new(maximumConcurrentScans, maximumConcurrentScans);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.WriteIndented = false);
        _application = builder.Build();
        MapPipeline();
    }

    public string BearerToken { get; }
    public Uri Address
    {
        get
        {
            string address = _application.Urls.SingleOrDefault()
                ?? throw new InvalidOperationException("The API has not started.");
            return new Uri(address.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("The API has already started.");
        _started = true;
        await _application.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    void MapPipeline()
    {
        _application.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            string supplied = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (!supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !TokenEquals(supplied[prefix.Length..], BearerToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context).ConfigureAwait(false);
        });

        _application.MapGet("/health", () => Results.Json(new { status = "ok" }));
        _application.MapPost("/scans", (ScanRequest request) => Submit(request));
        _application.MapGet("/scans/{id:guid}", (Guid id) =>
            _jobs.TryGetValue(id, out ScanJob? job) ? Results.Json(job.Snapshot()) : Results.NotFound());
        _application.MapDelete("/scans/{id:guid}", (Guid id) =>
        {
            if (!_jobs.TryGetValue(id, out ScanJob? job)) return Results.NotFound();
            job.Cancel();
            return Results.Accepted($"/scans/{id}", job.Snapshot());
        });
    }

    IResult Submit(ScanRequest request)
    {
        string? error = Validate(request);
        if (error is not null) return Results.BadRequest(new { error });
        var job = new ScanJob(Guid.NewGuid(), request.Paths.Select(Path.GetFullPath).ToArray());
        if (!_jobs.TryAdd(job.Id, job)) throw new InvalidOperationException("Could not allocate scan identifier.");
        job.Execution = RunJobAsync(job, request, _lifetime.Token);
        return Results.Accepted($"/scans/{job.Id}", job.Snapshot());
    }

    async Task RunJobAsync(ScanJob job, ScanRequest request, CancellationToken lifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, lifetime);
        try
        {
            await _scanSlots.WaitAsync(linked.Token).ConfigureAwait(false);
            job.MarkRunning();
            try
            {
                await using var session = new MultiScanSession(Math.Min(4, request.Paths.Count));
                var options = new ScanOptions
                {
                    ForceDirectoryScanner = request.ForceDirectoryScanner,
                    MaximumDepth = request.MaximumDepth,
                    ExcludedPatterns = request.ExcludedPatterns ?? [],
                    ExcludedExtensions = request.ExcludedExtensions ?? [],
                };
                options.Validate();
                IReadOnlyList<TargetScanOutcome> outcomes = await session.ScanOutcomesAsync(
                    job.Paths, cancellationToken: linked.Token, options: options).ConfigureAwait(false);
                job.Complete(outcomes);
            }
            finally { _scanSlots.Release(); }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { job.MarkCancelled(); }
        catch (Exception ex) { job.Fail(ex); }
    }

    static string? Validate(ScanRequest request)
    {
        if (request.Paths is null || request.Paths.Count is < 1 or > 16) return "paths must contain 1 to 16 entries";
        if (request.Paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 32_767)) return "a path is empty or too long";
        if (request.MaximumDepth == 0) return "maximumDepth must be at least 1";
        if ((request.ExcludedPatterns?.Count ?? 0) > 1000 || (request.ExcludedExtensions?.Count ?? 0) > 1000)
            return "too many exclusion entries";
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        foreach (ScanJob job in _jobs.Values) job.Cancel();
        Task[] executions = _jobs.Values.Select(job => job.Execution ?? Task.CompletedTask).ToArray();
        await Task.WhenAll(executions.Select(async task => { try { await task.ConfigureAwait(false); } catch { } }));
        if (_started) await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        _scanSlots.Dispose();
        _lifetime.Dispose();
        foreach (ScanJob job in _jobs.Values) job.Dispose();
    }

    static string GenerateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static bool TokenEquals(string supplied, string expected)
    {
        byte[] left = Encoding.UTF8.GetBytes(supplied), right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public sealed record ScanRequest(IReadOnlyList<string> Paths, bool ForceDirectoryScanner = false,
    uint? MaximumDepth = null, IReadOnlyList<string>? ExcludedPatterns = null,
    IReadOnlyList<string>? ExcludedExtensions = null);

sealed class ScanJob : IDisposable
{
    readonly object _gate = new();
    string _status = "queued";
    DateTimeOffset? _startedUtc, _completedUtc;
    object? _result;
    public ScanJob(Guid id, string[] paths) { Id = id; Paths = paths; }
    public Guid Id { get; }
    public string[] Paths { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public Task? Execution { get; set; }
    public void Cancel() => Cancellation.Cancel();
    public void MarkRunning() { lock (_gate) { _status = "running"; _startedUtc = DateTimeOffset.UtcNow; } }
    public void MarkCancelled() { lock (_gate) { _status = "cancelled"; _completedUtc = DateTimeOffset.UtcNow; } }
    public void Fail(Exception ex) { lock (_gate) { _status = "failed"; _completedUtc = DateTimeOffset.UtcNow; _result = new { error = ex.Message, type = ex.GetType().Name }; } }
    public void Complete(IReadOnlyList<TargetScanOutcome> outcomes)
    {
        lock (_gate)
        {
            _status = outcomes.All(x => x.Succeeded) ? "completed" : outcomes.Any(x => x.Succeeded) ? "partial" : "failed";
            _completedUtc = DateTimeOffset.UtcNow;
            _result = new { targets = outcomes.Select(x => new { path = x.Path, succeeded = x.Succeeded,
                scanner = x.Scanner.ToString(), totalBytes = x.Result?.TotalBytes, fileCount = x.Result?.FileCount,
                directoryCount = x.Result?.DirCount, error = x.Error?.Message }) };
        }
    }
    public object Snapshot() { lock (_gate) return new { id = Id, status = _status, paths = Paths,
        startedUtc = _startedUtc, completedUtc = _completedUtc, result = _result }; }
    public void Dispose() => Cancellation.Dispose();
}
