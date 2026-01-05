using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Talkty.App.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public static class Log
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Talkty", "Logs");

    private static readonly string LogFile = Path.Combine(
        LogDirectory, $"talkty_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

    private static readonly ConcurrentQueue<string> LogQueue = new();
    private static readonly object WriteLock = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            _initialized = true;

            // Write comprehensive header
            var header = $"""
                ================================================================================
                TALKTY LOG - Started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
                ================================================================================

                --- SYSTEM INFO ---
                Machine: {Environment.MachineName}
                OS: {Environment.OSVersion}
                .NET: {Environment.Version}
                Processors: {Environment.ProcessorCount}
                64-bit OS: {Environment.Is64BitOperatingSystem}
                64-bit Process: {Environment.Is64BitProcess}
                Working Set: {Environment.WorkingSet / 1024 / 1024} MB
                Log File: {LogFile}

                --- GPU DETECTION ---
                {GetGpuInfo()}

                ================================================================================

                """;

            File.WriteAllText(LogFile, header);
            Info("Logging initialized with enhanced diagnostics");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize logging: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets GPU information by checking for NVIDIA drivers and CUDA availability.
    /// </summary>
    private static string GetGpuInfo()
    {
        var sb = new System.Text.StringBuilder();

        try
        {
            // Check for nvidia-smi
            var nvidiaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (File.Exists(nvidiaPath) || ExistsOnPath("nvidia-smi.exe"))
            {
                sb.AppendLine("NVIDIA driver: Detected");

                // Try to get GPU info
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "nvidia-smi",
                        Arguments = "--query-gpu=name,memory.total,driver_version,cuda_version --format=csv,noheader,nounits",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        var output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);

                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            var parts = output.Trim().Split(',');
                            if (parts.Length >= 4)
                            {
                                sb.AppendLine($"GPU: {parts[0].Trim()}");
                                sb.AppendLine($"VRAM: {parts[1].Trim()} MB");
                                sb.AppendLine($"Driver: {parts[2].Trim()}");
                                sb.AppendLine($"CUDA Version: {parts[3].Trim()}");
                            }
                            else
                            {
                                sb.AppendLine($"GPU Info: {output.Trim()}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"GPU query error: {ex.Message}");
                }
            }
            else
            {
                sb.AppendLine("NVIDIA driver: Not detected (nvidia-smi not found)");
            }

            // Check for CUDA runtime in loaded modules
            sb.AppendLine();
            sb.AppendLine("CUDA Runtime DLLs (in runtimes/cuda):");
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var cudaDir = Path.Combine(appDir, "runtimes", "cuda", "win-x64");
            if (Directory.Exists(cudaDir))
            {
                var dlls = Directory.GetFiles(cudaDir, "*.dll");
                foreach (var dll in dlls)
                {
                    var info = new FileInfo(dll);
                    sb.AppendLine($"  {info.Name} ({info.Length / 1024 / 1024:F1} MB)");
                }
            }
            else
            {
                sb.AppendLine($"  Directory not found: {cudaDir}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"GPU detection error: {ex.Message}");
        }

        return sb.ToString();
    }

    private static bool ExistsOnPath(string fileName)
    {
        var values = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var path in values.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(path, fileName);
            if (File.Exists(fullPath))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Log all currently loaded DLLs containing specific keywords.
    /// </summary>
    public static void LogLoadedModules(params string[] keywords)
    {
        try
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var modules = process.Modules;

            Info("=== LOADED MODULES ===");
            foreach (System.Diagnostics.ProcessModule module in modules)
            {
                var name = module.ModuleName.ToLowerInvariant();
                if (keywords.Length == 0 || keywords.Any(k => name.Contains(k.ToLowerInvariant())))
                {
                    Info($"  {module.ModuleName} - {module.FileName}");
                }
            }
            Info("=== END MODULES ===");
        }
        catch (Exception ex)
        {
            Warning($"Failed to enumerate modules: {ex.Message}");
        }
    }

    /// <summary>
    /// Create a performance timer that logs elapsed time on dispose.
    /// </summary>
    public static IDisposable TimeOperation(string operationName)
    {
        return new OperationTimer(operationName);
    }

    private class OperationTimer : IDisposable
    {
        private readonly string _name;
        private readonly DateTime _start;

        public OperationTimer(string name)
        {
            _name = name;
            _start = DateTime.Now;
            Info($"[TIMER] Starting: {name}");
        }

        public void Dispose()
        {
            var elapsed = DateTime.Now - _start;
            Info($"[TIMER] Completed: {_name} in {elapsed.TotalMilliseconds:F1}ms");
        }
    }

    /// <summary>
    /// Write a section header for visual clarity in logs.
    /// </summary>
    public static void Section(string title)
    {
        var line = $"{'=',-60}";
        Write(LogLevel.Info, "", "", "", 0);
        Write(LogLevel.Info, line, "", "", 0);
        Write(LogLevel.Info, $"  {title}", "", "", 0);
        Write(LogLevel.Info, line, "", "", 0);
    }

    public static void Debug(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write(LogLevel.Debug, message, caller, file, line);

    public static void Info(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write(LogLevel.Info, message, caller, file, line);

    public static void Warning(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write(LogLevel.Warning, message, caller, file, line);

    public static void Error(string message, Exception? ex = null,
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var fullMessage = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Write(LogLevel.Error, fullMessage, caller, file, line);
    }

    private static void Write(LogLevel level, string message, string caller, string file, int line)
    {
        if (!_initialized) Initialize();

        var fileName = Path.GetFileName(file);
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var levelStr = level.ToString().ToUpper().PadRight(7);
        var location = $"{fileName}:{line} ({caller})";

        var logLine = $"[{timestamp}] [{levelStr}] [{location}] {message}";

        // Console output with colors
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = level switch
        {
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.White
        };
        Console.WriteLine(logLine);
        Console.ForegroundColor = originalColor;

        // File output
        try
        {
            lock (WriteLock)
            {
                File.AppendAllText(LogFile, logLine + Environment.NewLine);
            }
        }
        catch
        {
            // Ignore file write errors
        }
    }

    public static string GetLogFilePath() => LogFile;

    /// <summary>
    /// Writes a crash log to the application install directory.
    /// This file can be sent to support for troubleshooting.
    /// </summary>
    public static string WriteCrashLog(Exception ex, string context = "Unhandled Exception")
    {
        try
        {
            // Get app install directory (where the exe lives)
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var crashFile = Path.Combine(appDir, $"crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

            var crashReport = $"""
                ================================================================================
                TALKTY CRASH REPORT
                ================================================================================

                Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
                Context: {context}

                --- SYSTEM INFO ---
                Machine: {Environment.MachineName}
                OS: {Environment.OSVersion}
                .NET Version: {Environment.Version}
                Processors: {Environment.ProcessorCount}
                64-bit OS: {Environment.Is64BitOperatingSystem}
                64-bit Process: {Environment.Is64BitProcess}
                Working Set: {Environment.WorkingSet / 1024 / 1024} MB

                --- EXCEPTION ---
                Type: {ex.GetType().FullName}
                Message: {ex.Message}
                Source: {ex.Source}
                TargetSite: {ex.TargetSite}

                --- STACK TRACE ---
                {ex.StackTrace}

                {GetInnerExceptionDetails(ex)}
                --- END OF CRASH REPORT ---
                """;

            File.WriteAllText(crashFile, crashReport);

            // Also log to regular log
            Error($"CRASH LOG WRITTEN: {crashFile}", ex);

            return crashFile;
        }
        catch (Exception writeEx)
        {
            // If we can't write to app dir, try temp
            try
            {
                var tempCrashFile = Path.Combine(Path.GetTempPath(), $"talkty_crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(tempCrashFile, $"Crash: {ex.Message}\n\nStack:\n{ex.StackTrace}\n\nWrite error: {writeEx.Message}");
                return tempCrashFile;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    private static string GetInnerExceptionDetails(Exception ex)
    {
        if (ex.InnerException == null)
            return string.Empty;

        var inner = ex.InnerException;
        var depth = 1;
        var details = new System.Text.StringBuilder();
        details.AppendLine("--- INNER EXCEPTIONS ---");

        while (inner != null && depth <= 5)
        {
            details.AppendLine($"""

                [Inner Exception {depth}]
                Type: {inner.GetType().FullName}
                Message: {inner.Message}
                Stack:
                {inner.StackTrace}
                """);
            inner = inner.InnerException;
            depth++;
        }

        details.AppendLine();
        return details.ToString();
    }
}
