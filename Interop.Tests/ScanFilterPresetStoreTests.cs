using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class ScanFilterPresetStoreTests
{
    [Fact]
    public async Task RoundTripsAndSortsPresets()
    {
        string path = TemporaryPath();
        try
        {
            var store = new ScanFilterPresetStore(path);
            await store.UpsertAsync(new("Large", new ScanQuery { MinimumSize = 1_000 }),
                TestContext.Current.CancellationToken);
            await store.UpsertAsync(new("Archives", new ScanQuery { Extensions = ["zip"] }),
                TestContext.Current.CancellationToken);

            IReadOnlyList<ScanFilterPreset> loaded =
                await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["Archives", "Large"], loaded.Select(item => item.Name));
            Assert.Equal(1_000ul, loaded[1].Query.MinimumSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplacesNamesCaseInsensitively()
    {
        string path = TemporaryPath();
        try
        {
            var store = new ScanFilterPresetStore(path);
            await store.UpsertAsync(new("Large", new ScanQuery { MinimumSize = 1 }),
                TestContext.Current.CancellationToken);
            IReadOnlyList<ScanFilterPreset> presets = await store.UpsertAsync(
                new("large", new ScanQuery { MinimumSize = 2 }),
                TestContext.Current.CancellationToken);

            Assert.Single(presets);
            Assert.Equal(2ul, presets[0].Query.MinimumSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeletesExistingPreset()
    {
        string path = TemporaryPath();
        try
        {
            var store = new ScanFilterPresetStore(path);
            await store.UpsertAsync(new("Temporary", new ScanQuery()),
                TestContext.Current.CancellationToken);

            var result = await store.DeleteAsync("temporary", TestContext.Current.CancellationToken);

            Assert.True(result.Removed);
            Assert.Empty(result.Presets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportsMalformedJsonWithoutOverwritingIt()
    {
        string path = TemporaryPath();
        await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);
        try
        {
            var store = new ScanFilterPresetStore(path);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.LoadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("{broken", await File.ReadAllTextAsync(
                path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportsSemanticallyInvalidJsonAsInvalidData()
    {
        string path = TemporaryPath();
        await File.WriteAllTextAsync(path, "[{\"Name\":\"\",\"Query\":{}}]",
            TestContext.Current.CancellationToken);
        try
        {
            var store = new ScanFilterPresetStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.LoadAsync(TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadDeduplicatesNamesCaseInsensitivelyWithLastValueWinning()
    {
        string path = TemporaryPath();
        await File.WriteAllTextAsync(path,
            "[{\"Name\":\"Large\",\"Query\":{\"MinimumSize\":1}},{\"Name\":\"large\",\"Query\":{\"MinimumSize\":2}}]",
            TestContext.Current.CancellationToken);
        try
        {
            var store = new ScanFilterPresetStore(path);
            IReadOnlyList<ScanFilterPreset> loaded =
                await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Single(loaded);
            Assert.Equal(2ul, loaded[0].Query.MinimumSize);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadRepairsNullableQueryCollections()
    {
        string path = TemporaryPath();
        await File.WriteAllTextAsync(path,
            "[{\"Name\":\"Portable\",\"Query\":{\"Extensions\":null,\"Sort\":null}}]",
            TestContext.Current.CancellationToken);
        try
        {
            ScanFilterPreset preset = Assert.Single(await new ScanFilterPresetStore(path)
                .LoadAsync(TestContext.Current.CancellationToken));

            Assert.Empty(preset.Query.Extensions);
            Assert.NotEmpty(preset.Query.Sort);
        }
        finally { File.Delete(path); }
    }

    static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"canopy-presets-{Guid.NewGuid():N}.json");
}
