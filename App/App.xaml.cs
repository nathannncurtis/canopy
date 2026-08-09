using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SizeMonitor.Helpers;
using SizeMonitor.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SizeMonitor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
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
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: false);

        new MainWindow().Show();
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
