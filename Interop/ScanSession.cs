using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

public sealed class ScanSession : IDisposable
{
    SafeScanHandle? _handle;
    SmonProgressCallback? _callbackDelegate;
    bool _disposed;
    readonly object _gate = new();
    ProgressCoalescer? _progress;
    ScanSessionState _state = ScanSessionState.Created;
    ScanErrorInfo? _lastErrorInfo;

    public ScanSessionState State { get { lock (_gate) return _state; } }
    public ScanErrorInfo? LastErrorInfo { get { lock (_gate) return _lastErrorInfo; } }
    public ScannerKind Scanner
    {
        get
        {
            SafeScanHandle? handle = GetHandle(throwIfDisposed: false);
            return handle is null ? ScannerKind.Unknown : (ScannerKind)Native.Smon_GetScannerKind(handle);
        }
    }

    public ScannerRouteInfo? RouteInfo
    {
        get
        {
            SafeScanHandle? handle = _handle;
            if (handle is null || handle.IsInvalid) return null;
            var native = new SmonRouteInfoNative
            {
                StructSize = checked((uint)Marshal.SizeOf<SmonRouteInfoNative>()),
            };
            return Native.Smon_GetRouteInfo(handle, ref native)
                ? new(native.ScannerKind, native.FilesystemKind, native.FallbackReason,
                    native.CloudBacked != 0)
                : null;
        }
    }

    public ScanNodeMetadata? GetNodeMetadata(uint nodeIndex)
    {
        SafeScanHandle? handle = _handle;
        if (handle is null || handle.IsInvalid) return null;
        var native = new SmonNodeMetadataNative
        {
            StructSize = checked((uint)Marshal.SizeOf<SmonNodeMetadataNative>()),
        };
        return Native.Smon_GetNodeMetadata(handle, nodeIndex, ref native)
            ? new(native.Flags, native.LinkCount, native.VolumeSerial, native.FileId,
                native.LogicalBytes, native.AllocatedBytes, native.UniquelyAccountedBytes)
            : null;
    }

    public static ScanSession Start(string path, IProgress<ScanProgress>? progress) => Start(path, progress, null);

    public static unsafe ScanSession Start(string path, IProgress<ScanProgress>? progress, ScanOptions? options) =>
        Start(path, progress, options, null);

    public static unsafe ScanSession Start(string path, IProgress<ScanProgress>? progress, ScanOptions? options,
        ScanProgressOptions? progressOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options?.Validate();
        progressOptions ??= new ScanProgressOptions();
        progressOptions.Validate();
        var session = new ScanSession();
        session._progress = progress is null ? null : new ProgressCoalescer(progress, progressOptions.MinimumInterval);
        SmonProgressCallback? callback = progress is null ? null :
            (dirs, files, bytes, _) =>
            {
                // No managed exception may unwind through a reverse-P/Invoke frame.
                try { session._progress?.Offer(new ScanProgress(dirs, files, bytes)); }
                catch { }
            };
        session._callbackDelegate = callback;

        IntPtr rawHandle;
        if (options is null)
            rawHandle = Native.Smon_BeginScan(path, callback, IntPtr.Zero);
        else
        {
            string? patterns = options.BuildExcludedPatternList();
            string? extensions = options.BuildExcludedExtensionList();
            fixed (char* patternPointer = patterns)
            fixed (char* extensionPointer = extensions)
            {
                SmonScanOptionsNative native = options.ToNative((IntPtr)patternPointer, (IntPtr)extensionPointer);
                rawHandle = Native.Smon_BeginScanEx(path, ref native, callback, IntPtr.Zero);
            }
        }
        if (rawHandle == IntPtr.Zero)
            throw new ScanException((uint)Marshal.GetLastPInvokeError(), path);
        session._handle = new SafeScanHandle(rawHandle) { CallbackRoot = callback };
        session._state = ScanSessionState.Running;
        return session;
    }

