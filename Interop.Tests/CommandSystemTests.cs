using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class CommandSystemTests
{
    [Fact]
    public async Task SearchesExecutesAndHonorsAvailability()
    {
        bool ran = false; var registry = new AppCommandRegistry();
        registry.Register(new("favorites.toggle", "Toggle favorite", "Locations", "Pin or unpin", "Ctrl+D", ["pin"]),
            _ => { ran = true; return Task.CompletedTask; });
        registry.Register(new("search.save", "Save search", "Search", "Save current filters"),
            _ => Task.CompletedTask, () => false);
        Assert.Equal("favorites.toggle", Assert.Single(registry.Search("pin")).Command.Id);
        Assert.True(await registry.ExecuteAsync("favorites.toggle", TestContext.Current.CancellationToken));
        Assert.True(ran); Assert.False(await registry.ExecuteAsync("search.save", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OverridesDetectCollisionsAndGenerateHelp()
    {
        var registry = Registry(); registry.SetShortcut("search.save", "Ctrl+Shift+S");
        Assert.Equal("search.save", registry.FindCommandByShortcut("Ctrl+Shift+S"));
        Assert.Throws<ArgumentException>(() => registry.SetShortcut("favorites.toggle", "Ctrl+Shift+S"));
        string help = registry.GenerateShortcutHelp(); Assert.Contains("# Keyboard shortcuts", help); Assert.Contains("Ctrl+Shift+S", help);
    }

    [Fact]
    public async Task ShortcutStoreRoundTripsAtomically()
    {
        string path = Path.Combine(Path.GetTempPath(), $"canopy-shortcuts-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ShortcutOverrideStore(path);
            await store.SaveAsync(new Dictionary<string, string?> { ["search.save"] = "Ctrl+S" }, TestContext.Current.CancellationToken);
            Assert.Equal("Ctrl+S", (await store.LoadAsync(TestContext.Current.CancellationToken))["search.save"]);
        }
        finally { File.Delete(path); }
    }

    static AppCommandRegistry Registry()
    {
        var registry = new AppCommandRegistry();
        registry.Register(new("favorites.toggle", "Favorite", "Locations", "Toggle favorite", "Ctrl+D"), _ => Task.CompletedTask);
        registry.Register(new("search.save", "Save search", "Search", "Save filters", "Ctrl+S"), _ => Task.CompletedTask);
        return registry;
    }
}
