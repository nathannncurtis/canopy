using System.Windows.Controls;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class PhysicalStorageSummaryView : UserControl
{
    public PhysicalStorageSummaryView() => InitializeComponent();

    public void SetAccounting(PhysicalStorageAccounting? value)
    {
        _logical.Text = Bytes(value?.LogicalBytes);
        _allocated.Text = Bytes(value?.AllocatedBytes);
        _savings.Text = value is null ? "Unavailable"
            : $"{Bytes(value.CompressionSavingsBytes)} ({value.CompressionSavingsPercent:0.0}%)";
        _sparse.Text = Bytes(value?.SparseSavingsBytes);
        _overhead.Text = Bytes(value?.AllocationOverheadBytes);
        _unaccounted.Text = Bytes(value?.UnaccountedUsedBytes) +
            (value?.IsPartial == true ? " (scan incomplete; includes skipped content)" : string.Empty);
        _reserved.Text = value is null ? "Unavailable"
            : $"Reserved: {Bytes(value.ReservedBytes)}; pool unavailable: {Bytes(value.SystemManagedUnavailableBytes)}";
    }

    static string Bytes(ulong? value) => value is ulong bytes
        ? Helpers.SizeFormatter.FormatBytes(bytes) : "Unavailable";
}
