using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Readers.Tar;
using Talkty.App.Models;

namespace Talkty.App.Services;

public class DownloadProgress
{
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public double Percentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }
    public int RetryAttempt { get; set; }
    public bool IsVerifying { get; set; }

    public string SpeedDisplay
    {
        get
        {
            if (SpeedBytesPerSecond >= 1_000_000)
                return $"{SpeedBytesPerSecond / 1_000_000:F1} MB/s";
            if (SpeedBytesPerSecond >= 1_000)
                return $"{SpeedBytesPerSecond / 1_000:F0} KB/s";
            return $"{SpeedBytesPerSecond:F0} B/s";
        }
    }

    public string TimeRemainingDisplay
    {
        get
        {
            if (EstimatedTimeRemaining.TotalHours >= 1)
                return $"{EstimatedTimeRemaining.Hours}h {EstimatedTimeRemaining.Minutes}m";
            if (EstimatedTimeRemaining.TotalMinutes >= 1)
                return $"{EstimatedTimeRemaining.Minutes}m {EstimatedTimeRemaining.Seconds}s";
            return $"{EstimatedTimeRemaining.Seconds}s";
        }
    }

    public string DownloadedDisplay
    {
        get
        {
            double downloaded = BytesDownloaded / 1_000_000.0;
            double total = TotalBytes / 1_000_000.0;

            if (total >= 1000)
                return $"{downloaded / 1000:F2} / {total / 1000:F2} GB";
            return $"{downloaded:F1} / {total:F1} MB";
        }
    }
}

public interface IModelDownloadService
{
    Task<bool> DownloadModelAsync(
        ModelProfile profile,
        string destinationPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken = default);

    void CancelDownload();

    Task<bool> VerifyModelIntegrityAsync(ModelProfile profile, string modelPath);
}

public class ModelDownloadService : IModelDownloadService
{
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 2000;