    public async Task<ScanResultManaged> WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    // Snapshot under the state gate, then block outside it. SafeHandle
                    // marshalling pins the native handle for the duration of this call.
                    SafeScanHandle handle = GetHandle(throwIfDisposed: true)!;
                    bool completed = Native.Smon_Wait(handle, 50);
                    ReportNativeStatus(handle);
                    if (completed) break;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeScanHandle preResultHandle = GetHandle(throwIfDisposed: true)!;
            ScanProgress aggregation = (ReadStatus(preResultHandle, terminal: false) ?? new(0, 0, 0))
                with { Phase = ScanPhase.Aggregation };
            _progress?.Offer(aggregation, force: true);
            ScanResultManaged result = GetResult();
            SafeScanHandle handle = GetHandle(throwIfDisposed: true)!;
            ScanProgress finalizing = (ReadStatus(handle, terminal: false) ?? aggregation)
                with { Phase = ScanPhase.Finalization, IsTerminal = false };
            _progress?.Offer(finalizing, force: true);
            ScanProgress terminal = ReadStatus(handle, terminal: true) ??
                new(result.DirCount, result.FileCount, result.TotalBytes, ScanPhase.Complete, true);
            _progress?.Offer(terminal, force: true);
            lock (_gate) if (!_disposed) _state = ScanSessionState.Completed;
            return result;
        }
        catch (OperationCanceledException)
        {
            Cancel();
            ReportTerminalFallback();
            throw;
        }
        catch
        {
            ReportTerminalFallback();
            lock (_gate) if (!_disposed) _state = ScanSessionState.Failed;
            throw;
        }
    }

    public void Cancel()
    {
        SafeScanHandle? handle = GetHandle(throwIfDisposed: false);
        if (handle is null) return;
        lock (_gate) if (!_disposed) _state = ScanSessionState.Cancelling;
        Native.Smon_Cancel(handle);
    }

    public void Pause() => SetPaused(true);
    public void Resume() => SetPaused(false);

    void SetPaused(bool paused)
    {
        SafeScanHandle? handle = GetHandle(throwIfDisposed: true);
        if (handle is null) return;
        {
            lock (_gate)
            {
                ScanSessionState required = paused ? ScanSessionState.Running : ScanSessionState.Paused;
                if (_state != required) return;
            }
            if (Native.Smon_SetPaused(handle, paused))
                lock (_gate) if (!_disposed) _state = paused ? ScanSessionState.Paused : ScanSessionState.Running;
        }
    }

    unsafe ScanResultManaged GetResult()
    {
        SafeScanHandle handle = GetHandle(throwIfDisposed: true)!;
        {
            ScanResultNative native = default;
            bool succeeded = Native.Smon_GetResult(handle, &native);
            ScanErrorInfo details = ScanErrorInfo.Read(handle);
            lock (_gate) _lastErrorInfo = details;
            if (!succeeded) throw new ScanException(details.Win32Error, errorInfo: details);
            ScanResultManaged result = ScanResultManaged.FromNative(native);
            ScanNodeMetadata?[] metadata = CopyMetadata(handle, result.Nodes.Length);
            result.Metadata = metadata;
            PhysicalStorageMetadata.RollUpDirectories(result);
            return result;
        }
    }

    static unsafe ScanNodeMetadata?[] CopyMetadata(SafeScanHandle handle, int nodeCount)
    {
        var native = new SmonNodeMetadataNative[nodeCount];
        try
        {
            fixed (SmonNodeMetadataNative* buffer = native)
            {
                if (!Native.Smon_CopyNodeMetadata(handle, buffer, checked((uint)nodeCount),
                        checked((uint)Marshal.SizeOf<SmonNodeMetadataNative>()), out uint required))
                {
                    int error = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(error, "Could not copy scan node metadata.");
                }
                if (required != (uint)nodeCount)
                    throw new InvalidDataException(
                        $"Native metadata count {required} did not match result node count {nodeCount}.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            return CopyMetadataLegacy(handle, nodeCount);
        }

        var managed = new ScanNodeMetadata?[nodeCount];
        for (int index = 0; index < native.Length; index++)
        {
            SmonNodeMetadataNative value = native[index];
            managed[index] = new(value.Flags, value.LinkCount, value.VolumeSerial, value.FileId,
                value.LogicalBytes, value.AllocatedBytes, value.UniquelyAccountedBytes);
        }
        return managed;
    }

    static ScanNodeMetadata?[] CopyMetadataLegacy(SafeScanHandle handle, int nodeCount)
    {
        var metadata = new ScanNodeMetadata?[nodeCount];
        for (uint index = 0; index < metadata.Length; index++)
        {
            var value = new SmonNodeMetadataNative
            {
                StructSize = checked((uint)Marshal.SizeOf<SmonNodeMetadataNative>()),
            };
            if (Native.Smon_GetNodeMetadata(handle, index, ref value))
                metadata[index] = new(value.Flags, value.LinkCount, value.VolumeSerial, value.FileId,
                    value.LogicalBytes, value.AllocatedBytes, value.UniquelyAccountedBytes);
        }
        return metadata;
    }


    SafeScanHandle? GetHandle(bool throwIfDisposed)
    {
        lock (_gate)
        {
            if (_disposed || _handle is null || _handle.IsInvalid || _handle.IsClosed)
            {
                if (throwIfDisposed) ObjectDisposedException.ThrowIf(_disposed, this);
                return null;
            }
            return _handle;
        }
    }

    void ReportNativeStatus(SafeScanHandle handle)
    {
        ScanProgress? status = ReadStatus(handle, terminal: false);
        if (status is not null) _progress?.Offer(status);
    }

    static ScanProgress? ReadStatus(SafeScanHandle handle, bool terminal)
    {
        var status = new SmonScanStatusNative
        {
            StructSize = checked((uint)Marshal.SizeOf<SmonScanStatusNative>()),
        };
        return Native.Smon_GetScanStatus(handle, ref status) ? status.ToManaged(terminal) : null;
    }

    void ReportTerminalFallback()
    {
        SafeScanHandle? handle = GetHandle(throwIfDisposed: false);
        ScanProgress terminal = handle is null
            ? new(0, 0, 0, ScanPhase.Complete, true)
            : ReadStatus(handle, terminal: true) ?? new(0, 0, 0, ScanPhase.Complete, true);
        _progress?.Offer(terminal, force: true);
    }

    public void Dispose()
    {
        SafeScanHandle? handle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            handle = _handle;
        }
        if (handle is not null && !handle.IsClosed)
        {
            // Cancellation/join can block on filesystem I/O; never hold _gate here.
            Native.Smon_Cancel(handle);
            Native.Smon_Wait(handle, uint.MaxValue);
            lock (_gate)
            {
                _handle = null;
            }
            handle.Dispose();
        }
        lock (_gate)
        {
            _callbackDelegate = null;
            _state = ScanSessionState.Disposed;
        }
    }
}

