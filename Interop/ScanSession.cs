using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

public sealed class ScanSession : IDisposable
{
    IntPtr                _handle;
    SmonProgressCallback? _callbackDelegate; // rooted to prevent GC while native code holds function pointer
    bool                  _disposed;

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

        return session;
    }

    // Polls until the scan completes, then returns the result. Cancellation is checked every 200 ms.
    public async Task<ScanResultManaged> WaitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await Task.Run(() =>
        {
            while (!Native.Smon_Wait(_handle, 200))
            {
                ct.ThrowIfCancellationRequested();
            }
        }, ct);

        return GetResult();
    }

    // Requests cancellation of the running scan. Non-blocking.
    public void Cancel() => Native.Smon_Cancel(_handle);

    unsafe ScanResultManaged GetResult()
    {
        ScanResultNative native = default;
        if (!Native.Smon_GetResult(_handle, &native))
            throw new InvalidOperationException("Smon_GetResult failed.");
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
    }
}
