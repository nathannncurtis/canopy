using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
public delegate void SmonProgressCallback(
    ulong   dirsVisited,
    ulong   filesVisited,
    ulong   bytesSeen,
    IntPtr  userData);

internal static unsafe class Native
{
    const string Dll = "Canopy.Core.dll";

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint Smon_GetAbiVersion();

    [DllImport(Dll, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_GetCapabilities(ref SmonCapabilitiesNative capabilities);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr Smon_BeginScan(
        string                path,
        SmonProgressCallback? callback,
        IntPtr                userData);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr Smon_BeginScanEx(
        string path,
        ref SmonScanOptionsNative options,
        SmonProgressCallback? callback,
        IntPtr userData);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_Cancel(IntPtr handle);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_SetPaused(
        IntPtr handle,
        [MarshalAs(UnmanagedType.Bool)] bool paused);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_Wait(IntPtr handle, uint timeoutMs);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint Smon_GetError(IntPtr handle);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_GetErrorInfo(IntPtr handle, ref SmonErrorInfoNative errorInfo);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint Smon_GetScannerKind(IntPtr handle);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_GetResult(IntPtr handle, ScanResultNative* result);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern void Smon_FreeResult(IntPtr handle);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_IsNtfsVolume(string path);
}