internal sealed class ProgressCoalescer(IProgress<ScanProgress> target, TimeSpan minimumInterval)
{
    readonly object _gate = new();
    readonly long _minimumTicks = Math.Max(1, (long)(minimumInterval.TotalSeconds * Stopwatch.Frequency));
    long _lastDelivery;
    ScanProgress? _last;

    public void Offer(ScanProgress value, bool force = false)
    {
        lock (_gate)
        {
            if (_last is not null && value.Phase < _last.Phase) return;
            long now = Stopwatch.GetTimestamp();
            if (!force && _lastDelivery != 0 && now - _lastDelivery < _minimumTicks) return;
            if (!force && value == _last) return;
            _last = value;
            _lastDelivery = now;
            try { target.Report(value); }
            catch { }
        }
    }
}

public enum ScanSessionState { Created, Running, Paused, Cancelling, Completed, Failed, Disposed }
public enum ScannerKind : uint { Unknown = 0, Mft = 1, Directory = 2 }

public enum ScanErrorCategory : uint
{
    None, Argument, Access, Io, Cancelled, Resource, Internal,
}

public enum ScanErrorStage : uint { None, Open, Enumerate, Build }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SmonErrorInfoNative
{
    public uint StructSize;
    public uint Win32Error;
    public ScanErrorCategory Category;
    public ScanErrorStage Stage;
    public uint AccessErrorCount;
    public uint AccessWin32Error;
    public uint PathLength;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Path;
}

public sealed record ScanErrorInfo(
    uint Win32Error,
    ScanErrorCategory Category,
    ScanErrorStage Stage,
    uint AccessErrorCount,
    uint AccessWin32Error,
    string? Path)
{
    internal static ScanErrorInfo Read(SafeScanHandle handle)
    {
        var native = new SmonErrorInfoNative
        {
            StructSize = checked((uint)Marshal.SizeOf<SmonErrorInfoNative>()),
            Path = string.Empty,
        };
        if (!Native.Smon_GetErrorInfo(handle, ref native))
            return new(Native.Smon_GetError(handle), ScanErrorCategory.None, ScanErrorStage.None, 0, 0, null);
        string? path = native.PathLength == 0 ? null : native.Path;
        return new(native.Win32Error, native.Category, native.Stage, native.AccessErrorCount,
            native.AccessWin32Error, path);
    }

    public string WindowsMessage => Win32Error == 0
        ? "The operation completed without a fatal Windows error."
        : new System.ComponentModel.Win32Exception((int)Win32Error).Message;
}

public sealed class ScanException : Exception
{
    public ScanException(uint nativeError, string? path = null, ScanErrorInfo? errorInfo = null)
        : base(BuildMessage(nativeError, errorInfo?.Path ?? path))
    {
        NativeError = nativeError;
        ErrorInfo = errorInfo ?? new(nativeError, ScanErrorCategory.None, ScanErrorStage.None, 0, 0, path);
    }

    public uint NativeError { get; }
    public ScanErrorInfo ErrorInfo { get; }

    static string BuildMessage(uint error, string? path)
    {
        string detail = new System.ComponentModel.Win32Exception((int)error).Message;
        return path is null ? detail : $"Could not scan '{path}': {detail}";
    }
}
