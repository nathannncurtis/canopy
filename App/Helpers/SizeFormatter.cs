namespace SizeMonitor.Helpers;

public static class SizeFormatter
{
    public static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_000_000_000_000UL => $"{bytes / 1_099_511_627_776.0:F2} TB",
        >= 1_000_000_000UL     => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_000_000UL         => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_000UL             => $"{bytes / 1_024.0:F1} KB",
        _                      => $"{bytes} B",
    };

    // Returns a short version for treemap labels.
    public static string FormatBytesShort(ulong bytes) => bytes switch
    {
        >= 1_000_000_000_000UL => $"{bytes / 1_099_511_627_776.0:F1}T",
        >= 1_000_000_000UL     => $"{bytes / 1_073_741_824.0:F1}G",
        >= 1_000_000UL         => $"{bytes / 1_048_576.0:F0}M",
        >= 1_000UL             => $"{bytes / 1_024.0:F0}K",
        _                      => $"{bytes}",
    };
}
