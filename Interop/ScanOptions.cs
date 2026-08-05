using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

[Flags]
internal enum SmonScanOptionFlags : uint
{
    ExcludeHidden = 0x00000001,
    ExcludeSystem = 0x00000002,
    ExcludeTemporary = 0x00000004,
    ExcludeReparsePoints = 0x00000008,
    ForceDirectoryScanner = 0x00000010,
}

[StructLayout(LayoutKind.Sequential)]
internal struct SmonScanOptionsNative
{
    public uint StructSize;
    public SmonScanOptionFlags Flags;
    public uint MaxDepth;
    public uint WorkerThreads;
    public ulong MinimumFileSize;
    public ulong MaximumFileSize;
    public IntPtr ExcludedPatterns;
    public IntPtr ExcludedExtensions;
}

public sealed record ScanOptions
{
    public uint? MaximumDepth { get; init; }
    public uint? WorkerThreads { get; init; }
    public ulong MinimumFileSize { get; init; }
    public ulong? MaximumFileSize { get; init; }
    public bool IncludeHidden { get; init; } = true;
    public bool IncludeSystem { get; init; } = true;
    public bool IncludeTemporary { get; init; } = true;
    public bool IncludeReparsePoints { get; init; } = true;
    public bool ForceDirectoryScanner { get; init; }
    public IReadOnlyList<string> ExcludedPatterns { get; init; } = [];
    public IReadOnlyList<string> ExcludedExtensions { get; init; } = [];

    internal void Validate()
    {
        if (MaximumFileSize is { } maximum && MinimumFileSize > maximum)
            throw new ArgumentException("MinimumFileSize cannot exceed MaximumFileSize.");
        if (WorkerThreads is > 1024)
            throw new ArgumentOutOfRangeException(nameof(WorkerThreads),
                "WorkerThreads cannot exceed 1024.");
        ValidateList(ExcludedPatterns, nameof(ExcludedPatterns));
        ValidateList(ExcludedExtensions, nameof(ExcludedExtensions));
    }

    internal string? BuildExcludedPatternList() => Join(ExcludedPatterns);
    internal string? BuildExcludedExtensionList() => Join(ExcludedExtensions);

    internal SmonScanOptionsNative ToNative(IntPtr patterns, IntPtr extensions)
    {
        SmonScanOptionFlags flags = 0;
        if (!IncludeHidden) flags |= SmonScanOptionFlags.ExcludeHidden;
        if (!IncludeSystem) flags |= SmonScanOptionFlags.ExcludeSystem;
        if (!IncludeTemporary) flags |= SmonScanOptionFlags.ExcludeTemporary;
        if (!IncludeReparsePoints) flags |= SmonScanOptionFlags.ExcludeReparsePoints;
        if (ForceDirectoryScanner) flags |= SmonScanOptionFlags.ForceDirectoryScanner;

        return new SmonScanOptionsNative
        {
            StructSize = checked((uint)Marshal.SizeOf<SmonScanOptionsNative>()),
            Flags = flags,
            MaxDepth = MaximumDepth ?? 0,
            WorkerThreads = WorkerThreads ?? 0,
            MinimumFileSize = MinimumFileSize,
            MaximumFileSize = MaximumFileSize ?? 0,
            ExcludedPatterns = patterns,
            ExcludedExtensions = extensions,
        };
    }

    static void ValidateList(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        for (int i = 0; i < values.Count; i++)
        {
            string? value = values[i];
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{parameterName} cannot contain empty values.", parameterName);
            if (value.IndexOfAny([';', ',', '\r', '\n']) >= 0)
                throw new ArgumentException(
                    $"{parameterName} values cannot contain list separators.", parameterName);
        }
    }

    static string? Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join(';', values);
}
