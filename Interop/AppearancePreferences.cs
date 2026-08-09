using System.Text.Json;
using System.Text.Json.Serialization;

namespace SizeMonitor.Interop;

public enum CanopyThemeMode { FollowSystem, Dark, Light, HighContrast }
public enum TreemapPalette { Standard, DeuteranopiaSafe, ProtanopiaSafe, TritanopiaSafe, Monochrome }
public enum InterfaceDensity { Compact, Comfortable }
public enum MotionPreference { Full, Reduced }
public enum ResolvedCanopyTheme { Dark, Light, HighContrast }

public sealed record AppearanceResourceProjection(
    ResolvedCanopyTheme Theme, double ApplicationFontSize, double TreeFontSize, double DialogFontSize, double TreeItemMinHeight,
    double ControlHorizontalPadding, double ControlVerticalPadding,
    uint WindowBackgroundArgb, uint WindowForegroundArgb,
    TreemapPalette TreemapPalette, TimeSpan TreemapTransitionDuration);

public sealed record AppearancePreferences
{
    public int Version { get; init; } = 1;
    public CanopyThemeMode Theme { get; init; } = CanopyThemeMode.FollowSystem;
    public TreemapPalette Palette { get; init; } = TreemapPalette.Standard;
    public InterfaceDensity Density { get; init; } = InterfaceDensity.Comfortable;
    public MotionPreference Motion { get; init; } = MotionPreference.Full;
    public double UiScale { get; init; } = 1;
}

public static class AppearancePreferenceRules
{
    static readonly uint[] Standard = [0xFF4C9DFF, 0xFF57B89A, 0xFFE07272, 0xFFC7973E, 0xFF9B72CF, 0xFF4FB3D4, 0xFFD48F4B, 0xFF6BB56B];
    static readonly uint[] Deuteranopia = [0xFF0072B2, 0xFFE69F00, 0xFF56B4E9, 0xFFD55E00, 0xFFF0E442, 0xFFCC79A7, 0xFF009E73, 0xFF6B6B6B];
    static readonly uint[] Protanopia = [0xFF3B5B92, 0xFFE69F00, 0xFF56B4E9, 0xFF009E73, 0xFFF0E442, 0xFF8C6BB1, 0xFF7F7F7F, 0xFFC7A76C];
    static readonly uint[] Tritanopia = [0xFFD55E00, 0xFF009E73, 0xFFCC79A7, 0xFF0072B2, 0xFFE69F00, 0xFF6A994E, 0xFF9B5DE5, 0xFF777777];
    static readonly uint[] Monochrome = [0xFF24527A, 0xFF326891, 0xFF427DA6, 0xFF5592B9, 0xFF6DA7C9, 0xFF89BBD7, 0xFFA7CEE3, 0xFFC7E1EE];

    public static AppearancePreferences Validate(AppearancePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Version != 1 || !Enum.IsDefined(preferences.Theme) ||
            !Enum.IsDefined(preferences.Palette) || !Enum.IsDefined(preferences.Density) ||
            !Enum.IsDefined(preferences.Motion) || !double.IsFinite(preferences.UiScale) ||
            preferences.UiScale is < 0.8 or > 1.5)
            throw new InvalidDataException("Appearance preferences are invalid or unsupported.");
        return preferences;
    }

    public static ResolvedCanopyTheme ResolveTheme(AppearancePreferences preferences,
        bool systemUsesLightTheme, bool systemHighContrast)
    {
        Validate(preferences);
        if (systemHighContrast || preferences.Theme == CanopyThemeMode.HighContrast)
            return ResolvedCanopyTheme.HighContrast;
        return preferences.Theme switch
        {
            CanopyThemeMode.Light => ResolvedCanopyTheme.Light,
            CanopyThemeMode.Dark => ResolvedCanopyTheme.Dark,
            _ => systemUsesLightTheme ? ResolvedCanopyTheme.Light : ResolvedCanopyTheme.Dark,
        };
    }

    public static double EffectiveScale(AppearancePreferences preferences) =>
        Validate(preferences).UiScale * (preferences.Density == InterfaceDensity.Compact ? 0.9 : 1);

    public static AppearanceResourceProjection ProjectResources(AppearancePreferences preferences,
        bool systemUsesLightTheme, bool systemHighContrast,
        uint systemBackgroundArgb = 0xFF000000, uint systemForegroundArgb = 0xFFFFFFFF)
    {
        Validate(preferences);
        ResolvedCanopyTheme theme = ResolveTheme(preferences, systemUsesLightTheme, systemHighContrast);
        (uint background, uint foreground) = theme switch
        {
            ResolvedCanopyTheme.Light => (0xFFF7F7F7, 0xFF1B1B1B),
            ResolvedCanopyTheme.Dark => (0xFF202020, 0xFFF5F5F5),
            _ => (systemBackgroundArgb, systemForegroundArgb),
        };
        bool compact = preferences.Density == InterfaceDensity.Compact;
        double scale = preferences.UiScale;
        return new(theme, 13 * scale, 13 * scale, 13 * scale, (compact ? 26 : 34) * scale,
            (compact ? 6 : 10) * scale, (compact ? 3 : 6) * scale,
            background, foreground, preferences.Palette, TreemapTransitionDuration(preferences.Motion));
    }

    public static IReadOnlyList<uint> TreemapColors(TreemapPalette palette) => palette switch
    {
        TreemapPalette.DeuteranopiaSafe => Deuteranopia,
        TreemapPalette.ProtanopiaSafe => Protanopia,
        TreemapPalette.TritanopiaSafe => Tritanopia,
        TreemapPalette.Monochrome => Monochrome,
        TreemapPalette.Standard => Standard,
        _ => throw new ArgumentOutOfRangeException(nameof(palette)),
    };

    public static TimeSpan TreemapTransitionDuration(MotionPreference motion) => motion switch
    {
        MotionPreference.Full => TimeSpan.FromMilliseconds(160),
        MotionPreference.Reduced => TimeSpan.Zero,
        _ => throw new ArgumentOutOfRangeException(nameof(motion)),
    };
}

public sealed class AppearancePreferenceStore(string path)
{
    const int MaximumBytes = 16 * 1024;
    readonly SemaphoreSlim _gate = new(1, 1);
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<AppearancePreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return new();
            var info = new FileInfo(path); if (info.Length > MaximumBytes) throw new InvalidDataException("Appearance settings file is too large.");
            await using Stream stream = File.OpenRead(path);
            try
            {
                return AppearancePreferenceRules.Validate(await JsonSerializer.DeserializeAsync<AppearancePreferences>(stream, Options, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Appearance settings are empty."));
            }
            catch (JsonException ex) { throw new InvalidDataException("Appearance settings JSON is invalid.", ex); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppearancePreferences preferences, CancellationToken cancellationToken = default)
    {
        AppearancePreferenceRules.Validate(preferences);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                    await JsonSerializer.SerializeAsync(stream, preferences, Options, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, fullPath, true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
        finally { _gate.Release(); }
    }
}
