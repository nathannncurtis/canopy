using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class DiagnosticBundleTests
{
    [Fact]
    public void RedactsDriveUncUnicodePathsAndUserNames()
    {
        const string log = "Open C:\\Users\\Ålice\\résumé.txt; share=\\\\server\\private$\\用户\\secret.bin and \"D:\\Work Files\\Nathan\\report.csv\".";

        string redacted = DiagnosticBundle.RedactPaths(log);

        Assert.DoesNotContain("Ålice", redacted);
        Assert.DoesNotContain("résumé.txt", redacted);
        Assert.DoesNotContain("server", redacted);
        Assert.DoesNotContain("用户", redacted);
        Assert.DoesNotContain("Nathan", redacted);
        Assert.DoesNotContain("report.csv", redacted);
        Assert.Equal(3, Count(redacted, "[REDACTED_PATH]"));
    }

    [Fact]
    public async Task BundleHasStableEntriesManifestAndRedactedLogs()
    {
        await using var output = new MemoryStream();
        await DiagnosticBundle.CreateAsync(Input(), output, TestContext.Current.CancellationToken);
        Assert.True(output.CanWrite);

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(["manifest.json", "logs.txt"], archive.Entries.Select(entry => entry.FullName));
        Assert.All(archive.Entries, entry => Assert.Equal(1980, entry.LastWriteTime.Year));

        using JsonDocument manifest = JsonDocument.Parse(await ReadAsync(archive.GetEntry("manifest.json")!));
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.2.3", manifest.RootElement.GetProperty("applicationVersion").GetString());
        Assert.False(manifest.RootElement.GetProperty("sensitivePathsIncluded").GetBoolean());
        string logs = await ReadAsync(archive.GetEntry("logs.txt")!);
        Assert.Contains("[REDACTED_PATH]", logs);
        Assert.DoesNotContain("secret.txt", logs);
    }

    [Fact]
    public async Task SensitivePathOptInPreservesOriginalLog()
    {
        DiagnosticBundleInput input = Input() with { IncludeSensitivePaths = true };
        await using var output = new MemoryStream();
        await DiagnosticBundle.CreateAsync(input, output, TestContext.Current.CancellationToken);
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);

        Assert.Equal(input.LogText, await ReadAsync(archive.GetEntry("logs.txt")!));
    }

    [Fact]
    public async Task CancellationWritesNothingAndLeavesCallerStreamOpen()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var output = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiagnosticBundle.CreateAsync(Input(), output, cancellation.Token));

        Assert.Equal(0, output.Length);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task RejectsLogsOverSizeBound()
    {
        DiagnosticBundleInput input = Input() with
        {
            LogText = new string('x', DiagnosticBundle.MaxLogBytes + 1),
        };
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            DiagnosticBundle.CreateAsync(input, output, TestContext.Current.CancellationToken));
    }

    static DiagnosticBundleInput Input() => new()
    {
        ApplicationVersion = "1.2.3",
        OperatingSystem = "Windows test",
        RuntimeVersion = ".NET test",
        ProcessArchitecture = "x64",
        LogText = "Failed to read C:\\Users\\Ålice\\secret.txt",
    };

    static async Task<string> ReadAsync(ZipArchiveEntry entry)
    {
        await using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
