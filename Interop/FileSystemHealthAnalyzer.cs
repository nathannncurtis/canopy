using System.Reflection;
using System.Runtime.InteropServices;

namespace SizeMonitor.Interop;

public enum FileSystemHealthKind
{
    BrokenSymbolicLink,
    OrphanedShortcut,
    DeepHierarchy,
    LongPath,
    TroublesomeWindowsName,
    VeryOldFile,
    RapidGrowth,
}

public enum SymbolicLinkOutcome { None, Valid, Dangling, Inaccessible, Cyclic }
public enum ShortcutOutcome { None, Resolvable, Missing, NetworkUnavailable, Unsupported, Malformed }
public enum FileAgeTimestamp { LastWrite, Creation, LastAccess }
public enum WindowsNameProblem { None, TrailingSpace, TrailingPeriod, ControlCharacter, InvalidCharacter, ReservedDeviceName }

public sealed record FileSystemObservation(string Path, bool IsDirectory, ulong Size,
    DateTimeOffset LastWriteUtc, FileAttributes Attributes = FileAttributes.Normal,
    string? LinkTarget = null, SymbolicLinkOutcome LinkOutcome = SymbolicLinkOutcome.None,
    string? ShortcutTarget = null, ShortcutOutcome ShortcutOutcome = ShortcutOutcome.None,
    DateTimeOffset? CreationUtc = null, DateTimeOffset? LastAccessUtc = null);

public sealed record FileSystemHealthFinding(FileSystemHealthKind Kind, string Path,
    string Detail, ulong Size = 0, double? GrowthBytesPerHour = null,
    ulong GrowthBytes = 0, SymbolicLinkOutcome LinkOutcome = SymbolicLinkOutcome.None,
    ShortcutOutcome ShortcutOutcome = ShortcutOutcome.None,
    WindowsNameProblem NameProblem = WindowsNameProblem.None);

public sealed record FileSystemHealthOptions
{
    public int DeepHierarchyThreshold { get; init; } = 32;
    public int PathLengthThreshold { get; init; } = 260;
    public TimeSpan OldFileAge { get; init; } = TimeSpan.FromDays(365 * 5);
    public DateTimeOffset? OldFileBeforeUtc { get; init; }
    public FileAgeTimestamp OldFileTimestamp { get; init; } = FileAgeTimestamp.LastWrite;
    public ulong OldFileMinimumSize { get; init; }
    public ulong RapidGrowthMinimumBytes { get; init; } = 100UL * 1024 * 1024;
    public double RapidGrowthMinimumBytesPerHour { get; init; } = 10 * 1024 * 1024;
}

public sealed record FileSystemHealthSnapshot(DateTimeOffset CapturedUtc,
    IReadOnlyDictionary<string, FileSystemObservation> Items);

public sealed record FileSystemHealthAnalysis(IReadOnlyList<FileSystemHealthFinding> Findings,
    FileSystemHealthSnapshot Snapshot);

public interface IFileSystemHealthSource
{
    Task<IReadOnlyList<FileSystemObservation>> ObserveAsync(IReadOnlyList<string> roots,
        CancellationToken cancellationToken);
}

