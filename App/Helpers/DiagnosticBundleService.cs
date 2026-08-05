using Microsoft.Win32;
using SizeMonitor.Interop;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace SizeMonitor.Helpers;

public enum DiagnosticBundleSaveStatus
{
    Saved,
    Cancelled,
}

public sealed record DiagnosticBundleSaveResult(
    DiagnosticBundleSaveStatus Status,
    string? Path = null);

/// <summary>Collects explicit application diagnostics and coordinates their user-selected export.</summary>
public sealed class DiagnosticBundleService
{
    const string TruncatedLogMarker = "[Earlier log content omitted to fit diagnostic bundle size limit.]\n";
    static readonly UTF8Encoding Utf8 = new(false);

    public async Task<DiagnosticBundleSaveResult> SaveAsync(
        Window? owner = null,
        bool includeSensitivePaths = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new SaveFileDialog
        {
            Title = "Save diagnostic bundle",
            Filter = "ZIP archives (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = "canopy-diagnostics.zip",
        };

        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true)
            return new(DiagnosticBundleSaveStatus.Cancelled);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string logs = await ReadCurrentLogAsync(cancellationToken).ConfigureAwait(false);
            var input = new DiagnosticBundleInput
            {
                ApplicationVersion = GetApplicationVersion(),
                OperatingSystem = RuntimeInformation.OSDescription,
                RuntimeVersion = RuntimeInformation.FrameworkDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                LogText = logs,
                IncludeSensitivePaths = includeSensitivePaths,
            };

            string destination = Path.GetFullPath(dialog.FileName);
            await DiagnosticBundle.CreateFileAsync(input, destination, cancellationToken).ConfigureAwait(false);
            return new(DiagnosticBundleSaveStatus.Saved, destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(DiagnosticBundleSaveStatus.Cancelled);
        }
    }

    static string GetApplicationVersion() =>
        (Assembly.GetEntryAssembly() ?? typeof(DiagnosticBundleService).Assembly)
            .GetName().Version?.ToString() ?? "unknown";

    static async Task<string> ReadCurrentLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                Logger.LogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            int markerBytes = Utf8.GetByteCount(TruncatedLogMarker);
            int byteLimit = DiagnosticBundle.MaxLogBytes - markerBytes;
            bool truncated = stream.Length > byteLimit;
            if (truncated)
                stream.Seek(-byteLimit, SeekOrigin.End);

            using var buffer = new MemoryStream(capacity: (int)Math.Min(stream.Length, byteLimit));
            byte[] chunk = new byte[64 * 1024];
            int remaining = byteLimit;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(
                    chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
            string log = Utf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            if (!truncated)
                return log;

            // A tail read can begin in the middle of a UTF-8 sequence or line; omit that fragment.
            int firstNewline = log.IndexOf('\n');
            if (firstNewline >= 0)
                log = log[(firstNewline + 1)..];
            return TruncatedLogMarker + log;
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }
}
