using System.Net.Http;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class ReportDeliveryView : UserControl
{
    static readonly HttpClient SharedHttpClient = new();
    readonly ReportDeliveryService _delivery;
    ReportDeliveryContent? _content;
    CancellationTokenSource? _cancellation;

    public ReportDeliveryView() : this(new ReportDeliveryService(SharedHttpClient)) { }

    public ReportDeliveryView(ReportDeliveryService delivery)
    {
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        InitializeComponent();
        Unloaded += (_, _) => Cancel();
    }

    public event Action<ReportDeliveryResult>? DeliveryCompleted;

    public void SetReport(ReportDeliveryContent? content)
    {
        Cancel();
        _content = content;
        _consent.IsChecked = false;
        UpdateSendEnabled();
        _status.Text = content is null ? "Generate a report before choosing delivery." : "Report ready to send.";
    }

    void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_webhookPanel is null) return;
        bool smtp = _mode.SelectedIndex == 1;
        _webhookPanel.Visibility = smtp ? Visibility.Collapsed : Visibility.Visible;
        _smtpPanel.Visibility = smtp ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = _content is null ? "Generate a report before choosing delivery." : "Report ready to send.";
    }

    async void OnSend(object sender, RoutedEventArgs e)
    {
        ReportDeliveryContent? content = _content;
        if (content is null || _cancellation is not null || _consent.IsChecked != true) return;
        content = content with { NetworkTransferConsent = true };
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetRunning(true);
        _status.Text = "Sending report securely...";
        try
        {
            ReportDeliveryResult result = _mode.SelectedIndex == 1
                ? await SendEmailAsync(content, cancellation.Token)
                : await SendWebhookAsync(content, cancellation.Token);
            _status.Text = result.Status == ReportDeliveryStatus.Delivered
                ? "Report delivered successfully."
                : $"Delivery was rejected by the remote service (status {result.ProtocolStatus?.ToString() ?? "unknown"}).";
            DeliveryCompleted?.Invoke(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "Report delivery cancelled.";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            _status.Text = "Delivery settings are invalid. Check the highlighted method's fields and try again.";
        }
        catch (Exception)
        {
            _status.Text = "Report delivery failed. Verify connectivity and server settings, then try again.";
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            _password.Clear();
            SetRunning(false);
            RaiseStatusChanged();
        }
    }

    Task<ReportDeliveryResult> SendWebhookAsync(ReportDeliveryContent content, CancellationToken token)
    {
        if (!Uri.TryCreate(_webhook.Text.Trim(), UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("An HTTPS endpoint is required.");
        return _delivery.SendWebhookAsync(endpoint, content, cancellationToken: token);
    }

    Task<ReportDeliveryResult> SendEmailAsync(ReportDeliveryContent content, CancellationToken token)
    {
        if (!int.TryParse(_port.Text.Trim(), out int port)) throw new FormatException("SMTP port is invalid.");
        string user = _user.Text.Trim();
        string password = _password.Password;
        if ((user.Length == 0) != (password.Length == 0))
            throw new ArgumentException("SMTP credentials must be supplied together.");
        var options = new SmtpDeliveryOptions
        {
            Host = _host.Text.Trim(), Port = port, From = _from.Text.Trim(),
            To = _to.Text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UserName = user.Length == 0 ? null : user,
            Password = user.Length == 0 ? null : password,
            RequireTls = true,
        };
        return _delivery.SendEmailAsync(options, content, cancellationToken: token);
    }

    void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    void OnConsentChanged(object sender, RoutedEventArgs e) => UpdateSendEnabled();

    public void Cancel() => _cancellation?.Cancel();

    void SetRunning(bool running)
    {
        _send.IsEnabled = !running && _content is not null && _consent.IsChecked == true;
        _cancel.IsEnabled = running;
        _mode.IsEnabled = !running;
        _webhookPanel.IsEnabled = !running;
        _smtpPanel.IsEnabled = !running;
        _consent.IsEnabled = !running;
        _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateSendEnabled() =>
        _send.IsEnabled = _cancellation is null && _content is not null && _consent.IsChecked == true;

    void RaiseStatusChanged()
    {
        AutomationPeer? peer = UIElementAutomationPeer.FromElement(_status)
            ?? UIElementAutomationPeer.CreatePeerForElement(_status);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
