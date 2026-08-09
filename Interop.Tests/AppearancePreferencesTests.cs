using SizeMonitor.Interop;
using Xunit;

namespace SizeMonitor.Interop.Tests;

public sealed class AppearancePreferencesTests
{
    [Theory]
    [InlineData(true, false, ResolvedCanopyTheme.Light)]
    [InlineData(false, false, ResolvedCanopyTheme.Dark)]
    [InlineData(true, true, ResolvedCanopyTheme.HighContrast)]
    public void FollowSystemResolvesRuntimeState(bool light, bool highContrast, ResolvedCanopyTheme expected) =>
        Assert.Equal(expected, AppearancePreferenceRules.ResolveTheme(new(), light, highContrast));

    [Fact]
    public void ExplicitHighContrastOverridesSystemTheme() =>
        Assert.Equal(ResolvedCanopyTheme.HighContrast, AppearancePreferenceRules.ResolveTheme(
            new() { Theme = CanopyThemeMode.HighContrast }, true, false));

    [Fact]
    public void DensityAndScaleProduceBoundedDeterministicScale()
    {
        Assert.Equal(0.72, AppearancePreferenceRules.EffectiveScale(new() { UiScale = 0.8, Density = InterfaceDensity.Compact }), 5);
        Assert.Equal(1.5, AppearancePreferenceRules.EffectiveScale(new() { UiScale = 1.5 }), 5);
        Assert.Throws<InvalidDataException>(() => AppearancePreferenceRules.Validate(new() { UiScale = 1.51 }));
    }

    [Fact]
    public async Task RoundTripsEveryPreferenceAtomically()
    {
        string directory = TemporaryDirectory(), file = Path.Combine(directory, "appearance.json");
        try
        {
            var store = new AppearancePreferenceStore(file);
            var expected = new AppearancePreferences { Theme = CanopyThemeMode.Light,
                Palette = TreemapPalette.DeuteranopiaSafe, Density = InterfaceDensity.Compact,
                Motion = MotionPreference.Reduced, UiScale = 1.25 };
            await store.SaveAsync(expected, TestContext.Current.CancellationToken);
            Assert.Equal(expected, await store.LoadAsync(TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RejectsUnknownAndOversizedDocuments()
    {
        string directory = TemporaryDirectory(), file = Path.Combine(directory, "appearance.json");
        try
        {
            var store = new AppearancePreferenceStore(file);
            await File.WriteAllTextAsync(file, "{\"version\":1,\"future\":true}", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
            await File.WriteAllBytesAsync(file, new byte[17 * 1024], TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EveryPaletteProvidesEightOpaqueDistinctRenderColors()
    {
        foreach (TreemapPalette palette in Enum.GetValues<TreemapPalette>())
        {
            IReadOnlyList<uint> colors = AppearancePreferenceRules.TreemapColors(palette);
            Assert.Equal(8, colors.Count); Assert.Equal(8, colors.Distinct().Count());
            Assert.All(colors, color => Assert.Equal(0xFF000000u, color & 0xFF000000u));
        }
    }

    [Fact]
    public void ReducedMotionDeterministicallyBypassesTransition() =>
        Assert.Equal(TimeSpan.Zero, AppearancePreferenceRules.TreemapTransitionDuration(MotionPreference.Reduced));

    [Fact]
    public void FullMotionUsesShortBoundedTransition() =>
        Assert.InRange(AppearancePreferenceRules.TreemapTransitionDuration(MotionPreference.Full),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));

    [Theory]
    [InlineData(CanopyThemeMode.Light, false, false, 0xFFF7F7F7u, 0xFF1B1B1Bu)]
    [InlineData(CanopyThemeMode.Dark, true, false, 0xFF202020u, 0xFFF5F5F5u)]
    [InlineData(CanopyThemeMode.HighContrast, true, false, 0xFF123456u, 0xFFABCDEFu)]
    public void ProjectsApplicationWideThemeResources(CanopyThemeMode mode, bool systemLight,
        bool systemHighContrast, uint background, uint foreground)
    {
        AppearanceResourceProjection projection = AppearancePreferenceRules.ProjectResources(
            new() { Theme = mode }, systemLight, systemHighContrast, 0xFF123456, 0xFFABCDEF);
        Assert.Equal(background, projection.WindowBackgroundArgb);
        Assert.Equal(foreground, projection.WindowForegroundArgb);
    }

    [Fact]
    public void CompactAndComfortableProjectDifferentKeyboardUsableHitTargets()
    {
        AppearanceResourceProjection compact = AppearancePreferenceRules.ProjectResources(new() { Density = InterfaceDensity.Compact }, false, false);
        AppearanceResourceProjection comfortable = AppearancePreferenceRules.ProjectResources(new() { Density = InterfaceDensity.Comfortable }, false, false);
        Assert.InRange(compact.TreeItemMinHeight, 24, 34);
        Assert.True(comfortable.TreeItemMinHeight > compact.TreeItemMinHeight);
        Assert.True(comfortable.ControlVerticalPadding > compact.ControlVerticalPadding);
    }

    [Fact]
    public void UiScaleProjectsSharedTreeAndDialogFontResource()
    {
        AppearanceResourceProjection projection = AppearancePreferenceRules.ProjectResources(new() { UiScale = 1.5 }, false, false);
        Assert.Equal(19.5, projection.ApplicationFontSize);
        Assert.Equal(projection.ApplicationFontSize, projection.TreeFontSize);
        Assert.Equal(projection.ApplicationFontSize, projection.DialogFontSize);
        Assert.Equal(51, projection.TreeItemMinHeight);
    }

    [Fact]
    public void RuntimeSystemTransitionReprojectsWithoutRestart()
    {
        var preferences = new AppearancePreferences { Theme = CanopyThemeMode.FollowSystem,
            Palette = TreemapPalette.TritanopiaSafe, Motion = MotionPreference.Reduced };
        AppearanceResourceProjection dark = AppearancePreferenceRules.ProjectResources(preferences, false, false);
        AppearanceResourceProjection light = AppearancePreferenceRules.ProjectResources(preferences, true, false);
        Assert.Equal(ResolvedCanopyTheme.Dark, dark.Theme); Assert.Equal(ResolvedCanopyTheme.Light, light.Theme);
        Assert.Equal(dark.TreemapPalette, light.TreemapPalette);
        Assert.Equal(TimeSpan.Zero, light.TreemapTransitionDuration);
    }

    [Fact]
    public void RuntimePreferenceChangeUpdatesTreemapResourcesWithoutRestart()
    {
        AppearanceResourceProjection before = AppearancePreferenceRules.ProjectResources(new(), false, false);
        AppearanceResourceProjection after = AppearancePreferenceRules.ProjectResources(new()
        { Palette = TreemapPalette.Monochrome, Motion = MotionPreference.Reduced }, false, false);
        Assert.NotEqual(before.TreemapPalette, after.TreemapPalette);
        Assert.NotEqual(before.TreemapTransitionDuration, after.TreemapTransitionDuration);
        Assert.Equal(TreemapPalette.Monochrome, after.TreemapPalette);
        Assert.Equal(TimeSpan.Zero, after.TreemapTransitionDuration);
    }

    static string TemporaryDirectory() { string path = Path.Combine(Path.GetTempPath(), "canopy-appearance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
