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

    public static async Task<ScanSnapshotSaveResult> SaveAsync(
        ScanResultManaged result,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

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
        await ScanSnapshotStore.SaveAsync(dialog.FileName, result, cancellationToken);
        return new ScanSnapshotSaveResult.Saved(dialog.FileName);
    }

    public static async Task<ScanSnapshotOpenResult> OpenAsync(
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
        ScanResultManaged result = await ScanSnapshotStore.LoadAsync(dialog.FileName, cancellationToken);
        return new ScanSnapshotOpenResult.Loaded(dialog.FileName, result);
    }
}
