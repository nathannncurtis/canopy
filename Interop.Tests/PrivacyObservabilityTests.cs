using System.Net;
using System.Text.Json;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class PrivacyObservabilityTests
{
    [Fact]
    public void DisabledTelemetryEmitsNothingAndEnabledPayloadIsCoarse()
    {
        Assert.Null(PerformanceTelemetry.Create(new(), "1.0.0", ScannerKind.Directory,
            TimeSpan.FromSeconds(4), 123, 42, 987654, "success"));
        PerformanceTelemetryEvent value = Assert.IsType<PerformanceTelemetryEvent>(
            PerformanceTelemetry.Create(new() { PerformanceTelemetry = true }, "1.0.0",
                ScannerKind.Directory, TimeSpan.FromSeconds(4), 123, 42, 987654, "success"));
        string json = JsonSerializer.Serialize(value);
        Assert.Contains("1-5s", json);
        Assert.DoesNotContain("987654", json);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrashPreviewStripsSourcePathsAndSubmissionRequiresConsent()
    {
        Exception exception = Assert.Throws<InvalidOperationException>(ThrowFromFixture);
        CrashReportPreview preview = CrashReportService.CreatePreview(exception, "1.2.3");
        string json = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("PrivacyObservabilityTests.cs", json);
        Assert.DoesNotContain("C:\\", json);

        var handler = new RecordingHandler();
        var service = new CrashReportService(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(
            new Uri("https://example.test/crash"), preview, new(), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
        await service.SubmitAsync(new Uri("https://example.test/crash"), preview,
            new() { CrashReports = true }, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConsentStoreDefaultsOffRoundTripsAndDeletesLocally()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canopy-observability-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ObservabilityConsentStore(directory);
            Assert.Equal(new ObservabilityConsent(), await store.LoadAsync(TestContext.Current.CancellationToken));
            var consent = new ObservabilityConsent { CrashReports = true, LogLevel = DiagnosticLogLevel.Trace };
            await store.SaveAsync(consent, TestContext.Current.CancellationToken);
            Assert.Equal(consent, await store.LoadAsync(TestContext.Current.CancellationToken));
            await store.DeleteLocalDataAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new ObservabilityConsent(), await store.LoadAsync(TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DiagnosticLoggerFiltersAndRotatesWithinBounds()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canopy-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "canopy.log");
        try
        {
            var logger = new BoundedDiagnosticLogger(path, DiagnosticLogLevel.Error,
                maximumBytes: 64 * 1024, retainedFiles: 2);
            logger.Write(DiagnosticLogLevel.Information, "filtered");
            Assert.False(File.Exists(path));
            logger.Write(DiagnosticLogLevel.Error, "first");
            Assert.DoesNotContain("filtered", File.ReadAllText(path));
            string block = new('x', 20_000);
            for (int i = 0; i < 8; i++) logger.Write(DiagnosticLogLevel.Error, block);
            Assert.True(File.Exists(path + ".1"));
            Assert.True(new FileInfo(path).Length <= 64 * 1024);
            Assert.False(File.Exists(path + ".3"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    static void ThrowFromFixture() => throw new InvalidOperationException(@"private C:\Users\Alice\secret.txt");

    sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }
}
