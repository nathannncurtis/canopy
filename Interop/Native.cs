using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    internal static extern bool Smon_Cancel(SafeScanHandle handle);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_SetPaused(
        SafeScanHandle handle,
        [MarshalAs(UnmanagedType.Bool)] bool paused);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_Wait(SafeScanHandle handle, uint timeoutMs);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint Smon_GetError(SafeScanHandle handle);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_GetErrorInfo(SafeScanHandle handle, ref SmonErrorInfoNative errorInfo);

    [DllImport(Dll, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_GetScanStatus(SafeScanHandle handle, ref SmonScanStatusNative status);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint Smon_GetScannerKind(SafeScanHandle handle);

    [DllImport(Dll, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_GetResult(SafeScanHandle handle, ScanResultNative* result);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern void Smon_FreeResult(IntPtr handle);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Smon_IsNtfsVolume(string path);
}

internal sealed class SafeScanHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    readonly Func<IntPtr, bool>? _testRelease;

    public SafeScanHandle() : base(true) { }

    internal SafeScanHandle(IntPtr value) : base(true) => SetHandle(value);

    internal SafeScanHandle(IntPtr value, Func<IntPtr, bool> testRelease) : base(true)
    {
        SetHandle(value);
        _testRelease = testRelease;
    }

    internal object? CallbackRoot { get; set; }

    protected override bool ReleaseHandle()
    {
        if (_testRelease is not null) return _testRelease(handle);
        // SafeHandle finalization is a last-resort cleanup path. Cancel and join
        // before freeing because native callbacks may still reference the session.
        NativeRelease.Cancel(handle);
        NativeRelease.Wait(handle, uint.MaxValue);
        Native.Smon_FreeResult(handle);
        return true;
    }

    static class NativeRelease
    {
        [DllImport("Canopy.Core.dll", EntryPoint = "Smon_Cancel", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Cancel(IntPtr handle);

        [DllImport("Canopy.Core.dll", EntryPoint = "Smon_Wait", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Wait(IntPtr handle, uint timeoutMs);
    }
}
