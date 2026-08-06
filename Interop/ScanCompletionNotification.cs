using System.Security;
using System.Reflection;
using System.Text;

namespace SizeMonitor.Interop;

public enum ScanCompletionOutcome
{
    Success,
    PartialSuccess,
    Failure,
}

public enum ScanNotificationDelivery
{
    Toast,
    SystemEvent,
    InProcessOnly,
}

public sealed record ScanCompletionSummary
{
    public required ScanCompletionOutcome Outcome { get; init; }
    public int TargetCount { get; init; }
    public int SuccessfulTargets { get; init; }
    public int FailedTargets { get; init; }
    public ulong FileCount { get; init; }
    public ulong DirectoryCount { get; init; }
    public ulong TotalBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<string> TargetPaths { get; init; } = [];
}

public sealed record ScanCompletionNotificationOptions
{
    public string SuccessTitle { get; init; } = "Canopy scan complete";
    public string PartialSuccessTitle { get; init; } = "Canopy scan completed with issues";
    public string FailureTitle { get; init; } = "Canopy scan failed";
    public string ActionLabel { get; init; } = "Open Canopy";
    public bool IncludeSensitivePaths { get; init; }
    public bool ThrowOnDeliveryFailure { get; init; }
}

public sealed record ScanCompletionNotificationPayload(
    string Title,
    string Message,
    string ActionLabel,
    ScanCompletionOutcome Outcome);

public sealed record ScanCompletionNotificationResult(
    ScanNotificationDelivery Delivery,
    ScanCompletionNotificationPayload Payload,
    Exception? DeliveryError = null);

public static class ScanCompletionNotificationBuilder
{
    public static ScanCompletionNotificationPayload Build(
        ScanCompletionSummary summary,
        ScanCompletionNotificationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        options ??= new ScanCompletionNotificationOptions();
        Validate(summary, options);

        string title = summary.Outcome switch
        {
            ScanCompletionOutcome.Success => options.SuccessTitle,
            ScanCompletionOutcome.PartialSuccess => options.PartialSuccessTitle,
            ScanCompletionOutcome.Failure => options.FailureTitle,
            _ => throw new ArgumentOutOfRangeException(nameof(summary)),
        };
        string targets = summary.TargetCount == 1 ? "1 target" : $"{summary.TargetCount:N0} targets";
        string message = summary.Outcome switch
        {
            ScanCompletionOutcome.Success =>
                $"{targets}: {summary.FileCount:N0} files, {summary.DirectoryCount:N0} folders, " +
                $"{summary.TotalBytes:N0} bytes in {FormatDuration(summary.Elapsed)}.",
            ScanCompletionOutcome.PartialSuccess =>
                $"{summary.SuccessfulTargets:N0} of {summary.TargetCount:N0} targets completed; " +
                $"{summary.FailedTargets:N0} failed. {summary.FileCount:N0} files scanned.",
            ScanCompletionOutcome.Failure =>
                $"The scan did not complete. {summary.FailedTargets:N0} of " +
                $"{summary.TargetCount:N0} targets failed.",
            _ => throw new ArgumentOutOfRangeException(nameof(summary)),
        };

        if (options.IncludeSensitivePaths && summary.TargetPaths.Count > 0)
            message += $" Targets: {string.Join(", ", summary.TargetPaths.Take(3))}" +
                       (summary.TargetPaths.Count > 3 ? ", …" : string.Empty);

        return new(title, message, options.ActionLabel, summary.Outcome);
    }

