using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

public sealed class ScanSession : IDisposable
{
    IntPtr                _handle;
    SmonProgressCallback? _callbackDelegate; // rooted to prevent GC while native code holds function pointer
    bool                  _disposed;
    readonly object       _gate = new();
    ScanSessionState      _state = ScanSessionState.Created;

    public ScanSessionState State { get { lock (_gate) return _state; } }
    public ScannerKind Scanner { get { lock (_gate) return _handle == IntPtr.Zero
        ? ScannerKind.Unknown : (ScannerKind)Native.Smon_GetScannerKind(_handle); } }

    // Starts the scan asynchronously; returns immediately.
    public static ScanSession Start(string path, IProgress<ScanProgress>? progress)
        => Start(path, progress, null);

    public static unsafe ScanSession Start(
        string path,
        IProgress<ScanProgress>? progress,
        ScanOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options?.Validate();
        var session = new ScanSession();

        SmonProgressCallback? cb = null;
        if (progress != null)
        {
            cb = (dirs, files, bytes, _) =>
                progress.Report(new ScanProgress(dirs, files, bytes));
        }
        session._callbackDelegate = cb;

        if (options is null)
        {
            session._handle = Native.Smon_BeginScan(path, cb, IntPtr.Zero);
        }
        else
        {
            string? patterns = options.BuildExcludedPatternList();
            string? extensions = options.BuildExcludedExtensionList();
            fixed (char* patternPointer = patterns)
            fixed (char* extensionPointer = extensions)
            {
                SmonScanOptionsNative nativeOptions = options.ToNative(
                    (IntPtr)patternPointer,
                    (IntPtr)extensionPointer);
                session._handle = Native.Smon_BeginScanEx(
                    path, ref nativeOptions, cb, IntPtr.Zero);
            }
        }
        if (session._handle == IntPtr.Zero)
            throw new ScanException((uint)Marshal.GetLastPInvokeError(), path);

        session._state = ScanSessionState.Running;

        return session;
    }

    // Polls until the scan completes, then returns the result. Cancellation is checked every 200 ms.
    public async Task<ScanResultManaged> WaitAsync(CancellationToken ct = default)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        if (Native.Smon_Wait(_handle, 200)) break;
                    }
                    ct.ThrowIfCancellationRequested();
                }
            }, ct);

            // Cancellation and native completion can race. The native handle may
            // become signaled before the polling loop observes the token; honor
            // the caller's cancellation rather than returning a partial result.
            ct.ThrowIfCancellationRequested();
            var result = GetResult();
            lock (_gate) _state = ScanSessionState.Completed;
            return result;
        }
        catch (OperationCanceledException)
        {
            Cancel();
            throw;
        }
        catch
        {
            lock (_gate) if (!_disposed) _state = ScanSessionState.Failed;
            throw;
        }
    }

    // Requests cancellation of the running scan. Non-blocking.
    public void Cancel()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _disposed) return;
            _state = ScanSessionState.Cancelling;
            Native.Smon_Cancel(_handle);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ScanSessionState.Running) return;
            if (Native.Smon_SetPaused(_handle, true)) _state = ScanSessionState.Paused;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ScanSessionState.Paused) return;
            if (Native.Smon_SetPaused(_handle, false)) _state = ScanSessionState.Running;
        }
    }

    unsafe ScanResultManaged GetResult()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ScanResultNative native = default;
            if (!Native.Smon_GetResult(_handle, &native))
                throw new ScanException(Native.Smon_GetError(_handle));
            return ScanResultManaged.FromNative(native);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
            // Cancel any in-progress scan and wait for the native thread to
            // finish before freeing the context.  Smon_FreeResult destroys the
            // ScanContext that the native thread is executing inside; calling it
            // while the thread is still running is a use-after-free.
                Native.Smon_Cancel(_handle);
                Native.Smon_Wait(_handle, uint.MaxValue); // blocks until thread exits
                Native.Smon_FreeResult(_handle);
                _handle = IntPtr.Zero;
            }
            _callbackDelegate = null;
            _state = ScanSessionState.Disposed;
        }
    }
}


public enum ScanSessionState
{
    Created,
    Running,
    Paused,
    Cancelling,
    Completed,
    Failed,
    Disposed,
}

public enum ScannerKind : uint
{
    Unknown = 0,
    Mft = 1,
    Directory = 2,
}

public sealed class ScanException(uint nativeError, string? path = null)
    : Exception(path is null
        ? new System.ComponentModel.Win32Exception((int)nativeError).Message
        : $"Could not scan '{path}': {new System.ComponentModel.Win32Exception((int)nativeError).Message}")
{
    public uint NativeError { get; } = nativeError;
}
