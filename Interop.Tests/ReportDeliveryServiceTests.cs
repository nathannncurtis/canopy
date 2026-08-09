using System.Net;
using System.Text;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ReportDeliveryServiceTests
{
    [Fact]
    public async Task WebhookRequiresHttpsAndSendsIdempotentBoundedPayload()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var service = new ReportDeliveryService(new HttpClient(handler));
        ReportDeliveryContent content = Content();

        ReportDeliveryResult result = await service.SendWebhookAsync(
            new Uri("https://example.test/report"), content,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReportDeliveryStatus.Delivered, result.Status);
        Assert.Equal("run-123", handler.IdempotencyKey);
        Assert.Equal("hello", Encoding.UTF8.GetString(handler.Body!));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendWebhookAsync(
            new Uri("http://example.test/report"), content,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WebhookRejectionDoesNotExposeResponseBody()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, "secret response");
        var service = new ReportDeliveryService(new HttpClient(handler));

        ReportDeliveryResult result = await service.SendWebhookAsync(
            new Uri("https://example.test/report"), Content(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReportDeliveryStatus.Rejected, result.Status);
        Assert.Equal(400, result.ProtocolStatus);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Fact]
    public async Task EmailUsesInjectedTransportAndPrivacySafePayload()
    {
        var transport = new RecordingTransport();
        var service = new ReportDeliveryService(new HttpClient(new RecordingHandler(HttpStatusCode.OK)), transport);
        var options = new SmtpDeliveryOptions
        {
            Host = "smtp.example.test", From = "sender@example.test", To = ["team@example.test"],
        };

        ReportDeliveryResult result = await service.SendEmailAsync(
            options, Content(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReportDeliveryStatus.Delivered, result.Status);
        Assert.Equal("run-123", transport.Payload!.IdempotencyKey);
        Assert.DoesNotContain("C:\\", transport.Payload.Body);
        Assert.Equal("hello", Encoding.UTF8.GetString(transport.Payload.Attachment.Span));
    }

    [Fact]
    public async Task RejectsOversizeCredentialsInUriAndHeaderInjection()
    {
        var service = new ReportDeliveryService(new HttpClient(new RecordingHandler(HttpStatusCode.OK)));
        Assert.Throws<ArgumentException>(() => ReportDeliveryService.BuildEmailPayload(
            new SmtpDeliveryOptions { Host = "smtp.example.test", From = "a@example.test", To = ["b@example.test"], Subject = "bad\r\nBcc:x" }, Content()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendWebhookAsync(
            new Uri("https://user:pass@example.test"), Content(),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ReportDeliveryService.BuildEmailPayload(
            new SmtpDeliveryOptions { Host = "smtp.example.test", From = "a@example.test", To = ["b@example.test"] },
            Content() with { Bytes = new byte[ReportDeliveryService.MaximumReportBytes + 1] }));
    }

    [Fact]
    public async Task NetworkDeliveryRequiresExplicitConsent()
    {
        var service = new ReportDeliveryService(new HttpClient(new RecordingHandler(HttpStatusCode.OK)));
        ReportDeliveryContent content = Content() with { NetworkTransferConsent = false };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendWebhookAsync(new Uri("https://example.test/report"), content,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Explicit consent", error.Message);
    }

    static ReportDeliveryContent Content() => new()
    {
        Bytes = Encoding.UTF8.GetBytes("hello"),
        IdempotencyKey = "run-123",
        NetworkTransferConsent = true,
    };

    sealed class RecordingHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public string? IdempotencyKey { get; private set; }
        public byte[]? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsByteArrayAsync(token);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    sealed class RecordingTransport : IReportEmailTransport
    {
        public ReportEmailPayload? Payload { get; private set; }
        public Task SendAsync(SmtpDeliveryOptions options, ReportEmailPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Payload = payload; return Task.CompletedTask;
        }
    }
}
