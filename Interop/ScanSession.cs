using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

public sealed class ScanSession : IDisposable
{
    IntPtr                _handle;
    SmonProgressCallback? _callbackDelegate; // rooted to prevent GC while native code holds function pointer
    bool                  _disposed;

    public ScanSessionState State { get; private set; } = ScanSessionState.Created;
    public ScannerKind Scanner => _handle == IntPtr.Zero
        ? ScannerKind.Unknown
        : (ScannerKind)Native.Smon_GetScannerKind(_handle);

    // Starts the scan asynchronously; returns immediately.
    public static ScanSession Start(string path, IProgress<ScanProgress>? progress)
    {
        var session = new ScanSession();

        SmonProgressCallback? cb = null;
        if (progress != null)
        {
            cb = (dirs, files, bytes, _) =>
                progress.Report(new ScanProgress(dirs, files, bytes));
        }
        session._callbackDelegate = cb;

        session._handle = Native.Smon_BeginScan(path, cb, IntPtr.Zero);
        if (session._handle == IntPtr.Zero)
            throw new InvalidOperationException($"Smon_BeginScan failed for path: {path}");

        session.State = ScanSessionState.Running;

        return session;
    }

    // Polls until the scan completes, then returns the result. Cancellation is checked every 200 ms.
    public async Task<ScanResultManaged> WaitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await Task.Run(() =>
            {
                while (!Native.Smon_Wait(_handle, 200))
                    ct.ThrowIfCancellationRequested();
            }, ct);

            var result = GetResult();
            State = ScanSessionState.Completed;
            return result;
        }
        catch (OperationCanceledException)
        {
            Cancel();
            throw;
        }
        catch
        {
            State = ScanSessionState.Failed;
            throw;
        }
    }

    // Requests cancellation of the running scan. Non-blocking.
    public void Cancel()
    {
        if (_handle == IntPtr.Zero || _disposed) return;
        State = ScanSessionState.Cancelling;
        Native.Smon_Cancel(_handle);
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != ScanSessionState.Running) return;
        if (Native.Smon_SetPaused(_handle, true)) State = ScanSessionState.Paused;
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != ScanSessionState.Paused) return;
        if (Native.Smon_SetPaused(_handle, false)) State = ScanSessionState.Running;
    }

    unsafe ScanResultManaged GetResult()
    {
        ScanResultNative native = default;
        if (!Native.Smon_GetResult(_handle, &native))
            throw new ScanException(Native.Smon_GetError(_handle));
        return ScanResultManaged.FromNative(native);
    }

    public void Dispose()
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
        State = ScanSessionState.Disposed;
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

public sealed class ScanException(uint nativeError)
    : Exception(new System.ComponentModel.Win32Exception((int)nativeError).Message)
{
    public uint NativeError { get; } = nativeError;
}
