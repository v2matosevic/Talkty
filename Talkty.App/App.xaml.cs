using System.Threading;
using System.Windows;
using Talkty.App.Services;

namespace Talkty.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static bool _ownsMutex;

    /// <summary>
    /// Restarts the application (used after installing the CUDA pack — the Whisper native
    /// runtime is chosen at first load and can't be swapped in-process). A detached cmd
    /// waits ~1s (ping trick) so this instance fully exits and releases the single-instance
    /// mutex before the new one starts.
    /// </summary>
    public static void Restart()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Error("Restart failed: cannot determine executable path");
                return;
            }

            Log.Info("Restarting application (CUDA pack activation)");
            // --restarted makes the new instance WAIT on the single-instance mutex instead
            // of exiting if this instance's teardown (native model dispose) outlives the delay.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping -n 2 127.0.0.1 >nul & start \"\" \"{exePath}\" --restarted",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restart application", ex);
        }
    }

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
            // Self-restart (CUDA pack activation): the old instance may still be tearing
            // down a multi-GB native model when we launch — wait for its mutex instead of
            // bailing, or the restart race leaves the user with NO running instance.
            var isRestart = e.Args.Contains("--restarted");
            var acquired = false;
            if (isRestart)
            {
                Log.Info("Restarted instance — waiting up to 10s for the old instance to exit");
                try
                {
                    acquired = _mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    // Old instance died without releasing — the mutex is ours now.
                    acquired = true;
                }
            }

            if (!acquired)
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
        Log.Shutdown();
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
