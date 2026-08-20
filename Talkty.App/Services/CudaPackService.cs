using System.IO;
using System.IO.Compression;
using System.Net.Http;

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
/// and no elevation is needed. <see cref="Engines.WhisperEngine"/> probes those exact paths,
/// so after a restart CUDA is picked up with zero further configuration.
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

    /// <summary>Relative paths (from the app root) that must exist for CUDA to load.</summary>
    private static readonly string[] RequiredFiles =
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
    public async Task<(bool Success, string Message)> DownloadAndInstallAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"TalktyCudaPack_{Guid.NewGuid():N}.zip");
        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"TalktyCudaPack_{Guid.NewGuid():N}");

        try
        {
            Log.Info($"CUDA pack download starting: {PackUrl}");

            using (var response = await HttpClient.GetAsync(
                       PackUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = File.Create(tempZip);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;
                    if (totalBytes > 0)
                        progress?.Report((double)downloaded / totalBytes);
                }
            }

            var zipSize = new FileInfo(tempZip).Length;
            if (zipSize < MinPackBytes)
            {
                Log.Error($"CUDA pack download suspiciously small: {zipSize} bytes");
                return (false, "Download looks incomplete — please try again.");
            }
            Log.Info($"CUDA pack downloaded: {zipSize / (1024.0 * 1024.0):F0} MB");

            // Extract to temp first so a half-written pack never lands in the app directory.
            ZipFile.ExtractToDirectory(tempZip, tempExtractDir);

            foreach (var required in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(tempExtractDir, required)))
                {
                    Log.Error($"CUDA pack is missing expected file: {required}");
                    return (false, "The downloaded pack is invalid — please try again later.");
                }
            }

            foreach (var source in Directory.EnumerateFiles(tempExtractDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(tempExtractDir, source);
                var destination = Path.Combine(AppDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            Log.Info("CUDA pack installed into app directory");
            return (true, "CUDA pack installed — restart Talkty to activate it.");
        }
        catch (OperationCanceledException)
        {
            Log.Info("CUDA pack download cancelled");
            return (false, "Download cancelled.");
        }
        catch (HttpRequestException ex)
        {
            Log.Error("CUDA pack download failed (network)", ex);
            return (false, "Download failed — check your internet connection and try again.");
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
            TryDelete(tempZip);
            TryDeleteDirectory(tempExtractDir);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp cleanup, best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
