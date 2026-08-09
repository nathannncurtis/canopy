using System.Text.Json;
using System.Text.Json.Serialization;

namespace SizeMonitor.Interop;

public enum WorkspacePanelMode { Split, TreeOnly, TreemapOnly }

public sealed record WorkspaceLayoutRuntimeState
{
    public WorkspacePanelMode PanelMode { get; init; } = WorkspacePanelMode.Split;
    public bool TreemapDetached { get; init; }
    public bool FullScreen { get; init; }
    public WorkspacePanelMode RestorePanelMode { get; init; } = WorkspacePanelMode.Split;
    public bool Maximized { get; init; }
    public bool RestoreMaximized { get; init; }
}

public static class WorkspaceLayoutTransitions
{
    public static WorkspaceLayoutRuntimeState SetPanelMode(WorkspaceLayoutRuntimeState state, WorkspacePanelMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return state.FullScreen ? state with { RestorePanelMode = mode } : state with { PanelMode = mode };
    }

    public static WorkspaceLayoutRuntimeState SetDetached(WorkspaceLayoutRuntimeState state, bool detached) =>
        state.FullScreen ? state : state with { TreemapDetached = detached };

    public static WorkspaceLayoutRuntimeState EnterFullScreen(WorkspaceLayoutRuntimeState state) =>
        state.FullScreen ? state : state with
        {
            FullScreen = true, RestorePanelMode = state.PanelMode, RestoreMaximized = state.Maximized,
            PanelMode = WorkspacePanelMode.TreemapOnly, TreemapDetached = false, Maximized = true,
        };

    public static WorkspaceLayoutRuntimeState ExitFullScreen(WorkspaceLayoutRuntimeState state) =>
        !state.FullScreen ? state : state with
        {
            FullScreen = false, PanelMode = state.RestorePanelMode,
            Maximized = state.RestoreMaximized,
        };
}

public sealed record WorkspaceLayoutState
{
    public int Version { get; init; } = 1;
    public double Left { get; init; } = double.NaN;
    public double Top { get; init; } = double.NaN;
    public double Width { get; init; } = 1100;
    public double Height { get; init; } = 700;
    public bool Maximized { get; init; }
    public WorkspacePanelMode PanelMode { get; init; } = WorkspacePanelMode.Split;
    public double TreeFraction { get; init; } = 0.4;
    public bool TreemapDetached { get; init; }
}

public static class WorkspaceLayoutValidator
{
    public static WorkspaceLayoutState Normalize(WorkspaceLayoutState state,
        double workLeft, double workTop, double workWidth, double workHeight)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != 1 || !double.IsFinite(workLeft) || !double.IsFinite(workTop) ||
            !double.IsFinite(workWidth) || !double.IsFinite(workHeight) || workWidth <= 0 || workHeight <= 0)
            throw new InvalidDataException("Workspace layout version or work area is invalid.");
        double width = ClampFinite(state.Width, 800, Math.Max(800, workWidth), Math.Min(1100, workWidth));
        double height = ClampFinite(state.Height, 500, Math.Max(500, workHeight), Math.Min(700, workHeight));
        width = Math.Min(width, workWidth); height = Math.Min(height, workHeight);
        double left = double.IsFinite(state.Left) ? state.Left : workLeft + (workWidth - width) / 2;
        double top = double.IsFinite(state.Top) ? state.Top : workTop + (workHeight - height) / 2;
        left = Math.Clamp(left, workLeft, workLeft + workWidth - width);
        top = Math.Clamp(top, workTop, workTop + workHeight - height);
        double fraction = ClampFinite(state.TreeFraction, 0.15, 0.85, 0.4);
        if (!Enum.IsDefined(state.PanelMode)) throw new InvalidDataException("Workspace panel mode is invalid.");
        return state with { Left = left, Top = top, Width = width, Height = height, TreeFraction = fraction };
    }

    static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, maximum);
}

public sealed class WorkspaceLayoutStore(string path)
{
    const int MaximumBytes = 32 * 1024;
    readonly SemaphoreSlim _gate = new(1, 1);
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<WorkspaceLayoutState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path); if (info.Length > MaximumBytes) throw new InvalidDataException("Workspace layout file is too large.");
            await using Stream stream = File.OpenRead(path);
            WorkspaceLayoutState state = await JsonSerializer.DeserializeAsync<WorkspaceLayoutState>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Workspace layout is empty.");
            if (state.Version != 1 || !Enum.IsDefined(state.PanelMode)) throw new InvalidDataException("Workspace layout is invalid or unsupported.");
            return state;
        }
        catch (JsonException ex) { throw new InvalidDataException("Workspace layout JSON is invalid.", ex); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(WorkspaceLayoutState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != 1 || !Enum.IsDefined(state.PanelMode)) throw new InvalidDataException("Workspace layout is invalid or unsupported.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                    await JsonSerializer.SerializeAsync(stream, state, Options, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, fullPath, true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
        finally { _gate.Release(); }
    }
}
