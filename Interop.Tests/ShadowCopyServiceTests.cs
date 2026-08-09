using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ShadowCopyServiceTests
{
    [Fact]
    public async Task ListsAndOrdersSnapshotsWithoutMutation()
    {
        var runner = new FakeRunner("""[{"Id":"old","DeviceObject":"\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy1","VolumeName":"\\\\?\\Volume{a}\\","DriveRoot":"C:","CreatedUtc":"2026-01-01T00:00:00Z"},{"Id":"new","DeviceObject":"\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy2","VolumeName":"\\\\?\\Volume{a}\\","DriveRoot":"C:","CreatedUtc":"2026-02-01T00:00:00Z"}]""");
        IReadOnlyList<ShadowCopyInfo> result = await new WindowsShadowCopyService(runner).ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["new", "old"], result.Select(item => item.Id));
        Assert.DoesNotContain("Invoke-CimMethod", runner.Scripts.Single());
    }

    [Fact]
    public void MapsOnlyPathsOnTheSnapshotsVolume()
    {
        var shadow = new ShadowCopyInfo("id", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7",
            @"\\?\Volume{x}\", @"C:\", DateTimeOffset.UtcNow);
        Assert.EndsWith(@"\Users\Alice", WindowsShadowCopyService.ResolvePath(shadow, @"C:\Users\Alice"));
        Assert.Throws<ArgumentException>(() => WindowsShadowCopyService.ResolvePath(shadow, @"D:\Data"));
        Assert.Throws<ArgumentException>(() => WindowsShadowCopyService.GetLocalDriveRoot(@"\\server\share"));
    }

    [Fact]
    public async Task CreationUsesEnvironmentNotScriptInterpolationAndReturnsMetadata()
    {
        var runner = new FakeRunner("""{"ReturnValue":0,"ShadowID":"created"}""",
            """{"Id":"created","DeviceObject":"\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy3","VolumeName":"\\\\?\\Volume{a}\\","DriveRoot":"C:","CreatedUtc":"2026-02-01T00:00:00Z"}""", "");
        await using ShadowCopyLease lease = await new WindowsShadowCopyService(runner).CreateSessionSnapshotAsync(@"C:\Data", TestContext.Current.CancellationToken);
        Assert.Equal("created", lease.Snapshot.Id);
        Assert.Equal(@"C:\", runner.Environments[0]["CANOPY_VSS_VOLUME"]);
        Assert.DoesNotContain(@"C:\Data", runner.Scripts[0]);
    }

    [Fact]
    public async Task ReleasesOnlySnapshotsCreatedByTheServiceAndOnlyOnce()
    {
        var runner = new FakeRunner("""{"ReturnValue":0,"ShadowID":"created"}""",
            """{"Id":"created","DeviceObject":"\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy3","VolumeName":"\\\\?\\Volume{a}\\","DriveRoot":"C:","CreatedUtc":"2026-02-01T00:00:00Z"}""", "");
        var service = new WindowsShadowCopyService(runner);
        ShadowCopyLease lease = await service.CreateSessionSnapshotAsync(@"C:\", TestContext.Current.CancellationToken);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Assert.Equal(3, runner.Scripts.Count);
        Assert.Contains("Remove-CimInstance", runner.Scripts[2]);
        Assert.Equal("created", runner.Environments[2]["CANOPY_VSS_ID"]);
    }

    [Fact]
    public async Task ExplainsPrivilegeFailures()
    {
        var runner = new FakeRunner("""{"ReturnValue":1,"ShadowID":null}""");
        ShadowCopyException error = await Assert.ThrowsAsync<ShadowCopyException>(() =>
            new WindowsShadowCopyService(runner).CreateSessionSnapshotAsync(@"C:\", TestContext.Current.CancellationToken));
        Assert.Contains("administrator", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    sealed class FakeRunner(params string[] responses) : IShadowCopyCommandRunner
    {
        int _index;
        public List<string> Scripts { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];
        public Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Scripts.Add(script); Environments.Add(environment);
            return Task.FromResult(responses[_index++]);
        }
    }
}
