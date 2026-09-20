using System.Diagnostics;
using System.IO;

namespace Talkty.App.Services;

/// <summary>
/// Starts the local command daemon when it is not there.
///
/// Alt+W is how this machine is used hands-free, so "no daemon is listening" is
/// not an answer: the daemon writes a start-up shim into the ordinary Startup
/// folder (`hermes voice autostart install`), and running that shim is how this
/// app puts it back without knowing anything about Node, paths or ports.
///
/// One attempt at a time, and never more than one daemon: whichever process
/// binds the port publishes the service record, and the rest stop by themselves.
/// </summary>
public class VoiceDaemonLauncher
{
    private readonly Func<string, bool> _exists;
    private readonly Action<string, string> _run;
    private DateTime _lastAttempt = DateTime.MinValue;

    /// <summary>Do not thrash: a failing start is not retried for this long.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(20);

    public VoiceDaemonLauncher(Func<string, bool>? exists = null, Action<string, string>? run = null)
    {
        _exists = exists ?? File.Exists;
        _run = run ?? Launch;
    }

    public static string ShimPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Hermes voice daemon.vbs");

    /// <summary>
    /// Ask the daemon to start. True when a start is under way: either this call
    /// asked for one, or one was asked for moments ago and may still be coming,
    /// so a caller that gets true can go on to wait for it to announce itself.
    /// False only when there is nothing to run.
    /// </summary>
    public bool TryStart(DateTime? now = null)
    {
        var moment = now ?? DateTime.UtcNow;
        var shim = ShimPath;
        if (moment - _lastAttempt < Cooldown) return _exists(shim);
        if (!_exists(shim))
        {
            Log.Warning($"No voice daemon start-up entry at {shim}; run `hermes voice autostart install`.");
            return false;
        }
        _lastAttempt = moment;
        try
        {
            _run("wscript.exe", $"//B //Nologo \"{shim}\"");
            Log.Info("Asked the voice daemon to start.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"The voice daemon could not be started: {ex.Message}");
            return false;
        }
    }

    private static void Launch(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
