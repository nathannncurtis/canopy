using System.Windows;
using Microsoft.Win32;
using SizeMonitor.Interop;

namespace SizeMonitor.Helpers;

public abstract record ScanSnapshotSaveResult
{
    private ScanSnapshotSaveResult() { }

    public sealed record Saved(string Path) : ScanSnapshotSaveResult;
    public sealed record Cancelled : ScanSnapshotSaveResult;
}

public abstract record ScanSnapshotOpenResult
{
    private ScanSnapshotOpenResult() { }

    public sealed record Loaded(string Path, ScanResultManaged Result) : ScanSnapshotOpenResult;
    public sealed record Cancelled : ScanSnapshotOpenResult;
}

/// <summary>Shows scan snapshot dialogs and delegates persistence to the Interop store.</summary>
public static class ScanSnapshotService
{
    const string SnapshotFilter = "Canopy scan snapshots (*.canopy)|*.canopy|All files (*.*)|*.*";
    static int _operationInFlight;

    public static async Task<ScanSnapshotSaveResult> SaveAsync(
        ScanResultManaged result,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        EnterOperation();
        try
        {
            return await SaveCoreAsync(result, owner, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _operationInFlight, 0);
        }
    }

    static async Task<ScanSnapshotSaveResult> SaveCoreAsync(
        ScanResultManaged result,
        Window? owner,
        CancellationToken cancellationToken)
    {

        var dialog = new SaveFileDialog
        {
            Title = "Save scan snapshot",
            Filter = SnapshotFilter,
            DefaultExt = ".canopy",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = "scan.canopy",
        };

        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true) return new ScanSnapshotSaveResult.Cancelled();

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
            () => ScanSnapshotStore.SaveAsync(dialog.FileName, result, cancellationToken),
            cancellationToken);
        return new ScanSnapshotSaveResult.Saved(dialog.FileName);
    }

    public static async Task<ScanSnapshotOpenResult> OpenAsync(
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterOperation();
        try
        {
            return await OpenCoreAsync(owner, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _operationInFlight, 0);
        }
    }

    static async Task<ScanSnapshotOpenResult> OpenCoreAsync(
        Window? owner,
        CancellationToken cancellationToken)
    {

        var dialog = new OpenFileDialog
        {
            Title = "Open scan snapshot",
            Filter = SnapshotFilter,
            DefaultExt = ".canopy",
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
        };

        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true) return new ScanSnapshotOpenResult.Cancelled();

        cancellationToken.ThrowIfCancellationRequested();
        ScanResultManaged result = await Task.Run(
            () => ScanSnapshotStore.LoadAsync(dialog.FileName, cancellationToken),
            cancellationToken);
        return new ScanSnapshotOpenResult.Loaded(dialog.FileName, result);
    }

    static void EnterOperation()
    {
        if (Interlocked.CompareExchange(ref _operationInFlight, 1, 0) != 0)
            throw new InvalidOperationException("A scan snapshot operation is already in progress.");
    }
}
