using System.Threading;
using System.Windows;
using Talkty.App.Services;

namespace Talkty.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Initialize logging first
        Log.Initialize();
        Log.Info("=== APPLICATION STARTING ===");

        // Global exception handlers
        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error("UNHANDLED UI EXCEPTION", args.Exception);
            var crashFile = Log.WriteCrashLog(args.Exception, "UI Thread Exception");
            ShowCrashDialog(crashFile);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Error("UNHANDLED DOMAIN EXCEPTION", ex);
            if (ex != null)
            {
                var crashFile = Log.WriteCrashLog(ex, "AppDomain Unhandled Exception");
                ShowCrashDialog(crashFile);
            }
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Error("UNOBSERVED TASK EXCEPTION", args.Exception);
            Log.WriteCrashLog(args.Exception, "Unobserved Task Exception");
            args.SetObserved();
        };

        // Single instance check
        const string mutexName = "TalktyApp_SingleInstance";
        Log.Debug($"Checking single instance mutex: {mutexName}");

        _mutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            Log.Warning("Another instance is already running. Exiting.");
            MessageBox.Show(
                "Talkty is already running. Check your system tray.",
                "Talkty",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        _ownsMutex = true;
        Log.Info("Single instance check passed - mutex acquired");
        Log.Info($"Log file: {Log.GetLogFilePath()}");

        base.OnStartup(e);
        Log.Info("Base startup complete");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("=== APPLICATION EXITING ===");

        if (_mutex != null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                    Log.Debug("Mutex released successfully");
                }
                catch (ApplicationException ex)
                {
                    Log.Warning($"Failed to release mutex (not owned): {ex.Message}");
                }
            }
            _mutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void ShowCrashDialog(string crashFile)
    {
        try
        {
            var message = "Talkty encountered an unexpected error.\n\n";

            if (!string.IsNullOrEmpty(crashFile))
            {
                message += $"A crash report has been saved to:\n{crashFile}\n\n" +
                           "Please send this file to support to help us fix the issue.";
            }
            else
            {
                message += "Unable to save crash report.";
            }

            MessageBox.Show(
                message,
                "Talkty - Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Ignore any errors showing the dialog
        }
    }
}
