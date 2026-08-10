using System.IO.Compression;
using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ResultFileOperationsTests
{
    [Fact]
    public async Task RenameRequiresExactPreviewConfirmationAndMovesTempFile()
    {
        using var temp = new TemporaryDirectory(); string source = temp.File("old.txt", "content");
        string destination = Path.Combine(temp.Path, "new.txt"); var service = new ResultFileOperationService(new TestBackend());
        ResultFileOperationPreview preview = service.Preview(new(ResultFileOperationKind.Rename, [source], destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(preview, Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(source));
        ResultFileOperationReport report = await service.ExecuteAsync(preview, preview.ConfirmationId, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Succeeded); Assert.False(File.Exists(source)); Assert.True(File.Exists(destination));
    }

    [Fact]
    public void PreviewRejectsStaleCollisionAndMultipleRenameButPreservesLongUnicodePath()
    {
        using var temp = new TemporaryDirectory(); string source = temp.File("資料.txt", "x");
        string collision = temp.File("exists.txt", "x"); var service = new ResultFileOperationService(new TestBackend());
        Assert.False(service.Preview(new(ResultFileOperationKind.Rename, [source], collision)).CanExecute);
        Assert.False(service.Preview(new(ResultFileOperationKind.Rename, [source, collision], temp.Path)).CanExecute);
        Assert.False(service.Preview(new(ResultFileOperationKind.Move, [Path.Combine(temp.Path, "missing")], temp.Path)).CanExecute);
        Assert.False(service.Preview(new(ResultFileOperationKind.Rename, [source],
            Path.Combine(temp.Path, "missing-parent", "renamed.txt"))).CanExecute);
        Assert.False(service.Preview(new(ResultFileOperationKind.CreateZip, [temp.Path],
            Path.Combine(temp.Path, "nested.zip"))).CanExecute);
        string longDestination = Path.Combine(temp.Path, new string('界', 240) + ".txt");
        Assert.Equal(Path.GetFullPath(longDestination), service.Preview(new(ResultFileOperationKind.Rename,
            [source], longDestination)).Items.Single().Destination);
    }

    [Fact]
    public async Task KnownPerItemFailureContinuesAndCancellationMarksRemainingItems()
    {
        using var temp = new TemporaryDirectory(); string first = temp.File("a.txt", "a"), second = temp.File("b.txt", "b");
        var denied = new TestBackend { DeniedSource = first }; var service = new ResultFileOperationService(denied);
        ResultFileOperationPreview preview = service.Preview(new(ResultFileOperationKind.Move, [first, second], temp.Subdirectory("out")));
        ResultFileOperationReport report = await service.ExecuteAsync(preview, preview.ConfirmationId, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Failed); Assert.Equal(1, report.Succeeded);

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        preview = service.Preview(new(ResultFileOperationKind.Compress, [first], null));
        report = await service.ExecuteAsync(preview, preview.ConfirmationId, cancelled.Token);
        Assert.Equal(1, report.Cancelled);
    }

    [Fact]
    public async Task CompressionDirectionIsDelegatedAndZipNeverDeletesSources()
    {
        using var temp = new TemporaryDirectory(); string file = temp.File("résumé.txt", "hello");
        var backend = new TestBackend(); var service = new ResultFileOperationService(backend);
        foreach ((ResultFileOperationKind kind, bool expected) in new[] { (ResultFileOperationKind.Compress, true), (ResultFileOperationKind.Decompress, false) })
        {
            ResultFileOperationPreview preview = service.Preview(new(kind, [file]));
            Assert.Equal(1, (await service.ExecuteAsync(preview, preview.ConfirmationId, TestContext.Current.CancellationToken)).Succeeded);
            Assert.Equal(expected, backend.CompressionRequests.Last().Compressed);
        }
        string zip = Path.Combine(temp.Path, "backup.zip");
        ResultFileOperationPreview archive = service.Preview(new(ResultFileOperationKind.CreateZip, [file], zip));
        Assert.Equal(1, (await service.ExecuteAsync(archive, archive.ConfirmationId, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True(File.Exists(file)); Assert.True(File.Exists(zip));
        using ZipArchive opened = ZipFile.OpenRead(zip); Assert.Equal("hello", new StreamReader(opened.Entries.Single().Open()).ReadToEnd());
    }

    [Fact]
    public async Task ReplacementAfterPreviewIsRejectedByStableSnapshot()
    {
        using var temp = new TemporaryDirectory(); string source = temp.File("same.txt", "1234");
        var backend = new TestBackend(); var service = new ResultFileOperationService(backend);
        ResultFileOperationPreview preview = service.Preview(new(ResultFileOperationKind.Rename, [source], Path.Combine(temp.Path, "new.txt")));
        File.Delete(source); System.IO.File.WriteAllText(source, "1234");
        System.IO.File.SetLastWriteTimeUtc(source, preview.Items[0].Snapshot!.LastWriteUtc);
        backend.RotateIdentity(source);
        ResultFileOperationReport report = await service.ExecuteAsync(preview, preview.ConfirmationId, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Failed); Assert.Contains("changed", report.Outcomes[0].Error);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void PreviewRejectsSameBasenameDestinationCollision()
    {
        using var temp = new TemporaryDirectory(); string left = temp.Subdirectory("left"), right = temp.Subdirectory("right");
        string a = System.IO.Path.Combine(left, "same.txt"), b = System.IO.Path.Combine(right, "same.txt");
        System.IO.File.WriteAllText(a, "a"); System.IO.File.WriteAllText(b, "b");
        ResultFileOperationPreview preview = new ResultFileOperationService(new TestBackend()).Preview(
            new(ResultFileOperationKind.Move, [a, b], temp.Subdirectory("out")));
        Assert.False(preview.CanExecute); Assert.All(preview.Items, item => Assert.Contains("same destination", item.Error));
    }

    [Fact]
    public async Task CompactPlanIsLiteralRejectsUnsupportedVolumeAndRunnerKillsOnCancel()
    {
        string path = @"C:\資料 & literal";
        ExternalProcessRequest plan = NtfsCompressionPlan.Create(path, directory: true, compress: true, supportsCompression: true);
        Assert.Equal(["/C", "/Q", "/S:" + path, path], plan.Arguments); Assert.DoesNotContain("/I", plan.Arguments);
        Assert.Throws<NotSupportedException>(() => NtfsCompressionPlan.Create(path, false, true, false));
        var runner = new ResultOperationProcessRunner();
        Assert.Equal(7, (await runner.RunAsync(new("cmd.exe", ["/c", "exit 7"]), TestContext.Current.CancellationToken)).ExitCode);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new("powershell.exe", ["-NoProfile", "-Command",
                "while ($true) { [Console]::Out.WriteLine('output'); [Console]::Error.WriteLine('error') }"]),
            cancellation.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CrossVolumeStageCommitsBeforeSourceDeleteAndCleansFailuresWithoutFollowingReparses()
    {
        var files = new RecordingMutation();
        var traversal = new FixedTraversal([new("source\\file", "file", false)]);
        await StagedDirectoryMove.ExecuteAsync("source", "destination", "stage", traversal, files, TestContext.Current.CancellationToken);
        Assert.True(files.Events.IndexOf("move:stage:destination") < files.Events.IndexOf("delete:source"));
        Assert.True(files.Events.IndexOf("metadata:stage") < files.Events.IndexOf("move:stage:destination"));

        files = new RecordingMutation { FailCopy = true };
        await Assert.ThrowsAsync<IOException>(() => StagedDirectoryMove.ExecuteAsync("source", "destination", "stage",
            traversal, files, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("delete:source", files.Events); Assert.Contains("delete:stage", files.Events);

        files = new RecordingMutation();
        await Assert.ThrowsAsync<IOException>(() => StagedDirectoryMove.ExecuteAsync("source", "destination", "stage",
            new FixedTraversal([new("source\\junction", "junction", true, true)]), files, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("copy:source\\junction", files.Events);
        Assert.False(DirectoryMoveRouting.RequiresStaging(@"C:\source", @"c:\destination"));
        Assert.True(DirectoryMoveRouting.RequiresStaging(@"C:\source", @"D:\destination"));
    }

    [Fact]
    public void TraversalMarksAndDoesNotDescendIntoDirectorySymlinkWhenPermitted()
    {
        using var temp = new TemporaryDirectory(); string outside = temp.Subdirectory("outside");
        System.IO.File.WriteAllText(System.IO.Path.Combine(outside, "secret.txt"), "secret");
        string root = temp.Subdirectory("root"), link = System.IO.Path.Combine(root, "junction");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        ResultTraversalEntry[] entries = new ReparseSafeResultFileTraversal().Enumerate(root).ToArray();
        ResultTraversalEntry entry = Assert.Single(entries);
        Assert.True(entry.IsReparsePoint); Assert.DoesNotContain(entries, item => item.RelativePath.Contains("secret.txt"));
    }

    [Fact]
    public async Task CaseOnlyRenameIsAllowedAndDirectoryCannotMoveIntoDescendant()
    {
        using var temp = new TemporaryDirectory(); string source = temp.File("CaseName.txt", "x");
        string destination = Path.Combine(temp.Path, "casename.txt"); var service = new ResultFileOperationService(new TestBackend());
        ResultFileOperationPreview rename = service.Preview(new(ResultFileOperationKind.Rename, [source], destination));
        Assert.True(rename.CanExecute);
        Assert.Equal(1, (await service.ExecuteAsync(rename, rename.ConfirmationId, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True(File.Exists(destination));
        string directory = temp.Subdirectory("parent"), child = Path.Combine(directory, "child"); Directory.CreateDirectory(child);
        ResultFileOperationPreview move = service.Preview(new(ResultFileOperationKind.Move, [directory], child));
        Assert.False(move.CanExecute); Assert.Contains("descendants", move.Items[0].Error);
    }

    [Fact]
    public async Task ZipDirectoryMembershipChangeInvalidatesPreviewAndParentChildSelectionIsRejected()
    {
        using var temp = new TemporaryDirectory(); string directory = temp.Subdirectory("folder");
        string first = Path.Combine(directory, "first.txt"); File.WriteAllText(first, "one");
        var service = new ResultFileOperationService(new TestBackend()); string zip = Path.Combine(temp.Path, "out.zip");
        ResultFileOperationPreview preview = service.Preview(new(ResultFileOperationKind.CreateZip, [directory], zip));
        File.WriteAllText(Path.Combine(directory, "second.txt"), "two");
        ResultFileOperationReport report = await service.ExecuteAsync(preview, preview.ConfirmationId, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.Failed); Assert.False(File.Exists(zip));
        Assert.False(service.Preview(new(ResultFileOperationKind.CreateZip, [directory, first], zip)).CanExecute);
    }

    [Fact]
    public async Task ProcessRunnerDrainsLargeStdoutAndStderrWithoutDeadlock()
    {
        var runner = new ResultOperationProcessRunner(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (int exitCode, string error) = await runner.RunAsync(new("cmd.exe",
            ["/c", "(for /L %i in (1,1,12000) do @echo 12345678901234567890) & (for /L %i in (1,1,12000) do @echo error-line 1>&2)"]), timeout.Token);
        Assert.Equal(0, exitCode); Assert.Contains("error-line", error);
    }

    sealed class TestBackend : IResultFileOperationBackend
    {
        readonly Dictionary<string, int> _identities = new(StringComparer.OrdinalIgnoreCase);
        public string? DeniedSource { get; init; }
        public List<(string Path, bool Compressed)> CompressionRequests { get; } = [];
        public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        public bool IsDirectory(string path) => Directory.Exists(path);
        public ResultFileSourceSnapshot Capture(string path)
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            info.Refresh();
            int generation = _identities.TryGetValue(path, out int value) ? value : (_identities[path] = 1);
            return new($"{generation}:{info.CreationTimeUtc.Ticks:x16}:{(int)info.Attributes:x8}", info is DirectoryInfo,
                info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc);
        }
        public void RotateIdentity(string path) => _identities[path] = _identities.GetValueOrDefault(path, 1) + 1;
        public IReadOnlyList<ResultFileMemberSnapshot> CaptureMembers(string directory) =>
            new ReparseSafeResultFileTraversal().Enumerate(directory)
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
                .Select(item => new ResultFileMemberSnapshot(item.RelativePath, Capture(item.FullPath))).ToArray();
        public Task MoveAsync(string source, string destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (source == DeniedSource) throw new UnauthorizedAccessException("Denied for test.");
            if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
            return Task.CompletedTask;
        }
        public Task SetCompressionAsync(string path, bool compressed, CancellationToken token)
        { token.ThrowIfCancellationRequested(); CompressionRequests.Add((path, compressed)); return Task.CompletedTask; }
        public Task CreateZipAsync(IReadOnlyList<string> sources, string destination, CancellationToken token)
        {
            using ZipArchive archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            foreach (string source in sources)
            { token.ThrowIfCancellationRequested(); archive.CreateEntryFromFile(source, System.IO.Path.GetFileName(source)); }
            return Task.CompletedTask;
        }
    }
    sealed class FixedTraversal(IReadOnlyList<ResultTraversalEntry> entries) : IResultFileTraversal
    { public IEnumerable<ResultTraversalEntry> Enumerate(string root) => entries; }
    sealed class RecordingMutation : IResultFileMutation
    {
        readonly HashSet<string> _directories = [];
        public bool FailCopy { get; init; }
        public List<string> Events { get; } = [];
        public void CreateDirectory(string path) { _directories.Add(path); Events.Add("create:" + path); }
        public void CopyFile(string source, string destination) { Events.Add("copy:" + source); if (FailCopy) throw new IOException("copy failed"); }
        public void MoveDirectory(string source, string destination) { Events.Add($"move:{source}:{destination}"); _directories.Remove(source); _directories.Add(destination); }
        public void DeleteDirectory(string path, bool recursive) { Events.Add("delete:" + path); _directories.Remove(path); }
        public bool DirectoryExists(string path) => _directories.Contains(path);
        public ResultFileMetadata CaptureMetadata(string path) => new(FileAttributes.Normal, DateTime.UnixEpoch);
        public void ApplyMetadata(string path, ResultFileMetadata metadata) => Events.Add("metadata:" + path);
    }
    sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "canopy-ops-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string File(string name, string content) { string path = System.IO.Path.Combine(Path, name); System.IO.File.WriteAllText(path, content); return path; }
        public string Subdirectory(string name) { string path = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(path); return path; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