public sealed class FileSystemHealthAnalyzer(IFileSystemHealthSource? source = null,
    TimeProvider? timeProvider = null)
{
    static readonly HashSet<string> ReservedNames = BuildReservedNames();
    static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    readonly IFileSystemHealthSource _source = source ?? new WindowsFileSystemHealthSource();
    readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<FileSystemHealthAnalysis> AnalyzeAsync(IEnumerable<string> roots,
        FileSystemHealthSnapshot? previous = null, FileSystemHealthOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        options ??= new(); Validate(options);
        string[] normalizedRoots = roots.Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalizedRoots.Length == 0) throw new ArgumentException("At least one root is required.", nameof(roots));
        DateTimeOffset captured = _time.GetUtcNow();
        IReadOnlyList<FileSystemObservation> observations =
            await _source.ObserveAsync(normalizedRoots, cancellationToken).ConfigureAwait(false);
        var current = new Dictionary<string, FileSystemObservation>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<FileSystemHealthFinding>();
        var deepestByRoot = new Dictionary<string, (int Depth, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (FileSystemObservation item in observations.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path));
            string path = NormalizeExtendedPath(Path.GetFullPath(item.Path));
            if (!normalizedRoots.Any(root => IsWithin(root, path)))
                throw new InvalidDataException($"The health source returned a path outside the requested roots: {path}");
            if (!current.TryAdd(path, item with { Path = path }))
                throw new InvalidDataException($"The health source returned a duplicate path: {path}");
            string root = normalizedRoots.First(candidate => IsWithin(candidate, path));
            string relative = Path.GetRelativePath(root, path);
            int depth = relative == "." ? 0 : relative.Count(character => character is '\\' or '/') + 1;
            string name = sourceName.Length == 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : sourceName;

            if (item.IsDirectory && (!deepestByRoot.TryGetValue(root, out var deepest) || depth > deepest.Depth ||
                depth == deepest.Depth && StringComparer.OrdinalIgnoreCase.Compare(path, deepest.Path) < 0))
                deepestByRoot[root] = (depth, path);
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && item.LinkOutcome is
                SymbolicLinkOutcome.Dangling or SymbolicLinkOutcome.Inaccessible or SymbolicLinkOutcome.Cyclic)
                findings.Add(new(FileSystemHealthKind.BrokenSymbolicLink, path,
                    LinkDetail(item), item.Size, LinkOutcome: item.LinkOutcome));
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                item.ShortcutOutcome == ShortcutOutcome.Missing)
                findings.Add(new(FileSystemHealthKind.OrphanedShortcut, path,
                    $"The shortcut target does not exist: {item.ShortcutTarget}", item.Size,
                    ShortcutOutcome: item.ShortcutOutcome));
            if (path.Length > options.PathLengthThreshold)
                findings.Add(new(FileSystemHealthKind.LongPath, path,
                    $"Path length {path.Length} exceeds {options.PathLengthThreshold}.", item.Size));
            if (TryGetNameProblem(name, out WindowsNameProblem nameProblem, out string? problem))
                findings.Add(new(FileSystemHealthKind.TroublesomeWindowsName, path, problem!, item.Size,
                    NameProblem: nameProblem));
            DateTimeOffset? fileTimestamp = SelectedTimestamp(item, options.OldFileTimestamp);
            DateTimeOffset cutoff = options.OldFileBeforeUtc?.ToUniversalTime() ?? captured - options.OldFileAge;
            if (!item.IsDirectory && fileTimestamp.HasValue && item.Size >= options.OldFileMinimumSize &&
                fileTimestamp.Value.ToUniversalTime() < cutoff)
                findings.Add(new(FileSystemHealthKind.VeryOldFile, path,
                    $"{options.OldFileTimestamp} timestamp {fileTimestamp.Value:u} is before {cutoff:u}.", item.Size));

            if (previous is not null && previous.Items.TryGetValue(path, out FileSystemObservation? old) &&
                !item.IsDirectory && item.Size > old.Size)
            {
                ulong increase = item.Size - old.Size;
                double percent = old.Size == 0 ? 100 : 100d * increase / old.Size;
                double hours = Math.Max((captured - previous.CapturedUtc).TotalHours, 1d / 3600);
                double rate = increase / hours;
                if (increase >= options.RapidGrowthMinimumBytes || rate >= options.RapidGrowthMinimumBytesPerHour)
                    findings.Add(new(FileSystemHealthKind.RapidGrowth, path,
                        $"Grew by {increase:N0} bytes ({percent:F1}%) at {rate:N0} bytes/hour.", item.Size, rate, increase));
            }
        }
        foreach ((string _, (int depth, string path)) in deepestByRoot)
            if (depth > options.DeepHierarchyThreshold)
                findings.Add(new(FileSystemHealthKind.DeepHierarchy, path,
                    $"Deepest directory depth {depth} exceeds {options.DeepHierarchyThreshold}."));
        return new(findings.OrderBy(finding => finding.Kind)
            .ThenByDescending(finding => finding.Kind == FileSystemHealthKind.RapidGrowth ? finding.GrowthBytes : 0)
            .ThenBy(finding => finding.Path, StringComparer.OrdinalIgnoreCase).ToArray(), new(captured, current));
    }

    static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(NormalizeExtendedPath(Path.GetFullPath(path.Trim())));
    }

    static string NormalizeExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return path[4..];
        return path;
    }

    static bool IsWithin(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    static string LinkDetail(FileSystemObservation item) => item.LinkOutcome switch
    {
        SymbolicLinkOutcome.Dangling => $"The symbolic-link target does not exist: {item.LinkTarget}",
        SymbolicLinkOutcome.Inaccessible => $"The symbolic-link target could not be accessed: {item.LinkTarget}",
        SymbolicLinkOutcome.Cyclic => $"The symbolic link participates in a target cycle: {item.LinkTarget}",
        _ => "The symbolic link is valid.",
    };

    static DateTimeOffset? SelectedTimestamp(FileSystemObservation item, FileAgeTimestamp timestamp) => timestamp switch
    {
        FileAgeTimestamp.Creation => item.CreationUtc,
        FileAgeTimestamp.LastAccess => item.LastAccessUtc,
        _ => item.LastWriteUtc,
    };

    static bool TryGetNameProblem(string name, out WindowsNameProblem kind, out string? problem)
    {
        if (name.Length == 0) { kind = WindowsNameProblem.None; problem = null; return false; }
        if (name[^1] == ' ') { kind = WindowsNameProblem.TrailingSpace; problem = "The name ends with a space."; return true; }
        if (name[^1] == '.') { kind = WindowsNameProblem.TrailingPeriod; problem = "The name ends with a period."; return true; }
        if (name.Any(character => character < 32))
        { kind = WindowsNameProblem.ControlCharacter; problem = "The name contains a control character."; return true; }
        if (name.Any(InvalidNameCharacters.Contains))
        { kind = WindowsNameProblem.InvalidCharacter; problem = "The name contains a character disallowed by Windows."; return true; }
        string stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem)) { kind = WindowsNameProblem.ReservedDeviceName; problem = $"'{stem}' is a reserved Windows device name."; return true; }
        kind = WindowsNameProblem.None; problem = null; return false;
    }

    static void Validate(FileSystemHealthOptions value)
    {
        if (value.DeepHierarchyThreshold < 0 || value.PathLengthThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Depth and path thresholds are invalid.");
        if (value.OldFileAge < TimeSpan.Zero || value.RapidGrowthMinimumBytesPerHour < 0 ||
            !double.IsFinite(value.RapidGrowthMinimumBytesPerHour))
            throw new ArgumentOutOfRangeException(nameof(value), "Age and growth thresholds must be finite and non-negative.");
    }

    static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (int number = 1; number <= 9; number++) { names.Add($"COM{number}"); names.Add($"LPT{number}"); }
        return names;
    }
}

