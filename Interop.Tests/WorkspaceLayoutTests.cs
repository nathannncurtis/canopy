using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class WorkspaceLayoutTests
{
    [Fact]
    public void NormalizesOffscreenBoundsAndPanelFraction()
    {
        WorkspaceLayoutState result = WorkspaceLayoutValidator.Normalize(new()
        { Left = -10000, Top = 9000, Width = 4000, Height = 100, TreeFraction = 0.99 }, 0, 0, 1920, 1080);
        Assert.Equal(0, result.Left); Assert.Equal(580, result.Top); Assert.Equal(1920, result.Width);
        Assert.Equal(500, result.Height); Assert.Equal(0.85, result.TreeFraction);
    }

    [Fact]
    public void UsesCenteredFallbackForNonFiniteBounds()
    {
        WorkspaceLayoutState result = WorkspaceLayoutValidator.Normalize(new(), 100, 50, 1600, 900);
        Assert.Equal(350, result.Left); Assert.Equal(150, result.Top);
    }

    [Fact]
    public async Task RoundTripsEveryWorkspaceModeAtomically()
    {
        string directory = TemporaryDirectory(), file = Path.Combine(directory, "layout.json");
        try
        {
            var store = new WorkspaceLayoutStore(file);
            foreach (WorkspacePanelMode mode in Enum.GetValues<WorkspacePanelMode>())
            {
                var state = new WorkspaceLayoutState { Left = 10, Top = 20, Width = 1200, Height = 800,
                    Maximized = true, PanelMode = mode, TreeFraction = 0.33, TreemapDetached = true };
                await store.SaveAsync(state, TestContext.Current.CancellationToken);
                Assert.Equal(state, await store.LoadAsync(TestContext.Current.CancellationToken));
            }
            Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RejectsUnknownFieldsAndOversizedFiles()
    {
        string directory = TemporaryDirectory(), file = Path.Combine(directory, "layout.json");
        try
        {
            var store = new WorkspaceLayoutStore(file);
            await File.WriteAllTextAsync(file, "{\"version\":1,\"surprise\":true}", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
            await File.WriteAllBytesAsync(file, new byte[33 * 1024], TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FullScreenTransitionAttachesTreemapAndRestoresModeAndMaximizedState()
    {
        var initial = new WorkspaceLayoutRuntimeState
        { PanelMode = WorkspacePanelMode.TreeOnly, TreemapDetached = true, Maximized = false };
        WorkspaceLayoutRuntimeState full = WorkspaceLayoutTransitions.EnterFullScreen(initial);
        Assert.True(full.FullScreen); Assert.True(full.Maximized); Assert.False(full.TreemapDetached);
        Assert.Equal(WorkspacePanelMode.TreemapOnly, full.PanelMode);
        WorkspaceLayoutRuntimeState restored = WorkspaceLayoutTransitions.ExitFullScreen(full);
        Assert.False(restored.FullScreen); Assert.False(restored.Maximized);
        Assert.Equal(WorkspacePanelMode.TreeOnly, restored.PanelMode);
    }

    [Fact]
    public void ModeRequestedDuringFullScreenIsAppliedOnExit()
    {
        WorkspaceLayoutRuntimeState full = WorkspaceLayoutTransitions.EnterFullScreen(new());
        full = WorkspaceLayoutTransitions.SetPanelMode(full, WorkspacePanelMode.TreeOnly);
        Assert.Equal(WorkspacePanelMode.TreemapOnly, full.PanelMode);
        Assert.Equal(WorkspacePanelMode.TreeOnly, WorkspaceLayoutTransitions.ExitFullScreen(full).PanelMode);
    }

    [Fact]
    public void DetachAttachTransitionsAreIgnoredWhileFullScreen()
    {
        WorkspaceLayoutRuntimeState state = WorkspaceLayoutTransitions.SetDetached(new(), true);
        Assert.True(state.TreemapDetached);
        state = WorkspaceLayoutTransitions.SetDetached(state, false);
        Assert.False(state.TreemapDetached);
        WorkspaceLayoutRuntimeState full = WorkspaceLayoutTransitions.EnterFullScreen(state);
        Assert.Same(full, WorkspaceLayoutTransitions.SetDetached(full, true));
    }

    static string TemporaryDirectory() { string path = Path.Combine(Path.GetTempPath(), "canopy-layout-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
