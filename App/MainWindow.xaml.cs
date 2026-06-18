using System.Security.Principal;
using System.Windows;
using SizeMonitor.Interop;
using Wpf.Ui.Controls;

namespace SizeMonitor;

public partial class MainWindow : FluentWindow
{
    ScanSession?             _session;
    CancellationTokenSource? _cts;
    ScanResultManaged?       _result;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hide elevation badge if running elevated.
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        _elevBadge.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        _emptyState.Visibility = Visibility.Visible;
    }

    async void OnScan(object sender, RoutedEventArgs e)
    {
        string path = _pathBox.Text.Trim();
        if (string.IsNullOrEmpty(path)) return;

        _btnScan.IsEnabled     = false;
        _btnCancel.IsEnabled   = true;
        _emptyState.Visibility = Visibility.Collapsed;
        _statSize.Text         = "Scanning...";
        _statFiles.Text        = "";
        _statTime.Text         = "";
        _statTimeSep.Visibility = Visibility.Collapsed;

        _cts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(p =>
        {
            _statFiles.Text = $"{p.FilesVisited:N0} files, {p.DirsVisited:N0} dirs";
        });

        try
        {
            _session = ScanSession.Start(path, progress);
            _result  = await _session.WaitAsync(_cts.Token);
            OnScanComplete(_result);
        }
        catch (OperationCanceledException)
        {
            _statSize.Text         = "Cancelled";
            _statFiles.Text        = "";
            _statTime.Text         = "";
            _statTimeSep.Visibility = Visibility.Collapsed;
            _emptyState.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _statSize.Text         = $"Error: {ex.Message}";
            _statFiles.Text        = "";
            _statTime.Text         = "";
            _statTimeSep.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _btnScan.IsEnabled   = true;
            _btnCancel.IsEnabled = false;
            _session?.Dispose();
            _session = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    void OnCancel(object sender, RoutedEventArgs e)
    {
        _session?.Cancel();
        _cts?.Cancel();
    }

    void OnScanComplete(ScanResultManaged result)
    {
        _statSize.Text          = Helpers.SizeFormatter.FormatBytes(result.TotalBytes);
        _statFiles.Text         = $"{result.FileCount:N0} files, {result.DirCount:N0} dirs";
        _statTime.Text          = $"{result.ElapsedSec:F1}s";
        _statTimeSep.Visibility = Visibility.Visible;
        // Tree and treemap will be wired in Phase 7.
    }
}