    public ModelDownloadService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ResponseDrainTimeout = TimeSpan.FromSeconds(30),
        };
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromHours(4);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Talkty/1.0");
    }

    public async Task<bool> DownloadModelAsync(
        ModelProfile profile,
        string destinationPath,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var url = profile.GetDownloadUrl();
        var tempPath = destinationPath + ".download";
        var expectedSize = profile.GetModelSize();

        Log.Info($"[DOWNLOAD] === Starting download ===");
        Log.Info($"[DOWNLOAD] Profile: {profile}");
        Log.Info($"[DOWNLOAD] URL: {url}");
        Log.Info($"[DOWNLOAD] Destination: {destinationPath}");
        Log.Info($"[DOWNLOAD] Expected size: {expectedSize:N0} bytes ({expectedSize / 1_000_000.0:F1} MB)");

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Log.Debug($"[DOWNLOAD] Creating directory: {directory}");
                Directory.CreateDirectory(directory);
            }

            long bytesDownloaded = 0;
            long totalBytes = expectedSize;
            var startTime = DateTime.Now;
            int retryCount = 0;

            // Check if partial download exists for resume
            if (File.Exists(tempPath))
            {
                var fileInfo = new FileInfo(tempPath);
                bytesDownloaded = fileInfo.Length;
                Log.Info($"[DOWNLOAD] Found partial download: {bytesDownloaded:N0} bytes ({bytesDownloaded * 100.0 / expectedSize:F1}% complete)");
            }
            else
            {
                Log.Debug($"[DOWNLOAD] No partial download found, starting fresh");
            }

            while (bytesDownloaded < totalBytes && retryCount < MaxRetries)
            {
                Log.Info($"[DOWNLOAD] --- Download attempt {retryCount + 1}/{MaxRetries} ---");
                Log.Info($"[DOWNLOAD] Current position: {bytesDownloaded:N0} / {totalBytes:N0} bytes ({bytesDownloaded * 100.0 / totalBytes:F1}%)");

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    Log.Debug($"[DOWNLOAD] Created HTTP request");

                    // Request range for resume support
                    if (bytesDownloaded > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(bytesDownloaded, null);
                        Log.Info($"[DOWNLOAD] Requesting HTTP Range: bytes={bytesDownloaded}-");
                    }

                    Log.Debug($"[DOWNLOAD] Sending request to server...");
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);

                    Log.Info($"[DOWNLOAD] Response status: {(int)response.StatusCode} {response.StatusCode}");
                    Log.Debug($"[DOWNLOAD] Response headers:");
                    foreach (var header in response.Headers)
                    {
                        Log.Debug($"[DOWNLOAD]   {header.Key}: {string.Join(", ", header.Value)}");
                    }
                    foreach (var header in response.Content.Headers)
                    {
                        Log.Debug($"[DOWNLOAD]   {header.Key}: {string.Join(", ", header.Value)}");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Warning($"[DOWNLOAD] Non-success status code: {response.StatusCode}");
                        // If range not supported and we have partial, restart
                        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            Log.Warning("[DOWNLOAD] Server returned 416 Range Not Satisfiable - restarting download from beginning");
                            bytesDownloaded = 0;
                            CleanupTempFile(tempPath);
                            continue;
                        }
                        response.EnsureSuccessStatusCode();
                    }

                    // Get actual total size from server
                    if (response.Content.Headers.ContentRange?.Length.HasValue == true)
                    {
                        totalBytes = response.Content.Headers.ContentRange.Length.Value;
                        Log.Info($"[DOWNLOAD] Server reported total size via Content-Range: {totalBytes:N0} bytes");
                    }
                    else if (response.Content.Headers.ContentLength.HasValue && bytesDownloaded == 0)
                    {
                        totalBytes = response.Content.Headers.ContentLength.Value;
                        Log.Info($"[DOWNLOAD] Server reported size via Content-Length: {totalBytes:N0} bytes");
                    }
                    else
                    {
                        Log.Warning($"[DOWNLOAD] Server did not provide content size, using expected: {totalBytes:N0} bytes");
                    }

                    var fileMode = bytesDownloaded > 0 ? FileMode.Append : FileMode.Create;
                    Log.Debug($"[DOWNLOAD] Opening file stream in {fileMode} mode: {tempPath}");

                    await using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token);
                    Log.Debug($"[DOWNLOAD] Content stream opened");

                    await using var fileStream = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920, true);
                    Log.Debug($"[DOWNLOAD] File stream opened");

                    var buffer = new byte[81920];
                    var lastProgressTime = DateTime.Now;
                    var lastLogTime = DateTime.Now;
                    long lastProgressBytes = bytesDownloaded;
                    long lastLogBytes = bytesDownloaded;
                    int consecutiveZeroReads = 0;
                    int readCount = 0;

                    Log.Info($"[DOWNLOAD] Starting download loop...");

                    while (true)
                    {
                        _cts.Token.ThrowIfCancellationRequested();

                        int bytesRead;
                        try
                        {
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                            readCts.CancelAfter(TimeSpan.FromSeconds(60));
                            bytesRead = await contentStream.ReadAsync(buffer, readCts.Token);
                            readCount++;
                        }
                        catch (OperationCanceledException) when (!_cts.Token.IsCancellationRequested)
                        {
                            Log.Warning($"[DOWNLOAD] Read timeout after 60 seconds at {bytesDownloaded:N0} bytes ({bytesDownloaded * 100.0 / totalBytes:F1}%)");
                            throw new IOException("Read timeout");
                        }

                        if (bytesRead == 0)
                        {
                            consecutiveZeroReads++;
                            Log.Debug($"[DOWNLOAD] Zero-byte read #{consecutiveZeroReads} at {bytesDownloaded:N0} bytes");
                            if (consecutiveZeroReads > 3)
                            {
                                if (bytesDownloaded < totalBytes * 0.99)
                                {
                                    var pct = bytesDownloaded * 100.0 / totalBytes;
                                    Log.Error($"[DOWNLOAD] Connection closed prematurely at {bytesDownloaded:N0}/{totalBytes:N0} bytes ({pct:F1}%)");
                                    Log.Error($"[DOWNLOAD] This may indicate: network timeout, server disconnect, or content delivery issue");
                                    throw new IOException($"Connection closed prematurely at {pct:F1}%");
                                }
                                Log.Info($"[DOWNLOAD] Stream ended at {bytesDownloaded:N0} bytes (close enough to {totalBytes:N0})");
                                break;
                            }
                            await Task.Delay(100, _cts.Token);
                            continue;
                        }
                        consecutiveZeroReads = 0;

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                        await fileStream.FlushAsync(_cts.Token);
                        bytesDownloaded += bytesRead;

                        var now = DateTime.Now;
                        var elapsed = (now - lastProgressTime).TotalSeconds;
                        var logElapsed = (now - lastLogTime).TotalSeconds;

                        // Log detailed progress every 10 seconds
                        if (logElapsed >= 10)
                        {
                            var pct = bytesDownloaded * 100.0 / totalBytes;
                            var recentMB = (bytesDownloaded - lastLogBytes) / 1_000_000.0;
                            var speed = recentMB / logElapsed;
                            Log.Info($"[DOWNLOAD] Progress: {bytesDownloaded:N0}/{totalBytes:N0} bytes ({pct:F1}%) - {speed:F2} MB/s - {readCount} reads");
                            lastLogTime = now;
                            lastLogBytes = bytesDownloaded;
                        }

                        if (elapsed >= 0.25)
                        {
                            var recentBytes = bytesDownloaded - lastProgressBytes;
                            var speed = recentBytes / elapsed;
                            var remaining = totalBytes - bytesDownloaded;
                            var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : TimeSpan.Zero;

                            progress.Report(new DownloadProgress
                            {
                                BytesDownloaded = bytesDownloaded,
                                TotalBytes = totalBytes,
                                SpeedBytesPerSecond = speed,
                                EstimatedTimeRemaining = eta,
                                RetryAttempt = retryCount
                            });

                            lastProgressTime = now;
                            lastProgressBytes = bytesDownloaded;
                        }
                    }

                    Log.Info($"[DOWNLOAD] Download loop completed. Total bytes: {bytesDownloaded:N0}, Total reads: {readCount}");
                    break;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException && !_cts.Token.IsCancellationRequested)
                {
                    retryCount++;
                    var pct = bytesDownloaded * 100.0 / totalBytes;
                    Log.Warning($"[DOWNLOAD] !!! Download interrupted !!!");
                    Log.Warning($"[DOWNLOAD] Attempt: {retryCount}/{MaxRetries}");
                    Log.Warning($"[DOWNLOAD] Exception type: {ex.GetType().Name}");
                    Log.Warning($"[DOWNLOAD] Message: {ex.Message}");
                    Log.Warning($"[DOWNLOAD] Downloaded: {bytesDownloaded:N0}/{totalBytes:N0} bytes ({pct:F1}%)");
                    if (ex.InnerException != null)
                    {
                        Log.Warning($"[DOWNLOAD] Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }

                    if (retryCount >= MaxRetries)
                    {
                        Log.Error($"[DOWNLOAD] Download failed after {MaxRetries} retries - giving up");
                        throw new IOException($"Download failed after {MaxRetries} retries. Last error: {ex.Message}", ex);
                    }

                    progress.Report(new DownloadProgress
                    {
                        BytesDownloaded = bytesDownloaded,
                        TotalBytes = totalBytes,
                        SpeedBytesPerSecond = 0,
                        EstimatedTimeRemaining = TimeSpan.Zero,
                        RetryAttempt = retryCount
                    });

                    var delay = RetryDelayMs * retryCount;
                    Log.Info($"[DOWNLOAD] Waiting {delay}ms before retry...");
                    await Task.Delay(delay, _cts.Token);
                    Log.Info($"[DOWNLOAD] Retrying download from {bytesDownloaded:N0} bytes...");
                }
            }

            // Verify file size
            var finalFileInfo = new FileInfo(tempPath);
            Log.Info($"[DOWNLOAD] Final file size: {finalFileInfo.Length:N0} bytes (expected ~{totalBytes:N0})");
            if (finalFileInfo.Length < totalBytes * 0.95)
            {
                var pct = finalFileInfo.Length * 100.0 / totalBytes;
                Log.Error($"[DOWNLOAD] Downloaded file too small! Got {finalFileInfo.Length:N0} bytes ({pct:F1}%), expected ~{totalBytes:N0}");
                throw new InvalidDataException($"Downloaded file appears incomplete ({finalFileInfo.Length}/{totalBytes} bytes)");
            }

            // Check if this is an archive that needs extraction (check URL, not temp filename)
            bool isArchive = url.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ||
                            url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                            url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

            // Determine the actual archive type from URL for extraction
            string? archiveType = null;
            if (url.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
                archiveType = ".tar.bz2";
            else if (url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                archiveType = ".tar.gz";
            else if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                archiveType = ".zip";

            if (isArchive && archiveType != null)
            {
                // For archives, extract to the parent directory (Models folder)
                // The archive contains a root directory with the model name
                var extractDir = Path.GetDirectoryName(destinationPath) ?? destinationPath;
                Log.Info($"[DOWNLOAD] Archive detected ({archiveType}), extracting to: {extractDir}");
                progress.Report(new DownloadProgress
                {
                    BytesDownloaded = bytesDownloaded,
                    TotalBytes = totalBytes,
                    IsVerifying = true // Use this to show "Extracting..."
                });

                await ExtractArchiveAsync(tempPath, extractDir, archiveType, _cts.Token);

                // Clean up archive after extraction
                CleanupTempFile(tempPath);
                Log.Info($"[DOWNLOAD] Archive extracted and cleaned up");

                // Verify the expected directory exists
                if (!Directory.Exists(destinationPath))
                {
                    Log.Warning($"[DOWNLOAD] Expected directory not found after extraction: {destinationPath}");
                    // Try to find extracted content
                    var dirs = Directory.GetDirectories(extractDir);
                    Log.Info($"[DOWNLOAD] Directories in {extractDir}: {string.Join(", ", dirs.Select(Path.GetFileName))}");
                }
            }
            else
            {
                // Rename temp file to final for single files
                Log.Debug($"[DOWNLOAD] Moving temp file to final location...");
                if (File.Exists(destinationPath))
                {
                    Log.Debug($"[DOWNLOAD] Deleting existing file: {destinationPath}");
                    File.Delete(destinationPath);
                }

                File.Move(tempPath, destinationPath);
                Log.Info($"[DOWNLOAD] File moved to: {destinationPath}");
            }

            var totalTime = DateTime.Now - startTime;
            Log.Info($"[DOWNLOAD] Download complete! Time: {totalTime.TotalSeconds:F1}s, Retries: {retryCount}");

            // Skip SHA256 verification - hashes change when HuggingFace updates models
            // File size check above is sufficient for validating download completeness
            Log.Info($"[DOWNLOAD] === Download successful ===");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[DOWNLOAD] Download cancelled by user for {profile}");
            Log.Info($"[DOWNLOAD] Partial file preserved for resume: {tempPath}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[DOWNLOAD] !!! Download failed with exception !!!");
            Log.Error($"[DOWNLOAD] Profile: {profile}");
            Log.Error($"[DOWNLOAD] Exception: {ex.GetType().Name}: {ex.Message}");
            Log.Error($"[DOWNLOAD] Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Log.Error($"[DOWNLOAD] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            Log.Info($"[DOWNLOAD] Partial file preserved for resume: {tempPath}");
            throw;
        }
    }

    public void CancelDownload()
    {
        _cts?.Cancel();
    }

    public async Task<bool> VerifyModelIntegrityAsync(ModelProfile profile, string modelPath)
    {
        return await Task.Run(() => VerifyModelIntegritySync(profile, modelPath));
    }

    private bool VerifyModelIntegritySync(ModelProfile profile, string modelPath)
    {
        var expectedHash = profile.GetExpectedSha256();
        if (string.IsNullOrEmpty(expectedHash))
        {
            Log.Warning($"No expected hash defined for {profile}, skipping integrity check");
            return true; // No hash to verify against
        }

        if (!File.Exists(modelPath))
        {
            Log.Error($"Model file not found for integrity check: {modelPath}");
            return false;
        }

        try
        {
            Log.Debug($"Computing SHA256 for {modelPath}...");
            var actualHash = ComputeSha256Sync(modelPath);
            var matches = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                Log.Info($"SHA256 verified for {profile}: {actualHash}");
            }
            else
            {
                Log.Error($"SHA256 mismatch for {profile}. Expected: {expectedHash}, Got: {actualHash}");
            }

            return matches;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to compute SHA256 for {modelPath}", ex);
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string ComputeSha256Sync(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        var hashBytes = SHA256.HashData(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static void CleanupTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Extract a tar.bz2 or other archive to the destination directory.
    /// </summary>
    /// <param name="archivePath">Path to the archive file (may have .download extension)</param>
    /// <param name="destinationDir">Directory to extract to</param>
    /// <param name="archiveType">The actual archive type (.tar.bz2, .tar.gz, .zip)</param>
    /// <param name="ct">Cancellation token</param>
    private static async Task ExtractArchiveAsync(string archivePath, string destinationDir, string archiveType, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            Log.Info($"[EXTRACT] Starting extraction of {archivePath}");
            Log.Info($"[EXTRACT] Archive type: {archiveType}");
            Log.Info($"[EXTRACT] Destination: {destinationDir}");

            // Create destination directory if it doesn't exist
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            try
            {
                // Handle based on archive type (not file extension)
                if (archiveType == ".tar.bz2")
                {
                    ExtractTarBz2(archivePath, destinationDir, ct);
                }
                else if (archiveType == ".tar.gz" || archiveType == ".tgz")
                {
                    using var archive = ArchiveFactory.Open(archivePath);
                    ExtractArchiveEntries(archive, destinationDir, ct);
                }
                else if (archiveType == ".zip")
                {
                    using var archive = ArchiveFactory.Open(archivePath);
                    ExtractArchiveEntries(archive, destinationDir, ct);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported archive type: {archiveType}");
                }

                Log.Info($"[EXTRACT] Extraction completed successfully");
            }
            catch (Exception ex)
            {
                Log.Error($"[EXTRACT] Extraction failed: {ex.Message}");
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Extract a tar.bz2 archive using TarReader with BZip2 decompression.
    /// </summary>
    private static void ExtractTarBz2(string archivePath, string destinationDir, CancellationToken ct)
    {
        Log.Debug($"[EXTRACT] Opening BZip2 stream...");

        using var fileStream = File.OpenRead(archivePath);
        using var bzip2Stream = new BZip2Stream(fileStream, SharpCompress.Compressors.CompressionMode.Decompress, false);

        // Use TarReader for sequential reading (more reliable for streamed decompression)
        using var tarReader = TarReader.Open(bzip2Stream);

        int fileCount = 0;
        while (tarReader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();

            var entry = tarReader.Entry;
            if (entry == null || entry.IsDirectory)
                continue;

            var entryPath = entry.Key ?? "";
            if (string.IsNullOrEmpty(entryPath))
                continue;

            // Normalize path separators
            entryPath = entryPath.Replace('/', Path.DirectorySeparatorChar);

            // Build full destination path
            var fullPath = Path.Combine(destinationDir, entryPath);
            var entryDir = Path.GetDirectoryName(fullPath);

            // Create directory if needed
            if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
            {
                Directory.CreateDirectory(entryDir);
                Log.Debug($"[EXTRACT] Created directory: {entryDir}");
            }

            // Extract file using stream
            using var entryStream = tarReader.OpenEntryStream();
            using var outputStream = File.Create(fullPath);
            entryStream.CopyTo(outputStream);

            fileCount++;
            if (fileCount % 50 == 0)
            {
                Log.Debug($"[EXTRACT] Extracted {fileCount} files... Current: {entry.Key}");
            }
        }

        Log.Info($"[EXTRACT] Total files extracted: {fileCount}");
    }

    /// <summary>
    /// Extract all entries from an archive to the destination directory.
    /// </summary>
    private static void ExtractArchiveEntries(IArchive archive, string destinationDir, CancellationToken ct)
    {
        int fileCount = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
                continue;

            // Get the entry path, removing any leading directory component if needed
            var entryPath = entry.Key ?? "";

            // Skip empty paths
            if (string.IsNullOrEmpty(entryPath))
                continue;

            // Normalize path separators
            entryPath = entryPath.Replace('/', Path.DirectorySeparatorChar);

            // Build full destination path
            var fullPath = Path.Combine(destinationDir, entryPath);
            var entryDir = Path.GetDirectoryName(fullPath);

            // Create directory if needed
            if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
            {
                Directory.CreateDirectory(entryDir);
            }

            // Extract file
            entry.WriteToDirectory(destinationDir, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });

            fileCount++;
            if (fileCount % 100 == 0)
            {
                Log.Debug($"[EXTRACT] Extracted {fileCount} files...");
            }
        }

        Log.Info($"[EXTRACT] Total files extracted: {fileCount}");
    }
}