    static string FormatDuration(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{elapsed.TotalMinutes:F1} min"
        : $"{elapsed.TotalSeconds:F1} sec";

    static void Validate(
        ScanCompletionSummary summary,
        ScanCompletionNotificationOptions options)
    {
        if (summary.TargetCount < 0 || summary.SuccessfulTargets < 0 || summary.FailedTargets < 0)
            throw new ArgumentOutOfRangeException(nameof(summary), "Target counts cannot be negative.");
        if (summary.SuccessfulTargets + summary.FailedTargets > summary.TargetCount)
            throw new ArgumentException("Successful and failed target counts exceed the total.", nameof(summary));
        if (summary.Elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(summary), "Elapsed time cannot be negative.");
        if (string.IsNullOrWhiteSpace(options.SuccessTitle) ||
            string.IsNullOrWhiteSpace(options.PartialSuccessTitle) ||
            string.IsNullOrWhiteSpace(options.FailureTitle) ||
            string.IsNullOrWhiteSpace(options.ActionLabel))
            throw new ArgumentException("Notification titles and action label cannot be empty.", nameof(options));
    }
}

public interface IScanCompletionNotificationSink
{
    ValueTask<ScanNotificationDelivery> DeliverAsync(
        ScanCompletionNotificationPayload payload,
        CancellationToken cancellationToken);
}

public sealed class ScanCompletionNotificationService
{
    readonly IScanCompletionNotificationSink _sink;

    public ScanCompletionNotificationService(IScanCompletionNotificationSink? sink = null) =>
        _sink = sink ?? new WindowsScanCompletionNotificationSink();

    public event EventHandler<ScanCompletionNotificationPayload>? NotificationRaised;

    public async Task<ScanCompletionNotificationResult> NotifyAsync(
        ScanCompletionSummary summary,
        ScanCompletionNotificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ScanCompletionNotificationOptions();
        cancellationToken.ThrowIfCancellationRequested();
        ScanCompletionNotificationPayload payload =
            ScanCompletionNotificationBuilder.Build(summary, options);
        NotificationRaised?.Invoke(this, payload);
        try
        {
            ScanNotificationDelivery delivery =
                await _sink.DeliverAsync(payload, cancellationToken).ConfigureAwait(false);
            return new(delivery, payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!options.ThrowOnDeliveryFailure)
        {
            return new(ScanNotificationDelivery.InProcessOnly, payload, ex);
        }
    }
}

sealed class WindowsScanCompletionNotificationSink : IScanCompletionNotificationSink
{
    const string CompletionEventName = @"Local\Canopy.ScanCompleted";
    readonly EventWaitHandle? _completionEvent = CreateCompletionEvent();

    public ValueTask<ScanNotificationDelivery> DeliverAsync(
        ScanCompletionNotificationPayload payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryShowAppNotification(payload))
            return ValueTask.FromResult(ScanNotificationDelivery.Toast);
        if (_completionEvent is not null)
        {
            _completionEvent.Set();
            return ValueTask.FromResult(ScanNotificationDelivery.SystemEvent);
        }
        return ValueTask.FromResult(ScanNotificationDelivery.InProcessOnly);
    }

    static bool TryShowAppNotification(ScanCompletionNotificationPayload payload)
    {
        try
        {
            Type? managerType = Type.GetType(
                "Microsoft.Windows.AppNotifications.AppNotificationManager, Microsoft.WindowsAppRuntime");
            Type? notificationType = Type.GetType(
                "Microsoft.Windows.AppNotifications.AppNotification, Microsoft.WindowsAppRuntime");
            if (managerType is null || notificationType is null) return false;
            object? manager = managerType.GetProperty("Default")?.GetValue(null);
            string xml = "<toast><visual><binding template=\"ToastGeneric\"><text>" +
                EscapeXml(payload.Title) + "</text><text>" + EscapeXml(payload.Message) +
                "</text></binding></visual></toast>";
            object? notification = Activator.CreateInstance(notificationType, xml);
            if (manager is null || notification is null) return false;
            managerType.GetMethod("Show")?.Invoke(manager, [notification]);
            return true;
        }
        catch (Exception ex) when (ex is TypeLoadException or MissingMethodException or
                                      TargetInvocationException or InvalidOperationException or SecurityException)
        {
            return false;
        }
    }

    static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    static EventWaitHandle? CreateCompletionEvent()
    {
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, CompletionEventName); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or
                                      PlatformNotSupportedException or SecurityException)
        {
            return null;
        }
    }
}
