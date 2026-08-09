using Microsoft.Win32;
using SizeMonitor.Interop;
using System.IO;
using System.Windows;

namespace SizeMonitor.Helpers;

public enum ScanResultExportStatus
{
    Saved,
    Cancelled,
}

public enum ScanResultExportFormat
{
    Csv,
    Json,
    Xml,
    Html,
}

public sealed record ScanResultExportResult(
    ScanResultExportStatus Status,
    string? Path = null,
    ScanResultExportFormat? Format = null);

/// <summary>Coordinates safe, user-selected scan result exports for the WPF UI.</summary>
public sealed class ScanResultExportService
{
    public async Task<ScanResultExportResult> ExportAsync(
        ScanResultManaged result,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (cancellationToken.IsCancellationRequested)
            return new(ScanResultExportStatus.Cancelled);

        var dialog = new SaveFileDialog
        {
            Title = "Export scan results",
            Filter = "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json|" +
                     "XML files (*.xml)|*.xml|HTML files (*.html)|*.html",
            FilterIndex = 1,
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = "canopy-scan.csv",
        };

        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true)
            return new(ScanResultExportStatus.Cancelled);

        if (cancellationToken.IsCancellationRequested)
            return new(ScanResultExportStatus.Cancelled);
        string destinationPath = Path.GetFullPath(dialog.FileName);
        if (IsNetworkDestination(destinationPath))
        {
            MessageBoxResult consent = owner is null
                ? MessageBox.Show(
                    "This export contains filenames and paths and will be written to a network location. Continue?",
                    "Consent required for network export", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(owner,
                    "This export contains filenames and paths and will be written to a network location. Continue?",
                    "Consent required for network export", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (consent != MessageBoxResult.Yes)
                return new(ScanResultExportStatus.Cancelled);
        }
        ScanResultExportFormat format = SelectFormat(destinationPath, dialog.FilterIndex);
        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The selected export path has no parent directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        return await Task.Run(() => WriteAndPublishAsync(
            result, destinationPath, temporaryPath, format, cancellationToken)).ConfigureAwait(false);
    }

    static async Task<ScanResultExportResult> WriteAndPublishAsync(
        ScanResultManaged result,
        string destinationPath,
        string temporaryPath,
        ScanResultExportFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                switch (format)
                {
                    case ScanResultExportFormat.Json:
                        await ScanResultExporter.ExportJsonAsync(result, stream, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case ScanResultExportFormat.Xml:
                        await ScanReportExporter.ExportXmlAsync(result, stream, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case ScanResultExportFormat.Html:
                        await ScanReportExporter.ExportHtmlAsync(result, stream, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    default:
                        await ScanResultExporter.ExportCsvAsync(result, stream, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new(ScanResultExportStatus.Saved, destinationPath, format);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ScanResultExportStatus.Cancelled);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Preserve the original export outcome if another process briefly owns the temp file.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed best-effort cleanup must not hide the actionable export exception.
            }
        }
    }

    static ScanResultExportFormat SelectFormat(string path, int filterIndex)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return ScanResultExportFormat.Json;
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return ScanResultExportFormat.Csv;
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return ScanResultExportFormat.Xml;
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            return ScanResultExportFormat.Html;
        return filterIndex switch
        {
            2 => ScanResultExportFormat.Json,
            3 => ScanResultExportFormat.Xml,
            4 => ScanResultExportFormat.Html,
            _ => ScanResultExportFormat.Csv,
        };
    }

    static bool IsNetworkDestination(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        try { return new DriveInfo(root).DriveType == DriveType.Network; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