public sealed class WindowsFileSystemHealthSource : IFileSystemHealthSource
{
    public Task<IReadOnlyList<FileSystemObservation>> ObserveAsync(IReadOnlyList<string> roots,
        CancellationToken cancellationToken) => Task.Run<IReadOnlyList<FileSystemObservation>>(() =>
    {
        var observations = new List<FileSystemObservation>();
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out string? directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<string> children;
                try { children = Directory.EnumerateFileSystemEntries(directoryPath).ToArray(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
                foreach (string path in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        FileSystemObservation observation = Observe(path); observations.Add(observation);
                        if (observation.IsDirectory && (observation.Attributes & FileAttributes.ReparsePoint) == 0)
                            pending.Push(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException) { }
                }
            }
        }
        return observations;
    }, cancellationToken);

    static FileSystemObservation Observe(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool directory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        ulong size = directory ? 0 : checked((ulong)((FileInfo)info).Length);
        string? linkTarget = null; SymbolicLinkOutcome linkOutcome = SymbolicLinkOutcome.None;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            linkTarget = info.LinkTarget;
            if (linkTarget is not null)
                linkOutcome = ResolveLinkOutcome(info);
        }
        string? shortcutTarget = null; ShortcutOutcome shortcutOutcome = ShortcutOutcome.None;
        if (!directory && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            (shortcutTarget, shortcutOutcome) = ResolveShortcut(path);
        }
        return new(path, directory, size, info.LastWriteTimeUtc, attributes, linkTarget, linkOutcome,
            shortcutTarget, shortcutOutcome, info.CreationTimeUtc, info.LastAccessTimeUtc);
    }

    static SymbolicLinkOutcome ResolveLinkOutcome(FileSystemInfo start)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileSystemInfo current = start;
        try
        {
            for (int hop = 0; hop < 64; hop++)
            {
                string currentPath = Path.GetFullPath(current.FullName);
                if (!visited.Add(currentPath)) return SymbolicLinkOutcome.Cyclic;
                string? target = current.LinkTarget;
                if (target is null) return current.Exists ? SymbolicLinkOutcome.Valid : SymbolicLinkOutcome.Dangling;
                string targetPath = Path.IsPathFullyQualified(target) ? target :
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentPath)!, target));
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) return SymbolicLinkOutcome.Dangling;
                FileAttributes targetAttributes = File.GetAttributes(targetPath);
                if ((targetAttributes & FileAttributes.ReparsePoint) == 0) return SymbolicLinkOutcome.Valid;
                current = (targetAttributes & FileAttributes.Directory) != 0 ?
                    new DirectoryInfo(targetPath) : new FileInfo(targetPath);
            }
            return SymbolicLinkOutcome.Cyclic;
        }
        catch (UnauthorizedAccessException) { return SymbolicLinkOutcome.Inaccessible; }
        catch (IOException) { return SymbolicLinkOutcome.Inaccessible; }
    }

    static (string? Target, ShortcutOutcome Outcome) ResolveShortcut(string path)
    {
        if (!OperatingSystem.IsWindows()) return (null, ShortcutOutcome.Unsupported);
        object? shell = null, shortcut = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (type is null) return (null, ShortcutOutcome.Unsupported);
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            string? target = shortcut?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            if (string.IsNullOrWhiteSpace(target)) return (target, ShortcutOutcome.Malformed);
            if (File.Exists(target) || Directory.Exists(target)) return (target, ShortcutOutcome.Resolvable);
            return target.StartsWith(@"\\", StringComparison.Ordinal) ?
                (target, ShortcutOutcome.NetworkUnavailable) : (target, ShortcutOutcome.Missing);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MemberAccessException)
        { return (null, ShortcutOutcome.Malformed); }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
