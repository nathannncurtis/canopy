using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SizeMonitor;

public partial class App : Application
{
    public static AppearancePreferences CurrentAppearance { get; private set; } = new();
    public static event Action<AppearancePreferences>? AppearanceChanged;
    TrayMonitorHost? _tray;
    MainWindow? _window;
    bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        try
        {
            var consentStore = new ObservabilityConsentStore(AppDataPaths.ObservabilityDirectory);
            Logger.Level = consentStore.LoadAsync().GetAwaiter().GetResult().LogLevel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.Level = DiagnosticLogLevel.Error;
            Logger.Error("observability settings could not be loaded", ex);
        }

        Logger.Info($"startup — OS {Environment.OSVersion}, .NET {Environment.Version}");

        try
        {
            CoreCapabilities capabilities = CoreCapabilities.Read();
            Logger.Info($"core ABI {capabilities.AbiVersion}, capabilities {capabilities.Flags}, " +
                        $"limits {capabilities.MaxNodes:N0} nodes/{capabilities.MaxNameBytes:N0} name bytes");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException or CoreCompatibilityException
                                      or System.ComponentModel.Win32Exception)
        {
            Logger.Error("native core validation failed", ex);
            System.Windows.MessageBox.Show(
                $"Canopy cannot start because its native engine is missing or incompatible.\n\n" +
                $"Keep Canopy.exe and Canopy.Core.dll from the same release together.\n\n{ex.Message}",
                "Canopy", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var accent = Color.FromArgb(0xFF, 0x4C, 0x9D, 0xFF);
        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark, systemGlassColor: false, systemAccentColor: false);
        try { CurrentAppearance = await new AppearancePreferenceStore(AppDataPaths.AppearancePreferences).LoadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { Logger.Error("appearance settings could not be loaded", ex); CurrentAppearance = new(); }
        ApplyAppearanceTheme(CurrentAppearance);
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowMainWindow();
        _tray = new TrayMonitorHost(ShowMainWindow, StartDriveScan, ExitAsync);
        await _tray.StartAsync();
    }

    void StartDriveScan(string rootPath)
    {
        ShowMainWindow();
        _window!.StartScanFromTray(rootPath);
    }

    void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closing += (_, args) =>
            {
                if (_exiting) return;
                args.Cancel = true;
                _window.Hide();
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        if (_tray is not null) await _tray.DisposeAsync();
        _window?.Close();
        Shutdown();
    }

    public static void UpdateAppearance(AppearancePreferences preferences)
    {
        CurrentAppearance = AppearancePreferenceRules.Validate(preferences);
        ApplyAppearanceTheme(CurrentAppearance);
        AppearanceChanged?.Invoke(CurrentAppearance);
    }

    static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentAppearance.Theme != CanopyThemeMode.FollowSystem) return;
        Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplyAppearanceTheme(CurrentAppearance);
            AppearanceChanged?.Invoke(CurrentAppearance);
        });
    }

    static void ApplyAppearanceTheme(AppearancePreferences preferences)
    {
        AppearanceResourceProjection projection = AppearancePreferenceRules.ProjectResources(preferences,
            SystemUsesLightTheme(), SystemParameters.HighContrast,
            ToArgb(SystemColors.WindowColor), ToArgb(SystemColors.WindowTextColor));
        ApplicationTheme theme = projection.Theme == ResolvedCanopyTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        if (Current is null) return;
        Current.Resources["Canopy.FontSize"] = projection.ApplicationFontSize;
        Current.Resources["Canopy.TreeFontSize"] = projection.TreeFontSize;
        Current.Resources["Canopy.DialogFontSize"] = projection.DialogFontSize;
        Current.Resources["Canopy.TreeItemMinHeight"] = projection.TreeItemMinHeight;
        Current.Resources["Canopy.ControlPadding"] = new Thickness(
            projection.ControlHorizontalPadding, projection.ControlVerticalPadding,
            projection.ControlHorizontalPadding, projection.ControlVerticalPadding);
        Current.Resources["Canopy.WindowBackgroundBrush"] = Brush(projection.WindowBackgroundArgb);
        Current.Resources["Canopy.WindowForegroundBrush"] = Brush(projection.WindowForegroundArgb);
        Current.Resources[SystemFonts.MessageFontSizeKey] = projection.ApplicationFontSize;
    }

    static uint ToArgb(Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
    static SolidColorBrush Brush(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze(); return brush;
    }

    static bool SystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("unhandled UI exception", e.Exception);
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred.\n\nLog: {Logger.LogPath}\n\n{e.Exception.Message}",
            "Canopy", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Logger.Error("unhandled domain exception (terminating)", ex);
    }
}
