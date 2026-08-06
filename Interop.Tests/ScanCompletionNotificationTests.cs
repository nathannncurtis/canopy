using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanCompletionNotificationTests
{
    [Theory]
    [InlineData(ScanCompletionOutcome.Success, "Canopy scan complete")]
    [InlineData(ScanCompletionOutcome.PartialSuccess, "Canopy scan completed with issues")]
    [InlineData(ScanCompletionOutcome.Failure, "Canopy scan failed")]
    public void BuildsOutcomeSpecificSummaries(ScanCompletionOutcome outcome, string title)
    {
        ScanCompletionNotificationPayload payload = ScanCompletionNotificationBuilder.Build(
            Summary(outcome));

        Assert.Equal(title, payload.Title);
        Assert.NotEmpty(payload.Message);
        Assert.Equal("Open Canopy", payload.ActionLabel);
    }

    [Fact]
    public void OmitsSensitivePathsByDefaultAndIncludesOnlyByOptIn()
    {
        ScanCompletionSummary summary = Summary(ScanCompletionOutcome.Success) with
        {
            TargetPaths = [@"C:\Users\Alice\Private"],
        };

        Assert.DoesNotContain("Alice", ScanCompletionNotificationBuilder.Build(summary).Message);
        Assert.Contains("Alice", ScanCompletionNotificationBuilder.Build(summary,
            new ScanCompletionNotificationOptions { IncludeSensitivePaths = true }).Message);
    }

    [Fact]
    public async Task ServiceRaisesEventAndReturnsInjectedDelivery()
    {
        var sink = new RecordingSink();
        var service = new ScanCompletionNotificationService(sink);
        ScanCompletionNotificationPayload? raised = null;
        service.NotificationRaised += (_, payload) => raised = payload;

        ScanCompletionNotificationResult result = await service.NotifyAsync(
            Summary(ScanCompletionOutcome.Success), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ScanNotificationDelivery.SystemEvent, result.Delivery);
        Assert.Equal(result.Payload, raised);
        Assert.Equal(result.Payload, sink.Payload);
    }

    [Fact]
    public async Task CancellationPreventsEventAndDelivery()
    {
        var sink = new RecordingSink();
        var service = new ScanCompletionNotificationService(sink);
        bool raised = false;
        service.NotificationRaised += (_, _) => raised = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.NotifyAsync(
            Summary(ScanCompletionOutcome.Success), cancellationToken: cancellation.Token));

        Assert.False(raised);
        Assert.Null(sink.Payload);
    }

    [Fact]
    public async Task DeliveryErrorsAreReportedOrThrownByPolicy()
    {
        var service = new ScanCompletionNotificationService(new FailingSink());

        ScanCompletionNotificationResult result = await service.NotifyAsync(
            Summary(ScanCompletionOutcome.Failure), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ScanNotificationDelivery.InProcessOnly, result.Delivery);
        Assert.IsType<IOException>(result.DeliveryError);

        await Assert.ThrowsAsync<IOException>(() => service.NotifyAsync(
            Summary(ScanCompletionOutcome.Failure),
            new ScanCompletionNotificationOptions { ThrowOnDeliveryFailure = true },
            TestContext.Current.CancellationToken));
    }

    static ScanCompletionSummary Summary(ScanCompletionOutcome outcome) => new()
    {
        Outcome = outcome,
        TargetCount = 2,
        SuccessfulTargets = outcome == ScanCompletionOutcome.Failure ? 0 : 1,
        FailedTargets = outcome == ScanCompletionOutcome.Success ? 0 : 1,
        FileCount = 25,
        DirectoryCount = 4,
        TotalBytes = 1_024,
        Elapsed = TimeSpan.FromSeconds(2),
    };

    sealed class RecordingSink : IScanCompletionNotificationSink
    {
        public ScanCompletionNotificationPayload? Payload { get; private set; }
        public ValueTask<ScanNotificationDelivery> DeliverAsync(
            ScanCompletionNotificationPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Payload = payload;
            return ValueTask.FromResult(ScanNotificationDelivery.SystemEvent);
        }
    }

    sealed class FailingSink : IScanCompletionNotificationSink
    {
        public ValueTask<ScanNotificationDelivery> DeliverAsync(
            ScanCompletionNotificationPayload payload, CancellationToken cancellationToken) =>
            throw new IOException("delivery failed");
    }
}
