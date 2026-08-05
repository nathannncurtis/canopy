using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

[Flags]
public enum CoreCapability : ulong
{
    None = 0,
    MftScanner = 0x00000001,
    DirectoryScanner = 0x00000002,
    PauseResume = 0x00000004,
    Avx2Assembly = 0x00000008,
}

[StructLayout(LayoutKind.Sequential)]
internal struct SmonCapabilitiesNative
{
    public uint StructSize;
    public uint AbiVersion;
    public CoreCapability Flags;
    public uint MaxNodes;
    public uint MaxNameBytes;
}

public sealed record CoreCapabilities(
    uint AbiVersion,
    CoreCapability Flags,
    uint MaxNodes,
    uint MaxNameBytes)
{
    public const uint ExpectedAbiVersion = 1;

    public bool Supports(CoreCapability capability) => (Flags & capability) == capability;

    public static CoreCapabilities Read()
    {
        uint exportedVersion = Native.Smon_GetAbiVersion();
        ValidateCompatibility(exportedVersion);

        var native = new SmonCapabilitiesNative
        {
            StructSize = checked((uint)Marshal.SizeOf<SmonCapabilitiesNative>()),
        };
        if (!Native.Smon_GetCapabilities(ref native))
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Canopy.Core.dll did not return its capabilities.");
        ValidateCompatibility(native.AbiVersion);
        if (native.StructSize < Marshal.SizeOf<SmonCapabilitiesNative>())
            throw new CoreCompatibilityException(
                $"Canopy.Core.dll returned an invalid capability structure size ({native.StructSize}).");

        return new CoreCapabilities(
            native.AbiVersion,
            native.Flags,
            native.MaxNodes,
            native.MaxNameBytes);
    }

    public static void ValidateCompatibility(uint actualVersion)
    {
        if (actualVersion != ExpectedAbiVersion)
            throw new CoreCompatibilityException(
                $"Canopy.Core.dll ABI {actualVersion} is incompatible with the application " +
                $"(expected ABI {ExpectedAbiVersion}). Reinstall matching application files.");
    }
}

public sealed class CoreCompatibilityException(string message) : Exception(message);
