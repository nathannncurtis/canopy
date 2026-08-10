using SizeMonitor.Interop;
using System.Text.Json;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class CleanupOperationServiceTests
{
    [Fact]
    public void PreviewDefaultsToRecycleAndDoesNotMutate()
    {
        var fs = new FakeFileSystem(Item("café.txt", "1", 12)); var backend = new FakeBackend();
        CleanupPlan plan = new CleanupOperationService(fs, backend).Preview(["café.txt"]);
        Assert.Equal(CleanupMutationMode.Recycle, plan.Disposition); Assert.Equal((ulong)12, plan.EstimatedReclaimedBytes);
        Assert.Equal("RECYCLE 1 ITEMS", plan.RequiredConfirmation); Assert.Empty(backend.Targets);
    }

    [Fact]
    public void HardLinkAliasesAreNotEstimatedTwice()
    {
        var fs = new FakeFileSystem(Item("a", "same", 8), Item("b", "same", 8));
        CleanupPlan plan = new CleanupOperationService(fs, new FakeBackend()).Preview(["a", "b"]);
        Assert.Equal((ulong)8, plan.EstimatedReclaimedBytes); Assert.Equal((ulong)0, plan.Items[1].EstimatedReclaimedBytes);
    }

    [Fact]
    public void ExternalHardLinkAliasMakesEstimateConservativelyZero()
    {
        CleanupTargetSnapshot item = Item("a", "same", 8) with { LinkCount = 2 };
        Assert.Equal((ulong)0, new CleanupOperationService(new FakeFileSystem(item), new FakeBackend()).Preview(["a"]).EstimatedReclaimedBytes);
    }

    [Fact]
    public void SelectingAllHardLinkAliasesCountsAllocationOnce()
    {
        CleanupTargetSnapshot a = Item("a", "same", 8) with { LinkCount = 2 }, b = Item("b", "same", 8) with { LinkCount = 2 };
        Assert.Equal((ulong)8, new CleanupOperationService(new FakeFileSystem(a, b), new FakeBackend()).Preview(["a", "b"]).EstimatedReclaimedBytes);
    }

    [Fact]
    public void RecursiveDirectoryPreviewIncludesChildren()
    {
        CleanupTargetSnapshot directory = Item("folder", "dir", 0) with { Type = CleanupTargetType.Directory };
        CleanupTargetSnapshot child = Item(Path.Combine("folder", "child"), "child", 27);
        var fs = new FakeFileSystem(directory, child) { Tree = [child] };
        Assert.Equal((ulong)27, new CleanupOperationService(fs, new FakeBackend()).Preview(["folder"], recursive: true).EstimatedReclaimedBytes);
    }

    [Fact]
    public async Task RecursiveDirectoryChangeAfterPreviewIsStale()
    {
        CleanupTargetSnapshot directory = Item("folder", "dir", 0) with { Type = CleanupTargetType.Directory };
        CleanupTargetSnapshot child = Item(Path.Combine("folder", "child"), "child", 27);
        var fs = new FakeFileSystem(directory, child) { Tree = [child] }; var backend = new FakeBackend();
        var service = new CleanupOperationService(fs, backend); CleanupPlan plan = service.Preview(["folder"], CleanupMutationMode.Permanent, true);
        fs.Tree = [child with { Size = 28 }];
        CleanupExecutionReport report = await service.ExecuteAsync(plan, plan.RequiredConfirmation, TestContext.Current.CancellationToken);
        Assert.Equal(CleanupOutcomeKind.Stale, Assert.Single(report.Items).Outcome); Assert.Empty(backend.Targets);
    }

    [Fact]
    public void PreviewRejectsParentAndDescendantTargets()
    {
        CleanupTargetSnapshot parent = Item("parent", "p", 0) with { Type = CleanupTargetType.Directory };
        CleanupTargetSnapshot child = Item(Path.Combine("parent", "child"), "c", 1);
        Assert.Throws<InvalidOperationException>(() => new CleanupOperationService(new FakeFileSystem(parent, child), new FakeBackend()).Preview(["parent", Path.Combine("parent", "child")], recursive: true));
    }

    [Fact]
    public async Task StaleIdentityFailsWithoutMutation()
    {
        var fs = new FakeFileSystem(Item("a", "old", 8)); var backend = new FakeBackend();
        var service = new CleanupOperationService(fs, backend); CleanupPlan plan = service.Preview(["a"]);
        fs.Set(Item("a", "new", 8));
        CleanupExecutionReport report = await service.ExecuteAsync(plan, plan.RequiredConfirmation, TestContext.Current.CancellationToken);
        Assert.Equal(CleanupOutcomeKind.Stale, Assert.Single(report.Items).Outcome); Assert.Empty(backend.Targets);
    }

    [Fact]
    public async Task ExactConfirmationAndPartialFailureAreReportedPerItem()
    {
        var fs = new FakeFileSystem(Item("a", "1", 8), Item("b", "2", 9));
        var backend = new FakeBackend { FailPath = Path.GetFullPath("b") }; var service = new CleanupOperationService(fs, backend);
        CleanupPlan plan = service.Preview(["a", "b"], CleanupMutationMode.Permanent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(plan, "yes", TestContext.Current.CancellationToken));
        CleanupExecutionReport report = await service.ExecuteAsync(plan, plan.RequiredConfirmation, TestContext.Current.CancellationToken);
        Assert.Equal([CleanupOutcomeKind.PermanentlyDeleted, CleanupOutcomeKind.Failed], report.Items.Select(x => x.Outcome));
        Assert.Null(report.UndoToken); Assert.Equal((ulong)8, report.MeasuredReclaimedBytes);
    }

    [Fact]
    public async Task CancellationStopsAtItemBoundaries()
    {
        var cts = new CancellationTokenSource();
        var fs = new FakeFileSystem(Item("a", "1", 1), Item("b", "2", 1));
        var backend = new FakeBackend { AfterMutation = cts.Cancel }; var service = new CleanupOperationService(fs, backend);
        CleanupPlan plan = service.Preview(["a", "b"]); CleanupExecutionReport report = await service.ExecuteAsync(plan, plan.RequiredConfirmation, cts.Token);
        Assert.Equal(CleanupOutcomeKind.Recycled, report.Items[0].Outcome); Assert.Equal(CleanupOutcomeKind.Cancelled, report.Items[1].Outcome);
    }

    [Fact]
    public void RecursiveReparseDirectoryIsRejected()
    {
        CleanupTargetSnapshot link = Item("link", "1", 0) with { Type = CleanupTargetType.Directory, IsReparsePoint = true };
        Assert.Throws<InvalidOperationException>(() => new CleanupOperationService(new FakeFileSystem(link), new FakeBackend()).Preview(["link"], recursive: true));
    }

    [Fact]
    public async Task WindowsRecycleBackendUsesInjectedShellAndNeverClaimsUndo()
    {
        var shell = new FakeShell(); var backend = new WindowsCleanupMutationBackend(shell);
        CleanupTargetSnapshot item = Item("unicode-文件.txt", "9", 20);
        CleanupMutationResult result = await backend.MutateAsync(item, CleanupMutationMode.Recycle, false, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded); Assert.Equal(item.CanonicalPath, shell.Path); Assert.Null(result.VerifiableUndoToken);
        Assert.Contains("Windows Recycle Bin", result.Error);
    }

    [Fact]
    public async Task AppOwnedRecoveryPersistsManifestAndRestoresVerifiedItem()
    {
        string root = Path.Combine(Path.GetTempPath(), "canopy-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "original-文件.txt"), vault = Path.Combine(root, "vault"); await File.WriteAllTextAsync(original, "payload", TestContext.Current.CancellationToken);
            var fs = new WindowsCleanupFileSystem(); var backend = new RecoveryVaultBackend(fs, new FakeBackend(), _ => vault);
            CleanupTargetSnapshot snapshot = fs.Capture(original);
            CleanupMutationResult moved = await backend.MutateAsync(snapshot, CleanupMutationMode.Recoverable, false, TestContext.Current.CancellationToken);
            Assert.True(moved.Succeeded); Assert.False(File.Exists(original)); Assert.NotNull(moved.VerifiableUndoToken); Assert.True(File.Exists(moved.VerifiableUndoToken));
            Assert.True(await backend.UndoAsync(moved.VerifiableUndoToken!, TestContext.Current.CancellationToken)); Assert.Equal("payload", await File.ReadAllTextAsync(original, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AppOwnedRecoveryRefusesRestoreNameCollision()
    {
        string root = Path.Combine(Path.GetTempPath(), "canopy-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "same.txt"), vault = Path.Combine(root, "vault"); await File.WriteAllTextAsync(original, "old", TestContext.Current.CancellationToken);
            var fs = new WindowsCleanupFileSystem(); var backend = new RecoveryVaultBackend(fs, new FakeBackend(), _ => vault);
            CleanupMutationResult moved = await backend.MutateAsync(fs.Capture(original), CleanupMutationMode.Recoverable, false, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(original, "new", TestContext.Current.CancellationToken);
            Assert.False(await backend.UndoAsync(moved.VerifiableUndoToken!, TestContext.Current.CancellationToken)); Assert.Equal("new", await File.ReadAllTextAsync(original, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AppOwnedRecoveryRejectsTamperedPayloadPathEscape()
    {
        string root = Path.Combine(Path.GetTempPath(), "canopy-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "item.txt"), vault = Path.Combine(root, "vault"); await File.WriteAllTextAsync(original, "x", TestContext.Current.CancellationToken);
            var fs = new WindowsCleanupFileSystem(); var backend = new RecoveryVaultBackend(fs, new FakeBackend(), _ => vault);
            CleanupMutationResult moved = await backend.MutateAsync(fs.Capture(original), CleanupMutationMode.Recoverable, false, TestContext.Current.CancellationToken);
            RecoveryVaultManifest manifest = JsonSerializer.Deserialize<RecoveryVaultManifest>(await File.ReadAllTextAsync(moved.VerifiableUndoToken!, TestContext.Current.CancellationToken))!;
            await File.WriteAllTextAsync(moved.VerifiableUndoToken!, JsonSerializer.Serialize(manifest with { StoredPath = Path.Combine(root, "escaped.payload") }), TestContext.Current.CancellationToken);
            Assert.False(await backend.UndoAsync(moved.VerifiableUndoToken!, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InterruptedManifestWriteRollsPayloadBack()
    {
        string root = Path.Combine(Path.GetTempPath(), "canopy-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "rollback-長い名前.txt"), vault = Path.Combine(root, "vault"); await File.WriteAllTextAsync(original, "safe", TestContext.Current.CancellationToken);
            var fs = new WindowsCleanupFileSystem(); var backend = new RecoveryVaultBackend(fs, new FakeBackend(), _ => vault, manifestWriter: (_, _, _) => throw new IOException("interrupted"));
            await Assert.ThrowsAsync<IOException>(async () => await backend.MutateAsync(fs.Capture(original), CleanupMutationMode.Recoverable, false, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(original)); Assert.Equal("safe", await File.ReadAllTextAsync(original, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WindowsIdentitySupportsLongUnicodeTempPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "canopy-長路徑-" + Guid.NewGuid().ToString("N")); string nested = root;
        try
        {
            while (nested.Length < 270) nested = Path.Combine(nested, "資料夾segment"); Directory.CreateDirectory(nested);
            string file = Path.Combine(nested, "файл-文件.txt"); File.WriteAllText(file, "x");
            Assert.Equal((ulong)1, new WindowsCleanupFileSystem().Capture(file).Size);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AbortedShellRecycleIsFailure()
    {
        var backend = new WindowsCleanupMutationBackend(new AbortedShell());
        CleanupMutationResult result = await backend.MutateAsync(Item("a", "1", 1), CleanupMutationMode.Recycle, false, TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded); Assert.Contains("aborted", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VaultResolverUsesLocalAppDataOnlyOnSameVolume()
    {
        string local = Path.Combine(Path.GetTempPath(), "profile", "Local");
        string same = RecoveryVaultPathResolver.Resolve(Path.Combine(Path.GetPathRoot(local)!, "target"), local, "me");
        Assert.StartsWith(local, same, StringComparison.OrdinalIgnoreCase);
        string otherRoot = string.Equals(Path.GetPathRoot(local), "C:\\", StringComparison.OrdinalIgnoreCase) ? "D:\\" : "C:\\";
        string other = RecoveryVaultPathResolver.Resolve(otherRoot + "target", local, "me");
        Assert.StartsWith(otherRoot, other, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(local, other, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultiItemRecoveryRetainsTokensAndRestoresInReverseWithPartialStatus()
    {
        var fs = new FakeFileSystem(Item("a", "1", 1), Item("b", "2", 1)); var backend = new TokenBackend();
        var service = new CleanupOperationService(fs, backend); CleanupPlan plan = service.Preview(["a", "b"], CleanupMutationMode.Recoverable);
        CleanupExecutionReport execution = await service.ExecuteAsync(plan, plan.RequiredConfirmation, TestContext.Current.CancellationToken);
        Assert.All(execution.Items, item => Assert.NotNull(item.UndoToken));
        CleanupUndoReport undo = await service.UndoAllAsync(execution, TestContext.Current.CancellationToken);
        Assert.Equal(["token-b", "token-a"], backend.Undone); Assert.False(undo.Succeeded); Assert.Equal(2, undo.Items.Count);
    }

    static CleanupTargetSnapshot Item(string path, string id, ulong size) => new(Path.GetFullPath(path), new("volume", id), CleanupTargetType.File, size, DateTimeOffset.UnixEpoch, false);

    sealed class FakeFileSystem(params CleanupTargetSnapshot[] items) : ICleanupFileSystem
    {
        readonly Dictionary<string, CleanupTargetSnapshot> _items = items.ToDictionary(x => x.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        public CleanupTargetSnapshot Capture(string path) => _items[Path.GetFullPath(path)];
        public void Set(CleanupTargetSnapshot item) => _items[item.CanonicalPath] = item;
        public IReadOnlyList<CleanupTargetSnapshot> Tree { get; set; } = [];
        public IEnumerable<CleanupTargetSnapshot> EnumerateTree(string directory) => Tree;
    }
    sealed class FakeBackend : ICleanupMutationBackend
    {
        public List<string> Targets { get; } = []; public string? FailPath { get; init; } public Action? AfterMutation { get; init; }
        public ValueTask<CleanupMutationResult> MutateAsync(CleanupTargetSnapshot target, CleanupMutationMode disposition, bool recursive, CancellationToken cancellationToken)
        { Targets.Add(target.CanonicalPath); AfterMutation?.Invoke(); return ValueTask.FromResult(target.CanonicalPath == FailPath ? new CleanupMutationResult(false, 0, "denied") : new CleanupMutationResult(true, target.Size)); }
        public ValueTask<bool> UndoAsync(string token, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
    sealed class FakeShell : IWindowsShellDeleteAdapter
    {
        public string? Path { get; private set; }
        public (int Error, bool Aborted) Recycle(string path) { Path = path; return (0, false); }
    }
    sealed class AbortedShell : IWindowsShellDeleteAdapter { public (int Error, bool Aborted) Recycle(string path) => (0, true); }
    sealed class TokenBackend : ICleanupMutationBackend
    {
        public List<string> Undone { get; } = [];
        public ValueTask<CleanupMutationResult> MutateAsync(CleanupTargetSnapshot target, CleanupMutationMode disposition, bool recursive, CancellationToken cancellationToken) => ValueTask.FromResult(new CleanupMutationResult(true, target.Size, VerifiableUndoToken: "token-" + Path.GetFileName(target.CanonicalPath)));
        public ValueTask<bool> UndoAsync(string token, CancellationToken cancellationToken) { Undone.Add(token); return ValueTask.FromResult(token != "token-a"); }
    }
}
