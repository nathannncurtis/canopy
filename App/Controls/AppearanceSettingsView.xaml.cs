using System.IO;
using System.Windows;
using System.Windows.Controls;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;

namespace SizeMonitor.Controls;

public partial class AppearanceSettingsView : UserControl
{
    readonly AppearancePreferenceStore _store = new(AppDataPaths.AppearancePreferences);
    public event Action<AppearancePreferences>? PreferencesApplied;
    public AppearanceSettingsView() { InitializeComponent(); Loaded += OnLoaded; }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { SetControls(await _store.LoadAsync()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _status.Text = $"Appearance settings could not be loaded: {ex.Message}"; }
    }

    async void OnSave(object sender, RoutedEventArgs e)
    {
        AppearancePreferences preferences = ReadControls();
        try
        {
            await _store.SaveAsync(preferences);
            PreferencesApplied?.Invoke(preferences);
            _status.Text = "Appearance and accessibility settings were saved and applied.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _status.Text = $"Appearance settings were not saved: {ex.Message}"; }
    }

    AppearancePreferences ReadControls() => new()
    {
        Theme = (CanopyThemeMode)_theme.SelectedIndex,
        Palette = (TreemapPalette)_palette.SelectedIndex,
        Density = (InterfaceDensity)_density.SelectedIndex,
        Motion = (MotionPreference)_motion.SelectedIndex,
        UiScale = _scale.Value,
    };

    void SetControls(AppearancePreferences value)
    {
        _theme.SelectedIndex = (int)value.Theme; _palette.SelectedIndex = (int)value.Palette;
        _density.SelectedIndex = (int)value.Density; _motion.SelectedIndex = (int)value.Motion;
        _scale.Value = value.UiScale;
    }
}
