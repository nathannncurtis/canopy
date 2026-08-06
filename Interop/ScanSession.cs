using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

public sealed class ScanSession : IDisposable
{
    IntPtr _handle;
    SmonProgressCallback? _callbackDelegate;
    bool _disposed;
    readonly object _gate = new();
    int _nativeCalls;
    ScanSessionState _state = ScanSessionState.Created;

    public ScanSessionState State { get { lock (_gate) return _state; } }
    public ScannerKind Scanner
    {
        get
        {
            if (!TryAcquireHandle(throwIfDisposed: false, out IntPtr handle)) return ScannerKind.Unknown;
            try { return (ScannerKind)Native.Smon_GetScannerKind(handle); }
            finally { ReleaseHandle(); }
        }
    }

    public static ScanSession Start(string path, IProgress<ScanProgress>? progress) => Start(path, progress, null);

    public static unsafe ScanSession Start(string path, IProgress<ScanProgress>? progress, ScanOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options?.Validate();
        var session = new ScanSession();
        SmonProgressCallback? callback = progress is null ? null :
            (dirs, files, bytes, _) =>
            {
                try { progress.Report(new ScanProgress(dirs, files, bytes)); }
                catch { }
            };
        session._callbackDelegate = callback;

        if (options is null)
            session._handle = Native.Smon_BeginScan(path, callback, IntPtr.Zero);
        else
        {
            string? patterns = options.BuildExcludedPatternList();
            string? extensions = options.BuildExcludedExtensionList();
            fixed (char* patternPointer = patterns)
            fixed (char* extensionPointer = extensions)
            {
                SmonScanOptionsNative native = options.ToNative((IntPtr)patternPointer, (IntPtr)extensionPointer);
                session._handle = Native.Smon_BeginScanEx(path, ref native, callback, IntPtr.Zero);
            }
        }
        if (session._handle == IntPtr.Zero)
            throw new ScanException((uint)Marshal.GetLastPInvokeError(), path);
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
                    if (!TryAcquireHandle(throwIfDisposed: true, out IntPtr handle))
                        throw new ObjectDisposedException(nameof(ScanSession));
                    bool completed;
                    try { completed = Native.Smon_Wait(handle, 200); }
                    finally { ReleaseHandle(); }
                    if (completed) break;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ScanResultManaged result = GetResult();
            lock (_gate) if (!_disposed) _state = ScanSessionState.Completed;
            return result;
        }
        catch (OperationCanceledException) { Cancel(); throw; }
        catch { lock (_gate) if (!_disposed) _state = ScanSessionState.Failed; throw; }
    }

    public void Cancel()
    {
        if (!TryAcquireHandle(throwIfDisposed: false, out IntPtr handle)) return;
        try
        {
            lock (_gate) if (!_disposed) _state = ScanSessionState.Cancelling;
            Native.Smon_Cancel(handle);
        }
        finally { ReleaseHandle(); }
    }

    public void Pause() => SetPaused(true);
    public void Resume() => SetPaused(false);

    void SetPaused(bool paused)
    {
        if (!TryAcquireHandle(throwIfDisposed: true, out IntPtr handle)) return;
        try
        {
            lock (_gate)
            {
                ScanSessionState required = paused ? ScanSessionState.Running : ScanSessionState.Paused;
                if (_state != required) return;
            }
            if (Native.Smon_SetPaused(handle, paused))
                lock (_gate) if (!_disposed) _state = paused ? ScanSessionState.Paused : ScanSessionState.Running;
        }
        finally { ReleaseHandle(); }
    }

    unsafe ScanResultManaged GetResult()
    {
        if (!TryAcquireHandle(throwIfDisposed: true, out IntPtr handle))
            throw new ObjectDisposedException(nameof(ScanSession));
        try
        {
            ScanResultNative native = default;
            if (!Native.Smon_GetResult(handle, &native)) throw new ScanException(Native.Smon_GetError(handle));
            return ScanResultManaged.FromNative(native);
        }
        finally { ReleaseHandle(); }
    }

    bool TryAcquireHandle(bool throwIfDisposed, out IntPtr handle)
    {
        lock (_gate)
        {
            if (_disposed || _handle == IntPtr.Zero)
            {
                if (throwIfDisposed) ObjectDisposedException.ThrowIf(_disposed, this);
                handle = IntPtr.Zero;
                return false;
            }
            _nativeCalls++;
            handle = _handle;
            return true;
        }
    }

    void ReleaseHandle()
    {
        lock (_gate)
        {
            _nativeCalls--;
            if (_nativeCalls == 0) Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        IntPtr handle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            handle = _handle;
        }
        if (handle != IntPtr.Zero)
        {
            Native.Smon_Cancel(handle);
            Native.Smon_Wait(handle, uint.MaxValue);
            lock (_gate)
            {
                while (_nativeCalls != 0) Monitor.Wait(_gate);
                _handle = IntPtr.Zero;
            }
            Native.Smon_FreeResult(handle);
        }
        lock (_gate)
        {
            _callbackDelegate = null;
            _state = ScanSessionState.Disposed;
        }
    }
}

public enum ScanSessionState { Created, Running, Paused, Cancelling, Completed, Failed, Disposed }
public enum ScannerKind : uint { Unknown = 0, Mft = 1, Directory = 2 }

public sealed class ScanException(uint nativeError, string? path = null)
    : Exception(path is null
        ? new System.ComponentModel.Win32Exception((int)nativeError).Message
        : $"Could not scan '{path}': {new System.ComponentModel.Win32Exception((int)nativeError).Message}")
{
    public uint NativeError { get; } = nativeError;
}
