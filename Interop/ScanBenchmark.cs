using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SizeMonitor.Interop;

public sealed record ScanBenchmarkOptions
{
    public int WarmupRuns { get; init; } = 1;
    public int MeasuredRuns { get; init; } = 5;
    public ScanOptions? ScanOptions { get; init; }

    public void Validate()
    {
        if (WarmupRuns is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(WarmupRuns));
        if (MeasuredRuns is < 2 or > 30) throw new ArgumentOutOfRangeException(nameof(MeasuredRuns));
        ScanOptions?.Validate();
    }
}

public sealed record ScanBenchmarkRun(
    int Run,
    double ElapsedSeconds,
    double BytesPerSecond,
    ulong TotalBytes,
    ulong FileCount,
    ulong DirectoryCount,
    ScannerKind Scanner,
    string DatasetIdentity);

public sealed record ScanBenchmarkResult(
    int SchemaVersion,
    string DatasetIdentity,
    bool DatasetChanged,
    ScannerKind Scanner,
    IReadOnlyList<ScanBenchmarkRun> Runs,
    double MinimumSeconds,
    double MedianSeconds,
    double P95Seconds,
    double MedianBytesPerSecond,
    string OperatingSystem,
    string Architecture,
    int LogicalProcessors,
    string Runtime);

/// <summary>Runs read-only, repeatable scan benchmarks with warmup and machine context.</summary>
public static class ScanBenchmark
{
    public static async Task<ScanBenchmarkResult> RunAsync(string path,
        ScanBenchmarkOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new ScanBenchmarkOptions();
        options.Validate();
        string fullPath = Path.GetFullPath(path);
        return await RunAsync(async token =>
        {
            using ScanSession session = ScanSession.Start(fullPath, null, options.ScanOptions);
            ScanResultManaged result = await session.WaitAsync(token).ConfigureAwait(false);
            return (result, session.Scanner);
        }, options, DatasetPathToken(fullPath), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ScanBenchmarkResult> RunAsync(
        Func<CancellationToken, Task<(ScanResultManaged Result, ScannerKind Scanner)>> scan,
        ScanBenchmarkOptions options,
        string datasetToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetToken);
        options.Validate();

        for (int warmup = 0; warmup < options.WarmupRuns; warmup++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await scan(cancellationToken).ConfigureAwait(false);
        }

        var runs = new List<ScanBenchmarkRun>(options.MeasuredRuns);
        for (int index = 0; index < options.MeasuredRuns; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();
            (ScanResultManaged result, ScannerKind scanner) = await scan(cancellationToken).ConfigureAwait(false);
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            string identity = DatasetIdentity(datasetToken, result);
            runs.Add(new(index + 1, seconds, seconds > 0 ? result.TotalBytes / seconds : 0,
                result.TotalBytes, result.FileCount, result.DirCount, scanner, identity));
        }

        double[] times = runs.Select(run => run.ElapsedSeconds).Order().ToArray();
        double[] throughput = runs.Select(run => run.BytesPerSecond).Order().ToArray();
        string firstIdentity = runs[0].DatasetIdentity;
        ScannerKind firstScanner = runs[0].Scanner;
        return new(1, firstIdentity, runs.Any(run => run.DatasetIdentity != firstIdentity), firstScanner, runs,
            times[0], Percentile(times, 0.5), Percentile(times, 0.95), Percentile(throughput, 0.5),
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, RuntimeInformation.FrameworkDescription);
    }

    static double Percentile(double[] sorted, double percentile)
    {
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    static string DatasetPathToken(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    static string DatasetIdentity(string token, ScanResultManaged result)
    {
        string facts = $"{token}|{result.TotalBytes}|{result.FileCount}|{result.DirCount}|{result.Nodes.Length}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts)))[..24];
    }
}
