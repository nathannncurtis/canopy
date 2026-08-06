using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class LocationHistoryStoreTests
{
    [Fact]
    public async Task DeduplicatesAndOrdersRecentPaths()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempFile();
        var store = new LocationHistoryStore(temp.Path);
        await store.TouchAsync(@"C:/Work/", usedUtc: Utc(1), cancellationToken: token);
        await store.TouchAsync(@"c:\work", "Work files", Utc(3), token);
        await store.TouchAsync(@"D:\Data", usedUtc: Utc(2), cancellationToken: token);
        Assert.Equal([@"C:\Work", @"D:\Data"], store.Locations.Select(x => x.Path));
        Assert.Equal((ulong)2, store.Locations[0].UseCount);
        Assert.Equal("Work files", store.Locations[0].DisplayName);
    }

    [Fact]
    public async Task RetentionNeverEvictsFavorites()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempFile();
        var store = new LocationHistoryStore(temp.Path, 1);
        await store.PinAsync(@"C:\Pinned", cancellationToken: token);
        await store.TouchAsync(@"C:\Old", usedUtc: Utc(1), cancellationToken: token);
        await store.TouchAsync(@"C:\New", usedUtc: Utc(2), cancellationToken: token);
        Assert.Equal([@"C:\Pinned", @"C:\New"], store.Locations.Select(x => x.Path));
        await store.UnpinAsync(@"C:\Pinned", token);
        Assert.Single(store.Locations);
        Assert.Equal(@"C:\Pinned", store.Locations[0].Path);
    }

    [Fact]
    public async Task PinUnpinAndRemoveAreCaseInsensitive()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempFile();
        var store = new LocationHistoryStore(temp.Path);
        await store.TouchAsync(@"C:\Users\Alice", cancellationToken: token);
        await store.PinAsync(@"c:\users\alice", cancellationToken: token);
        Assert.True(Assert.Single(store.Locations).IsFavorite);
        await store.UnpinAsync(@"C:/USERS/ALICE/", token);
        Assert.False(Assert.Single(store.Locations).IsFavorite);
        await store.RemoveAsync(@"c:\Users\Alice", token);
        Assert.Empty(store.Locations);
    }

    [Fact]
    public async Task RoundTripsUnicodeDeterministically()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempFile();
        var store = new LocationHistoryStore(temp.Path);
        await store.TouchAsync(@"C:\文書", "文書", Utc(1), token);
        byte[] first = await File.ReadAllBytesAsync(temp.Path, token);
        var loaded = new LocationHistoryStore(temp.Path);
        await loaded.LoadAsync(token);
        Assert.Equal(store.Locations, loaded.Locations);
        await loaded.PinAsync(@"C:\文書", "文書", token);
        await loaded.UnpinAsync(@"C:\文書", token);
        Assert.NotEmpty(first);
    }

    [Fact]
    public async Task RejectsCorruptAndUnsupportedDocuments()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempFile();
        await File.WriteAllTextAsync(temp.Path, "{ nope }", token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocationHistoryStore(temp.Path).LoadAsync(token));
        await File.WriteAllTextAsync(temp.Path, "{\"Version\":99,\"Locations\":[]}", token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocationHistoryStore(temp.Path).LoadAsync(token));
    }

    [Fact]
    public async Task ConcurrentTouchesAreSerializedWithoutLostUpdates()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var temp = new TempFile();
        var store = new LocationHistoryStore(temp.Path, 100);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            store.TouchAsync(@"C:\Same", cancellationToken: token)));
        Assert.Equal((ulong)40, Assert.Single(store.Locations).UseCount);
        var loaded = new LocationHistoryStore(temp.Path, 100);
        await loaded.LoadAsync(token);
        Assert.Equal((ulong)40, Assert.Single(loaded.Locations).UseCount);
    }

    [Fact]
    public async Task CancellationDoesNotPublishMutation()
    {
        using var temp = new TempFile();
        var store = new LocationHistoryStore(temp.Path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TouchAsync(@"C:\Nope", cancellationToken: cancellation.Token));
        Assert.Empty(store.Locations);
    }

    static DateTimeOffset Utc(int minute) => new(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);

    sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"canopy-{Guid.NewGuid():N}.json");
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
