using System.Text.Json;
using SizeMonitor.Interop;

namespace SizeMonitor.Cli;

public static class Program
{
    public const int Success = 0;
    public const int RuntimeFailure = 1;
    public const int InvalidArguments = 2;
    public const int PartialFailure = 3;
    public const int AllTargetsFailed = 4;
    public const int Cancelled = 130;

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try { options = Options.Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine($"canopy-cli: {ex.Message}");
            Console.Error.WriteLine("Run canopy-cli --help for usage.");
            return InvalidArguments;
        }
        if (options.Help) { Console.WriteLine(HelpText); return Success; }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (options.BenchmarkRuns > 0)
            {
                ScanBenchmarkResult benchmark = await ScanBenchmark.RunAsync(options.Paths[0],
                    new ScanBenchmarkOptions
                    {
                        WarmupRuns = options.WarmupRuns,
                        MeasuredRuns = options.BenchmarkRuns,
                        ScanOptions = options.ScanOptions,
                    }, cancellation.Token);
                if (options.Json) Console.WriteLine(JsonSerializer.Serialize(benchmark));
                else WriteBenchmark(benchmark);
                return benchmark.DatasetChanged ? PartialFailure : Success;
            }
            await using var session = new MultiScanSession(options.Concurrency);
            IReadOnlyList<TargetScanOutcome> outcomes = await session.ScanOutcomesAsync(
                options.Paths, cancellationToken: cancellation.Token, options: options.ScanOptions);
            TargetScanResult[] successes = outcomes.Where(x => x.Succeeded)
                .Select(x => new TargetScanResult(x.Path, x.Scanner, x.Result!)).ToArray();
            TargetScanOutcome[] failures = outcomes.Where(x => !x.Succeeded).ToArray();
            ScanResultManaged? combined = successes.Length == 0 ? null : ScanResultCombiner.CombineTargets(successes);
            if (combined is not null && options.OutputPath is not null)
                await ExportAsync(combined, options.OutputPath, options.Format, cancellation.Token);
            if (options.Json) WriteJson(outcomes, combined, options.OutputPath);
            else WriteHuman(outcomes, combined, options.OutputPath);
            return failures.Length == 0 ? Success : successes.Length == 0 ? AllTargetsFailed : PartialFailure;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (options.Json) Console.WriteLine("{\"status\":\"cancelled\",\"exitCode\":130}");
            else Console.Error.WriteLine("Scan cancelled.");
            return Cancelled;
        }
        catch (Exception ex)
        {
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", exitCode = RuntimeFailure,
                    error = ex.Message, type = ex.GetType().Name }));
            else Console.Error.WriteLine($"canopy-cli: {ex.Message}");
            return RuntimeFailure;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    static async Task ExportAsync(ScanResultManaged result, string path, string format, CancellationToken token)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                switch (format)
                {
                    case "csv": await ScanResultExporter.ExportCsvAsync(result, stream, token); break;
                    case "json": await ScanResultExporter.ExportJsonAsync(result, stream, token); break;
                    case "xml": await ScanReportExporter.ExportXmlAsync(result, stream, token); break;
                    case "html": await ScanReportExporter.ExportHtmlAsync(result, stream, token); break;
                    default: throw new ArgumentException($"Unsupported output format '{format}'.");
                }
                await stream.FlushAsync(token);
            }
            File.Move(temporary, fullPath, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
    }

    static void WriteJson(IReadOnlyList<TargetScanOutcome> outcomes, ScanResultManaged? result, string? output)
    {
        var payload = new
        {
            status = outcomes.All(x => x.Succeeded) ? "success" : outcomes.Any(x => x.Succeeded) ? "partial" : "failed",
            totalBytes = result?.TotalBytes ?? 0, fileCount = result?.FileCount ?? 0,
            directoryCount = result?.DirCount ?? 0, elapsedSeconds = result?.ElapsedSec ?? 0,
            output,
            targets = outcomes.Select(x => new { path = x.Path, succeeded = x.Succeeded,
                scanner = x.Scanner.ToString(), totalBytes = x.Result?.TotalBytes,
                fileCount = x.Result?.FileCount, directoryCount = x.Result?.DirCount,
                error = x.Error?.Message, errorType = x.Error?.GetType().Name }),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload));
    }

    static void WriteHuman(IReadOnlyList<TargetScanOutcome> outcomes, ScanResultManaged? result, string? output)
    {
        foreach (TargetScanOutcome outcome in outcomes)
            if (outcome.Succeeded)
                Console.WriteLine($"OK   {outcome.Path} [{outcome.Scanner}] {outcome.Result!.TotalBytes:N0} bytes, {outcome.Result.FileCount:N0} files");
            else Console.Error.WriteLine($"FAIL {outcome.Path}: {outcome.Error?.Message ?? "unknown error"}");
        if (result is not null)
            Console.WriteLine($"Total: {result.TotalBytes:N0} bytes, {result.FileCount:N0} files, {result.DirCount:N0} directories");
        if (output is not null) Console.WriteLine($"Exported: {Path.GetFullPath(output)}");
    }

    static void WriteBenchmark(ScanBenchmarkResult result)
    {
        Console.WriteLine($"Dataset: {result.DatasetIdentity} ({(result.DatasetChanged ? "changed between runs" : "stable")})");
        Console.WriteLine($"Scanner: {result.Scanner}; {result.Runs.Count} measured runs");
        Console.WriteLine($"Median: {result.MedianSeconds:F3}s; p95: {result.P95Seconds:F3}s; " +
                          $"throughput: {result.MedianBytesPerSecond / (1024 * 1024):F1} MiB/s");
        Console.WriteLine($"Machine: {result.OperatingSystem}; {result.Architecture}; " +
                          $"{result.LogicalProcessors} logical processors; {result.Runtime}");
    }

    internal sealed record Options(IReadOnlyList<string> Paths, int Concurrency, bool Json,
        string? OutputPath, string Format, ScanOptions? ScanOptions, bool Help,
        int BenchmarkRuns, int WarmupRuns)
    {
        public static Options Parse(string[] args)
        {
            var paths = new List<string>(); var patterns = new List<string>(); var extensions = new List<string>();
            int concurrency = 2; bool json = false, help = false, forceDirectory = false;
            int benchmarkRuns = 0, warmupRuns = 1;
            bool includeHidden = true, includeSystem = true, includeTemporary = true, includeReparse = true;
            uint? depth = null, workers = null; ulong minimum = 0; ulong? maximum = null;
            string? output = null; string format = "json";
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{arg} requires a value.");
                switch (arg)
                {
                    case "-h" or "--help": help = true; break;
                    case "-p" or "--path": paths.Add(Next()); break;
                    case "--json": json = true; break;
                    case "-j" or "--concurrency": concurrency = ParsePositiveInt(Next(), arg, 256); break;
                    case "--exclude": patterns.Add(Next()); break;
                    case "--exclude-ext": extensions.Add(Next()); break;
                    case "--min-size": minimum = ParseSize(Next(), arg); break;
                    case "--max-size": maximum = ParseSize(Next(), arg); break;
                    case "--max-depth": depth = checked((uint)ParsePositiveInt(Next(), arg, int.MaxValue)); break;
                    case "--workers": workers = checked((uint)ParsePositiveInt(Next(), arg, 32)); break;
                    case "--exclude-hidden": includeHidden = false; break;
                    case "--exclude-system": includeSystem = false; break;
                    case "--exclude-temporary": includeTemporary = false; break;
                    case "--exclude-reparse": includeReparse = false; break;
                    case "--force-directory": forceDirectory = true; break;
                    case "--benchmark": benchmarkRuns = ParsePositiveInt(Next(), arg, 30); break;
                    case "--warmup": warmupRuns = ParseNonNegativeInt(Next(), arg, 10); break;
                    case "-o" or "--output": output = Next(); break;
                    case "--format": format = Next().ToLowerInvariant(); break;
                    default: if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option '{arg}'."); else paths.Add(arg); break;
                }
            }
            if (!help && paths.Count == 0) throw new ArgumentException("At least one scan path is required.");
            if (output is not null && format is not ("csv" or "json" or "xml" or "html"))
                throw new ArgumentException("--format must be csv, json, xml, or html.");
            if (benchmarkRuns > 0 && benchmarkRuns < 2)
                throw new ArgumentException("--benchmark requires at least 2 measured runs.");
            if (benchmarkRuns > 0 && paths.Count != 1)
                throw new ArgumentException("Benchmark mode requires exactly one scan path.");
            if (benchmarkRuns > 0 && output is not null)
                throw new ArgumentException("Benchmark mode cannot be combined with result export.");
            var scan = new ScanOptions { MaximumDepth = depth, WorkerThreads = workers, MinimumFileSize = minimum,
                MaximumFileSize = maximum, IncludeHidden = includeHidden, IncludeSystem = includeSystem,
                IncludeTemporary = includeTemporary, IncludeReparsePoints = includeReparse,
                ForceDirectoryScanner = forceDirectory, ExcludedPatterns = patterns, ExcludedExtensions = extensions };
            scan.Validate();
            return new(paths, concurrency, json, output, format, scan, help, benchmarkRuns, warmupRuns);
        }
        static int ParsePositiveInt(string text, string option, int max) =>
            int.TryParse(text, out int value) && value is > 0 && value <= max ? value :
                throw new ArgumentException($"{option} must be between 1 and {max}.");
        static int ParseNonNegativeInt(string text, string option, int max) =>
            int.TryParse(text, out int value) && value is >= 0 && value <= max ? value :
                throw new ArgumentException($"{option} must be between 0 and {max}.");
        static ulong ParseSize(string text, string option) => ByteSizeParser.TryParse(text, out ulong value) ? value :
            throw new ArgumentException($"{option} is not a valid byte size.");
    }

    const string HelpText = """
Canopy headless scanner
Usage: canopy-cli [options] <path> [path ...]
  -p, --path PATH            Add a scan target
  -j, --concurrency N        Concurrent targets (default 2)
      --json                 Emit one stable JSON summary to stdout
      --exclude GLOB         Exclude a relative-path/name glob (repeatable)
      --exclude-ext EXT      Exclude an extension (repeatable)
      --min-size SIZE        Minimum file size, e.g. 10 MiB
      --max-size SIZE        Maximum file size
      --max-depth N          Maximum scan depth
      --workers N            Directory scanner workers (1-32)
      --exclude-hidden|--exclude-system|--exclude-temporary|--exclude-reparse
      --force-directory      Do not select the MFT scanner
      --benchmark N          Run a read-only benchmark with N measured runs (2-30)
      --warmup N             Warmup runs before measurement (0-10; default 1)
  -o, --output FILE          Atomically export combined successful results
      --format FORMAT        csv, json, xml, or html (default json)

Exit codes: 0 success; 1 runtime failure; 2 invalid arguments; 3 partial target failure;
4 all targets failed; 130 cancelled by Ctrl+C.
""";
}
