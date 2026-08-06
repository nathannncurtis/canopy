using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace SizeMonitor.Helpers;

public static class ShellItemActions
{
    const uint SeeMaskInvokeIdList = 0x0000000c;
    const uint SeeMaskNoAsync = 0x00000100;
    const int SwShow = 5;
    const int ErrorFileNotFound = 2;
    const int ErrorCancelled = 1223;

    public static void Open(string path)
    {
        ShellItem item = ResolveExisting(path);
        Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        ShellItem item = ResolveExisting(path);
        if (item.Kind == ShellItemKind.Directory)
            throw new IOException($"The selected path is a directory, not a file: {item.Path}");
        Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    public static void OpenFolder(string path)
    {
        ShellItem item = ResolveExisting(path);
        if (item.Kind == ShellItemKind.File)
            throw new IOException($"The selected path is a file, not a directory: {item.Path}");
        Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    public static void OpenContainingFolder(string path)
    {
        ShellItem item = ResolveExisting(path);
        if (item.Path.Contains('"'))
            throw new ArgumentException("Explorer cannot select a path containing a quote.", nameof(path));
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            Arguments = $"/select,\"{item.Path}\"",
        };
        Start(startInfo);
    }

    public static async Task CopyFullPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ResolveExisting(path).Path;
        cancellationToken.ThrowIfCancellationRequested();

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(
                () => Clipboard.SetText(fullPath),
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);
            return;
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Clipboard access requires the WPF dispatcher or an STA thread.");
        Clipboard.SetText(fullPath);
    }

    public static void ShowProperties(string path)
    {
        string fullPath = ResolveExisting(path).Path;
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ShowProperties(fullPath));
            return;
        }
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Windows Properties requires the WPF dispatcher or an STA thread.");

        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskInvokeIdList | SeeMaskNoAsync,
            hwnd = Application.Current?.MainWindow is { } window
                ? new WindowInteropHelper(window).Handle
                : IntPtr.Zero,
            lpVerb = "properties",
            lpFile = fullPath,
            nShow = SwShow,
        };

        if (!ShellExecuteEx(ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to show properties for '{fullPath}'.");
    }

    public static void OpenElevatedTerminal(string path)
    {
        ShellItem item = ResolveExisting(path);
        if (item.Kind == ShellItemKind.Unknown)
            throw new UnauthorizedAccessException($"Cannot determine whether the terminal path is a file or directory: {item.Path}");
        string directory = item.Kind == ShellItemKind.Directory
            ? item.Path
            : Path.GetDirectoryName(item.Path)
                ?? throw new IOException($"The file has no containing directory: {item.Path}");

        var terminal = new ProcessStartInfo("wt.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = directory,
        };
        terminal.ArgumentList.Add("-d");
        terminal.ArgumentList.Add(directory);

        try
        {
            Start(terminal);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorFileNotFound)
        {
            // Windows Terminal is optional; Windows PowerShell is available on supported Windows versions.
            try
            {
                Start(new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = directory,
                });
            }
            catch (Win32Exception fallback) when (fallback.NativeErrorCode == ErrorCancelled)
            {
                return;
            }
            catch (Exception fallback)
            {
                throw new InvalidOperationException(
                    "Neither Windows Terminal nor Windows PowerShell could be started.",
                    new AggregateException(ex, fallback));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return;
        }
    }

    static ShellItem ResolveExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The shell item path is invalid.", nameof(path), ex);
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(fullPath);
            return new(fullPath, (attributes & FileAttributes.Directory) != 0
                ? ShellItemKind.Directory
                : ShellItemKind.File);
        }
        catch (UnauthorizedAccessException)
        {
            // The Windows shell can still select and show properties for ACL-restricted items.
            return new(fullPath, ShellItemKind.Unknown);
        }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException($"The shell item does not exist: {fullPath}", fullPath);
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException($"The shell item does not exist: {fullPath}", fullPath);
        }
    }

    static void Start(ProcessStartInfo startInfo)
    {
        Process? process = Process.Start(startInfo);
        if (process is null && !startInfo.UseShellExecute)
            throw new InvalidOperationException($"Windows did not start '{startInfo.FileName}'.");
        process?.Dispose();
    }

    enum ShellItemKind { Unknown, File, Directory }
    readonly record struct ShellItem(string Path, ShellItemKind Kind);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);
}
