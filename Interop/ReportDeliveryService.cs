using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;

namespace SizeMonitor.Interop;

public enum ReportDeliveryStatus { Delivered, Rejected }

public sealed record ReportDeliveryResult(
    ReportDeliveryStatus Status,
    int? ProtocolStatus = null,
    string? Message = null);

public sealed record ReportDeliveryContent
{
    public required ReadOnlyMemory<byte> Bytes { get; init; }
    public string MediaType { get; init; } = "text/plain";
    public string FileName { get; init; } = "canopy-report.txt";
    public required string IdempotencyKey { get; init; }
    public bool NetworkTransferConsent { get; init; }
}

public sealed record SmtpDeliveryOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 587;
    public bool RequireTls { get; init; } = true;
    public required string From { get; init; }
    public required IReadOnlyList<string> To { get; init; }
    public string Subject { get; init; } = "Canopy scan report";
    public string? UserName { get; init; }
    public string? Password { get; init; }
}

public sealed record ReportEmailPayload(
    string From,
    IReadOnlyList<string> To,
    string Subject,
    string Body,
    string AttachmentName,
    string AttachmentMediaType,
    ReadOnlyMemory<byte> Attachment,
    string IdempotencyKey);

public interface IReportEmailTransport
{
    Task SendAsync(SmtpDeliveryOptions options, ReportEmailPayload payload, CancellationToken cancellationToken);
}

public sealed class ReportDeliveryService
{
    public const int MaximumReportBytes = 10 * 1024 * 1024;
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    readonly HttpClient _httpClient;
    readonly IReportEmailTransport _emailTransport;

    public ReportDeliveryService(HttpClient httpClient, IReportEmailTransport? emailTransport = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _emailTransport = emailTransport ?? new FrameworkSmtpTransport();
    }

    public async Task<ReportDeliveryResult> SendWebhookAsync(
        Uri endpoint,
        ReportDeliveryContent content,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContent(content);
        ValidateTimeout(timeout);
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(endpoint.Host) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Webhook endpoint must be an absolute HTTPS URI without embedded credentials.", nameof(endpoint));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", content.IdempotencyKey);
        request.Headers.TryAddWithoutValidation("X-Canopy-Report", "1");
        request.Content = new ByteArrayContent(content.Bytes.ToArray());
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(content.MediaType);
        request.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = content.FileName,
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
        int code = (int)response.StatusCode;
        return response.IsSuccessStatusCode
            ? new(ReportDeliveryStatus.Delivered, code, "Webhook accepted the report.")
            : new(ReportDeliveryStatus.Rejected, code, "Webhook rejected the report.");
    }

    public async Task<ReportDeliveryResult> SendEmailAsync(
        SmtpDeliveryOptions options,
        ReportDeliveryContent content,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContent(content);
        ValidateSmtp(options);
        ValidateTimeout(timeout);
        ReportEmailPayload payload = BuildEmailPayload(options, content);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);
        await _emailTransport.SendAsync(options, payload, timeoutSource.Token).ConfigureAwait(false);
        return new(ReportDeliveryStatus.Delivered, Message: "SMTP server accepted the report.");
    }

    public static ReportEmailPayload BuildEmailPayload(
        SmtpDeliveryOptions options,
        ReportDeliveryContent content)
    {
        ValidateContent(content);
        ValidateSmtp(options);
        return new(options.From, options.To.ToArray(), CleanHeader(options.Subject, nameof(options.Subject)),
            "A Canopy scan report is attached. Paths and other sensitive details are included only if the report creator explicitly enabled them.",
            content.FileName, content.MediaType, content.Bytes, content.IdempotencyKey);
    }

    static void ValidateContent(ReportDeliveryContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.NetworkTransferConsent)
            throw new InvalidOperationException(
                "Explicit consent is required before a report or its filename can be sent over the network.");
        if (content.Bytes.Length == 0 || content.Bytes.Length > MaximumReportBytes)
            throw new ArgumentException($"Report must contain 1 to {MaximumReportBytes} bytes.", nameof(content));
        if (string.IsNullOrWhiteSpace(content.IdempotencyKey) || content.IdempotencyKey.Length > 128 ||
            content.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("Idempotency key must be 1-128 printable characters.", nameof(content));
        _ = MediaTypeHeaderValue.Parse(content.MediaType);
        if (string.IsNullOrWhiteSpace(content.FileName) || content.FileName.Length > 128 ||
            content.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Report filename is invalid.", nameof(content));
    }

    static void ValidateSmtp(SmtpDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host) || Uri.CheckHostName(options.Host) == UriHostNameType.Unknown)
            throw new ArgumentException("SMTP host is invalid.", nameof(options));
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options));
        if (!options.RequireTls) throw new ArgumentException("SMTP delivery requires TLS.", nameof(options));
        _ = new MailAddress(options.From);
        if (options.To is null || options.To.Count == 0 || options.To.Count > 50)
            throw new ArgumentException("SMTP delivery requires 1-50 recipients.", nameof(options));
        foreach (string address in options.To) _ = new MailAddress(address);
        if ((options.UserName is null) != (options.Password is null))
            throw new ArgumentException("SMTP username and password must be supplied together.", nameof(options));
        _ = CleanHeader(options.Subject, nameof(options.Subject));
    }

    static string CleanHeader(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("Mail header is invalid.", name);
        return value;
    }

    static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value && (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromMinutes(5)))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Delivery timeout must be 1 second to 5 minutes.");
    }

    sealed class FrameworkSmtpTransport : IReportEmailTransport
    {
        public async Task SendAsync(SmtpDeliveryOptions options, ReportEmailPayload payload, CancellationToken token)
        {
            using var client = new SmtpClient(options.Host, options.Port) { EnableSsl = true };
            if (options.UserName is not null)
                client.Credentials = new NetworkCredential(options.UserName, options.Password);
            using var message = new MailMessage { From = new MailAddress(payload.From), Subject = payload.Subject, Body = payload.Body };
            foreach (string recipient in payload.To) message.To.Add(recipient);
            message.Headers.Add("X-Canopy-Idempotency-Key", payload.IdempotencyKey);
            var stream = new MemoryStream(payload.Attachment.ToArray(), writable: false);
            message.Attachments.Add(new Attachment(stream, payload.AttachmentName, payload.AttachmentMediaType));
            await client.SendMailAsync(message, token).ConfigureAwait(false);
        }
    }
}
