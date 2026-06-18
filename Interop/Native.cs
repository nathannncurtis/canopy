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

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr Smon_BeginScan(
        string                path,
        SmonProgressCallback? callback,
        IntPtr                userData);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_Cancel(IntPtr handle);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_Wait(IntPtr handle, uint timeoutMs);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_GetResult(IntPtr handle, ScanResultNative* result);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern void Smon_FreeResult(IntPtr handle);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_IsNtfsVolume(string path);
}
