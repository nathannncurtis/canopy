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
    IncludeAlternateStreams = 0x00000020,
    FollowReparsePoints = 0x00000040,
    AllowCrossVolume = 0x00000080,
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
    public uint TraversalPolicyVersion;
    public uint Reserved;
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
    public bool IncludeAlternateStreams { get; init; }
    public bool FollowReparsePoints { get; init; }
    public bool StayOnVolume { get; init; } = true;
    public IReadOnlyList<string> ExcludedPatterns { get; init; } = [];
    public IReadOnlyList<string> ExcludedExtensions { get; init; } = [];

    public void Validate()
    {
        if (MaximumDepth == 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumDepth),
                "MaximumDepth must be at least 1; use null for no limit.");
        if (MaximumFileSize == 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumFileSize),
                "MaximumFileSize must be at least 1; use null for no limit.");
        if (MaximumFileSize is { } maximum && MinimumFileSize > maximum)
            throw new ArgumentException("MinimumFileSize cannot exceed MaximumFileSize.");
        if (WorkerThreads == 0)
            throw new ArgumentOutOfRangeException(nameof(WorkerThreads),
                "WorkerThreads must be at least 1; use null for automatic selection.");
        if (WorkerThreads is > 32)
            throw new ArgumentOutOfRangeException(nameof(WorkerThreads),
                "WorkerThreads cannot exceed 32.");
        ValidateList(ExcludedPatterns, nameof(ExcludedPatterns));
        ValidateList(ExcludedExtensions, nameof(ExcludedExtensions));
        foreach (string extension in ExcludedExtensions)
        {
            string value = extension.Trim();
            int star = value.IndexOf('*');
            if (value.Contains('?') || star >= 0 && (star != 0 || !value.StartsWith("*.", StringComparison.Ordinal) ||
                                                     value.IndexOf('*', 1) >= 0))
                throw new ArgumentException(
                    "ExcludedExtensions accepts extensions such as .tmp or *.tmp, not glob patterns.",
                    nameof(ExcludedExtensions));
        }
        if (FollowReparsePoints && !IncludeReparsePoints)
            throw new ArgumentException("Following reparse points requires including them.");
    }

    internal string? BuildExcludedPatternList() => Join(ExcludedPatterns);
    internal string? BuildExcludedExtensionList() => ExcludedExtensions.Count == 0
        ? null
        : string.Join(';', ExcludedExtensions.Select(NormalizeExtension));

    internal SmonScanOptionsNative ToNative(IntPtr patterns, IntPtr extensions)
    {
        SmonScanOptionFlags flags = 0;
        if (!IncludeHidden) flags |= SmonScanOptionFlags.ExcludeHidden;
        if (!IncludeSystem) flags |= SmonScanOptionFlags.ExcludeSystem;
        if (!IncludeTemporary) flags |= SmonScanOptionFlags.ExcludeTemporary;
        if (!IncludeReparsePoints) flags |= SmonScanOptionFlags.ExcludeReparsePoints;
        if (ForceDirectoryScanner) flags |= SmonScanOptionFlags.ForceDirectoryScanner;
        if (IncludeAlternateStreams) flags |= SmonScanOptionFlags.IncludeAlternateStreams;
        if (FollowReparsePoints) flags |= SmonScanOptionFlags.FollowReparsePoints;
        if (!StayOnVolume) flags |= SmonScanOptionFlags.AllowCrossVolume;

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
            TraversalPolicyVersion = 1,
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
            if (value.IndexOfAny([';', ',', '\r', '\n', '\0']) >= 0)
                throw new ArgumentException(
                    $"{parameterName} values cannot contain list separators.", parameterName);
        }
    }

    static string? Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join(';', values);

    static string NormalizeExtension(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("*.", StringComparison.Ordinal)) return trimmed[1..];
        if (trimmed.StartsWith('*')) return trimmed[1..];
        return trimmed;
    }
}
