namespace SizeMonitor.Interop;

public enum ScanAnomalyKind
{
    DeepHierarchy,
    LongName,
    LongPath,
    TroublesomeWindowsName,
}

public readonly record struct ScanAnomaly(
    uint NodeIndex,
    string Path,
    ScanAnomalyKind Kind,
    string Detail);

public sealed record ScanAnomalyOptions
{
    public int DeepHierarchyThreshold { get; init; } = 32;
    public int LongNameThreshold { get; init; } = 255;
    /// <summary>Maximum scan-relative tree-path length; this is not a MAX_PATH check.</summary>
    public int RelativePathLengthThreshold { get; init; } = 260;
}

/// <summary>
/// Detects anomalies available from a single <see cref="ScanResultManaged"/>.
/// Age and growth anomalies are intentionally excluded because scan results do not
/// currently contain timestamps or historical size samples.
/// </summary>
public static class ScanAnomalyFinder
{
    const uint NoNode = uint.MaxValue;
    static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();
    static readonly char[] InvalidWindowsNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static IReadOnlyList<ScanAnomaly> Find(
        ScanResultManaged result,
        ScanAnomalyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new ScanAnomalyOptions();
        ValidateOptions(options);
        if (result.Nodes.Length != result.Names.Length)
            throw new ArgumentException("The result must have one name per node.", nameof(result));

        int count = result.Nodes.Length;
        var depths = new int[count];
        var pathLengths = new int[count];
        ValidateTopology(result, depths, pathLengths, cancellationToken);

        var anomalies = new List<ScanAnomaly>();
        for (uint index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = result.Names[index];
            bool deep = depths[index] > options.DeepHierarchyThreshold;
            bool longName = name.Length > options.LongNameThreshold;
            bool longPath = pathLengths[index] > options.RelativePathLengthThreshold;
            string? problem = null;
            bool troublesome = result.Nodes[index].Parent != NoNode &&
                TryGetWindowsNameProblem(name, out problem);
            if (!deep && !longName && !longPath && !troublesome) continue;

            string path = ResolvePath(result, index, cancellationToken);
            if (deep)
                anomalies.Add(new(index, path, ScanAnomalyKind.DeepHierarchy,
                    $"Depth {depths[index]} exceeds {options.DeepHierarchyThreshold}."));
            if (longName)
                anomalies.Add(new(index, path, ScanAnomalyKind.LongName,
                    $"Name length {name.Length} exceeds {options.LongNameThreshold}."));
            if (longPath)
                anomalies.Add(new(index, path, ScanAnomalyKind.LongPath,
                    $"Relative path length {pathLengths[index]} exceeds " +
                    $"{options.RelativePathLengthThreshold}."));
            if (troublesome)
                anomalies.Add(new(index, path, ScanAnomalyKind.TroublesomeWindowsName, problem!));
        }
        return anomalies;
    }

    static void ValidateTopology(
        ScanResultManaged result,
        int[] depths,
        int[] pathLengths,
        CancellationToken token)
    {
        var states = new byte[result.Nodes.Length];
        var chain = new List<uint>();
        for (uint start = 0; start < result.Nodes.Length; start++)
        {
            token.ThrowIfCancellationRequested();
            if (states[start] == 2) continue;
            chain.Clear();
            uint current = start;
            while (current != NoNode)
            {
                token.ThrowIfCancellationRequested();
                if (current >= result.Nodes.Length)
                    throw new InvalidDataException("The scan result contains an invalid parent index.");
                if (states[current] == 2) break;
                if (states[current] == 1)
                    throw new InvalidDataException("The scan result contains a parent cycle.");
                states[current] = 1;
                chain.Add(current);
                current = result.Nodes[current].Parent;
            }

            int depth = current == NoNode ? -1 : depths[current];
            int pathLength = current == NoNode ? 0 : pathLengths[current];
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                uint index = chain[i];
                string name = result.Names[index]
                    ?? throw new InvalidDataException("The scan result contains a null node name.");
                pathLength = checked(pathLength + (pathLength == 0 ? 0 : 1) + name.Length);
                pathLengths[index] = pathLength;
                depths[index] = ++depth;
                states[index] = 2;
            }
        }
    }

    static string ResolvePath(ScanResultManaged result, uint index, CancellationToken token)
    {
        var names = new List<string>();
        uint current = index;
        while (current != NoNode)
        {
            token.ThrowIfCancellationRequested();
            names.Add(result.Names[current]);
            current = result.Nodes[current].Parent;
        }
        names.Reverse();
        return Path.Join(names.ToArray());
    }

    static bool TryGetWindowsNameProblem(string name, out string? problem)
    {
        if (name.Length == 0)
        {
            problem = "The item has an empty name.";
            return true;
        }
        if (name[^1] is ' ' or '.')
        {
            problem = "The name ends with a space or period.";
            return true;
        }
        if (name.Any(character => character < 32 || InvalidWindowsNameChars.Contains(character)))
        {
            problem = "The name contains a character disallowed by Windows.";
            return true;
        }

        string stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem))
        {
            problem = $"'{stem}' is a reserved Windows device name.";
            return true;
        }

        problem = null;
        return false;
    }

    static void ValidateOptions(ScanAnomalyOptions options)
    {
        if (options.DeepHierarchyThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Depth threshold cannot be negative.");
        if (options.LongNameThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Name threshold cannot be negative.");
        if (options.RelativePathLengthThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Path threshold cannot be negative.");
    }

    static HashSet<string> BuildReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (int number = 1; number <= 9; number++)
        {
            names.Add($"COM{number}");
            names.Add($"LPT{number}");
        }
        return names;
    }
}
