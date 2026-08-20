using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Talkty.App.Services;

/// <summary>
/// Downloads and installs the optional CUDA runtime pack.
///
/// WHY: bundling CUDA made the installer ~484MB when the CUDA DLLs (~578MB uncompressed,
/// cublasLt alone is 458MB) only benefit NVIDIA users — who still get GPU acceleration out
/// of the box through the bundled Vulkan backend. So the installer ships without CUDA and
/// the app offers this pack as an in-app download when an NVIDIA GPU is present.
///
/// The pack zip mirrors the install layout the fat build used (see the csproj CopyCudaDlls
/// targets): cublas/cudart DLLs in the app root, ggml CUDA natives in runtimes\cuda\win-x64.
/// Talkty installs per-user (PrivilegesRequired=lowest), so the app directory is writable
/// and no elevation is needed. <see cref="Engines.WhisperEngine"/> probes the same
/// <see cref="RequiredFiles"/> manifest, so after a restart CUDA is picked up with zero
/// further configuration.
///
/// Install is staged ON THE SAME VOLUME as the app (".cuda-pack-tmp-*" next to the exe):
/// the zip downloads and extracts there, then each file lands via File.Move — a rename,
/// never a partial copy — so a crash mid-install can leave a file absent but never
/// truncated. (A truncated cublasLt would pass the existence checks and select the CUDA
/// runtime "no fallback", bricking transcription with no in-app repair path.)
/// </summary>
public class CudaPackService
{
    /// <summary>
    /// Release asset holding the pack. Lives on a dedicated, version-independent release tag
    /// so app releases don't have to re-upload ~450MB each time.
    /// </summary>
    private const string PackUrl =
        "https://github.com/v2matosevic/Talkty/releases/download/cuda-pack-cu13/TalktyCudaPack-cu13-win-x64.zip";

    /// <summary>Sanity floor for the downloaded zip — a 404 page or stub is far smaller.</summary>
    private const long MinPackBytes = 100L * 1024 * 1024;

    private const int MaxDownloadAttempts = 4;

    /// <summary>
    /// THE manifest of CUDA runtime files, relative to the app root. Single source of truth
    /// inside the app: <see cref="Engines.WhisperEngine"/> checks the same list to decide
    /// whether the CUDA runtime can be selected. (The csproj CopyCudaDlls targets and
    /// installer/make-cuda-pack.ps1 mirror it — keep them in sync on a CUDA version bump.)
    /// </summary>
    public static readonly string[] RequiredFiles =
    {
        "cublas64_13.dll",
        "cublasLt64_13.dll",
        "cudart64_13.dll",
        Path.Combine("runtimes", "cuda", "win-x64", "ggml-cuda-whisper.dll"),
    };

