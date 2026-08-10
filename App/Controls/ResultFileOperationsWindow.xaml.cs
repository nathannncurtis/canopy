using System.Windows;
using System.Windows.Controls;
using System.IO;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class ResultFileOperationsWindow : Window
{
    readonly string[] _sources;
    readonly ResultFileOperationService _service = new(new WindowsResultFileOperationBackend());
    ResultFileOperationPreview? _current;
    CancellationTokenSource? _cancellation;
    public ResultFileOperationsWindow(ResultSelectionSummary selection)
    {
        InitializeComponent(); _sources = selection.Items.Select(item => item.Path).ToArray(); RefreshPreview();
    }
    ResultFileOperationKind Kind => (ResultFileOperationKind)Math.Max(0, _kind.SelectedIndex);
    void OnInputChanged(object sender, EventArgs e)
    {
        if (!IsLoaded) return;
        _destinationMode.Text = Kind switch { ResultFileOperationKind.Rename => "New sibling name or path",
            ResultFileOperationKind.Move => "Existing destination folder", ResultFileOperationKind.CreateZip => "New ZIP file", _ => "Destination not used" };
        _destination.IsEnabled = _browse.IsEnabled = Kind is ResultFileOperationKind.Rename or ResultFileOperationKind.Move or ResultFileOperationKind.CreateZip;
        RefreshPreview();
    }
    void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Kind == ResultFileOperationKind.Move)
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose destination folder" };
            if (picker.ShowDialog(this) == true) _destination.Text = picker.FolderName;
        }
        else if (Kind == ResultFileOperationKind.CreateZip)
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Title = "Choose ZIP archive", Filter = "ZIP archive (*.zip)|*.zip", AddExtension = true, DefaultExt = ".zip" };
            if (picker.ShowDialog(this) == true) _destination.Text = picker.FileName;
        }
        else if (Kind == ResultFileOperationKind.Rename && _sources.Length == 1)
            _destination.Text = _sources[0];
    }
    void OnPreview(object sender, RoutedEventArgs e) => RefreshPreview();
    void RefreshPreview()
    {
        _confirmed.IsChecked = false;
        try
        {
            _current = _service.Preview(new(Kind, _sources, _destination.Text));
            _preview.ItemsSource = _current.Items; _status.Text = _current.CanExecute
                ? $"Review {_current.Items.Count:N0} target(s), then confirm." : "Resolve every validation error before executing.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { _current = null; _preview.ItemsSource = null; _status.Text = ex.Message; }
        UpdateExecute();
    }
    void OnConfirmChanged(object sender, RoutedEventArgs e) => UpdateExecute();
    void UpdateExecute() => _execute.IsEnabled = _cancellation is null && _current?.CanExecute == true && _confirmed.IsChecked == true;
    async void OnExecute(object sender, RoutedEventArgs e)
    {
        ResultFileOperationPreview? preview = _current; if (preview is null || _confirmed.IsChecked != true) return;
        _cancellation = new(); _cancel.IsEnabled = true; UpdateExecute();
        try
        {
            ResultFileOperationReport report = await _service.ExecuteAsync(preview, preview.ConfirmationId, _cancellation.Token);
            _status.Text = $"{report.Succeeded:N0} succeeded, {report.Failed:N0} failed, {report.Cancelled:N0} cancelled." +
                (report.Outcomes.FirstOrDefault(item => item.Error is not null)?.Error is string error ? $" First error: {error}" : string.Empty);
            _preview.ItemsSource = report.Outcomes;
        }
        finally { _cancellation.Dispose(); _cancellation = null; _cancel.IsEnabled = false; _confirmed.IsChecked = false; UpdateExecute(); }
    }
    void OnCancel(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
}
