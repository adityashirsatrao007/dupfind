using System.Windows;
using System.Windows.Threading;
using DupFind.App.Services;

namespace DupFind.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                ErrorLog.Write(exception, "app domain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(args.Exception, "unobserved task");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);

        MessageBox.Show(
            $"DupFind hit an unexpected error and kept running.\n\n{e.Exception.Message}\n\nDetails: {ErrorLog.LogPath}",
            "DupFind",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