    private static readonly HttpClient HttpClient = new()
    {
        // No overall timeout — this is a large download; cancellation handles abandonment.
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static string AppDirectory => AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>True when every CUDA runtime file is already in place.</summary>
    public bool IsCudaInstalled =>
        RequiredFiles.All(f => File.Exists(Path.Combine(AppDirectory, f)));

    /// <summary>
    /// True when an NVIDIA driver is present (nvidia-smi ships with the driver).
    /// Cheap file checks only — never shells out.
    /// </summary>
    public bool HasNvidiaGpu
    {
        get
        {
            try
            {
                var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (File.Exists(Path.Combine(system32, "nvidia-smi.exe"))) return true;
                // Driver store fallback: nvml.dll is installed by every modern NVIDIA driver.
                return File.Exists(Path.Combine(system32, "nvml.dll"));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Downloads the pack, verifies it, and installs it into the app directory.
    /// Progress is 0..1. Returns (success, user-facing message).
    /// </summary>
    /// <summary>Serializes installs process-wide — two racing installers interleaving moves
    /// over the same DLLs must be impossible, not just unlikely.</summary>
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    /// <summary>
    /// Stable staging location (same volume as the app). A partial zip left by a cancelled
    /// or failed download is resumed by the next attempt instead of restarting ~450MB.
    /// </summary>
    private static string StagingDir => Path.Combine(AppDirectory, ".cuda-pack-tmp");

    public async Task<(bool Success, string Message)> DownloadAndInstallAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await InstallGate.WaitAsync(0, cancellationToken))
            return (false, "A CUDA pack download is already in progress.");

        // Staged on the SAME volume as the install dir: File.Move is then a rename (atomic
        // per file), and we don't need ~1GB of free space on the system drive when the app
        // lives elsewhere.
        var zipPath = Path.Combine(StagingDir, "pack.zip");
        var extractDir = Path.Combine(StagingDir, "extracted");
        var keepZipForResume = false;

        try
        {
            Directory.CreateDirectory(StagingDir);
            TryDeleteDirectory(extractDir); // leftovers from an interrupted earlier run

            try
            {
                await DownloadWithResumeAsync(zipPath, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                keepZipForResume = true;
                Log.Info("CUDA pack download cancelled (partial kept for resume)");
                return (false, "Download cancelled — it will resume where it left off.");
            }
            catch (HttpRequestException ex)
            {
                keepZipForResume = true;
                Log.Error("CUDA pack download failed (network)", ex);
                return (false, "Download failed — check your internet connection and try again.");
            }

            var zipSize = new FileInfo(zipPath).Length;
            if (zipSize < MinPackBytes)
            {
                Log.Error($"CUDA pack download suspiciously small: {zipSize} bytes");
                return (false, "Download looks incomplete — please try again.");
            }
            Log.Info($"CUDA pack downloaded: {zipSize / (1024.0 * 1024.0):F0} MB");

            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir);
            }
            catch (InvalidDataException ex)
            {
                // Corrupt archive (e.g. a stale partial resumed against a replaced asset) —
                // discard it so the next attempt starts clean.
                Log.Error("CUDA pack zip is corrupt — discarding", ex);
                return (false, "The downloaded pack was corrupt — please try again.");
            }

            foreach (var required in RequiredFiles)
            {
                var staged = new FileInfo(Path.Combine(extractDir, required));
                if (!staged.Exists || staged.Length == 0)
                {
                    Log.Error($"CUDA pack is missing expected file: {required}");
                    return (false, "The downloaded pack is invalid — please try again later.");
                }
            }

            foreach (var source in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extractDir, source);
                var destination = Path.Combine(AppDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, overwrite: true);
            }

            Log.Info("CUDA pack installed into app directory");
            return (true, "CUDA pack installed — restart Talkty to activate it.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error("CUDA pack install failed (permissions)", ex);
            return (false, "Couldn't write to the Talkty folder — is it installed somewhere read-only?");
        }
        catch (Exception ex)
        {
            Log.Error("CUDA pack install failed", ex);
            return (false, $"Installation failed: {ex.Message}");
        }
        finally
        {
            if (!keepZipForResume)
                TryDeleteDirectory(StagingDir);
            else
                TryDeleteDirectory(extractDir);
            InstallGate.Release();
        }
    }

    /// <summary>
    /// Streams the pack to <paramref name="zipPath"/>, retrying transient failures with
    /// exponential backoff and resuming via HTTP Range (GitHub release assets support it) —
    /// a blip at 440 of 450MB must not restart the download from zero.
    /// </summary>
    private static async Task DownloadWithResumeAsync(
        string zipPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                long existing = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;

                using var request = new HttpRequestMessage(HttpMethod.Get, PackUrl);
                if (existing > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existing, null);
                    Log.Info($"CUDA pack download resuming at {existing / (1024.0 * 1024.0):F0} MB (attempt {attempt})");
                }
                else
                {
                    Log.Info($"CUDA pack download starting: {PackUrl} (attempt {attempt})");
                }

                using var response = await HttpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                // 416 = our partial file already covers the full asset; nothing left to fetch.
                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    return;

                response.EnsureSuccessStatusCode();

                var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resumed && existing > 0)
                {
                    // Server ignored the Range — start over.
                    existing = 0;
                }

                var totalBytes = (response.Content.Headers.ContentLength ?? -1) + (resumed ? existing : 0);

                await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(
                    zipPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write);

                var buffer = new byte[81920];
                long downloaded = existing;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;
                    if (totalBytes > 0)
                        progress?.Report((double)downloaded / totalBytes);
                }
                return;
            }
            catch (Exception ex) when (attempt < MaxDownloadAttempts
                                       && ex is HttpRequestException or IOException
                                       && !cancellationToken.IsCancellationRequested)
            {
                Log.Warning($"CUDA pack download attempt {attempt} failed ({ex.Message}) — retrying");
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
