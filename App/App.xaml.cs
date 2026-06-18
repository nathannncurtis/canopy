using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SizeMonitor.Helpers;
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

        Logger.Info($"startup — OS {Environment.OSVersion}, .NET {Environment.Version}");

        var accent = Color.FromArgb(0xFF, 0x4C, 0x9D, 0xFF);
        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark, systemGlassColor: false, systemAccentColor: false);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: false);

        new MainWindow().Show();
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("unhandled UI exception", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred.\n\nLog: {Logger.LogPath}\n\n{e.Exception.Message}",
            "Canopy", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Logger.Error("unhandled domain exception (terminating)", ex);
    }
}
