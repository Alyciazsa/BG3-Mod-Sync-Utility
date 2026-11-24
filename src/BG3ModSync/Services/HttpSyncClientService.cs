using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BG3ModSync.Models;

namespace BG3ModSync.Services;

public class HttpSyncClientService
{
    private const int DownloadChunkSize = 1024 * 1024; // 1 MB pooled stream chunk (zero LOH retention, multi-gigabit ready)

    public static string NormalizeBaseHttpUrl(string rawInput, int defaultPort)
    {
        string trimmed = rawInput.Trim().TrimEnd('/');
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(trimmed);
            if (uri.IsDefaultPort && !trimmed.Contains($":{uri.Port}", StringComparison.Ordinal))
            {
                return $"{uri.Scheme}://{uri.Host}:{defaultPort}";
            }
            return trimmed;
        }

        if (trimmed.Contains(':'))
        {
            return $"http://{trimmed}";
        }

        return $"http://{trimmed}:{defaultPort}";
    }

    public async Task<bool> SyncFromHostNginxAsync(
        string baseHttpUrl,
        AppSettings settings,
        List<ModHashEntry> localEntries,
        Action<string> logCallback,
        Action<string, double>? progressCallback = null,
        CancellationToken cancellationToken = default,
        Action<string, double, double>? detailedProgressCallback = null)
    {
        string baseLarian = PathResolverService.GetLarianBasePath(settings);
        string? bg3InstallDir = PathResolverService.GetBg3InstallDir(settings);
        string modsDir = Path.Combine(baseLarian, "Mods");

        var (isDestSsd, destStorageDesc) = PathResolverService.DetectModsStorageMode(modsDir, baseLarian, localEntries);
        int maxConcurrentDownloads = isDestSsd ? 4 : 1;
        int destBufferSize = isDestSsd ? (8 * 1024 * 1024) : (16 * 1024 * 1024);

        using var handler = NetworkDiscoveryService.CreateOptimizedHttpHandler(TimeSpan.FromSeconds(10));
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        string modHashUrl = $"{baseHttpUrl.TrimEnd('/')}/{PathResolverService.ModHashFileName}";
        var lanAdapter = NetworkDiscoveryService.TryGetBestPhysicalLanAdapter();
        string lanBindNote = lanAdapter.HasValue ? $" [Physical LAN Socket Bound: {lanAdapter.Value.AdapterName} ({lanAdapter.Value.LocalIp})]" : "";
        logCallback($"[CLIENT] Connecting to Host Portable Nginx HTTP Server: {modHashUrl}{lanBindNote}");

        string remoteHashText = "";
        bool fetchedManifest = false;
        for (int attempt = 1; attempt <= 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var resp = await http.GetAsync(modHashUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    remoteHashText = await resp.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(remoteHashText))
                    {
                        fetchedManifest = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Host Nginx may still be finishing its initial file scan
            }

            string waitMsg = $"Waiting for Host to finish scanning files ({attempt * 1.5:0}s)...";
            progressCallback?.Invoke(waitMsg, 25);
            detailedProgressCallback?.Invoke(waitMsg, 25, 0);
            await Task.Delay(1500, cancellationToken);
        }

        if (!fetchedManifest)
        {
            logCallback($"[ERROR] Could not reach Host Nginx Server at {modHashUrl} (Timed out waiting for Host manifest).");
            return false;
        }

        var remoteEntries = ModHashService.ParseModHashContent(remoteHashText);
        if (remoteEntries.Count == 0)
        {
            logCallback("[WARN] Host ModHash.txt is empty or invalid.");
            return false;
        }

        logCallback($"[CLIENT] Retrieved Host ModHash.txt ({remoteEntries.Count} entries with direct Host file locations).");

        var localMap = localEntries.ToDictionary(e => e.Key, e => e, StringComparer.OrdinalIgnoreCase);
        var remoteMap = remoteEntries.ToDictionary(e => e.Key, e => e, StringComparer.OrdinalIgnoreCase);

        var toDownload = new List<(ModHashEntry Remote, string LocalDestPath)>();
        int upToDateCount = 0;
        int excludedSkipCount = 0;

        foreach (var remote in remoteEntries)
        {
            if (ModHashService.ShouldSkipEntry(remote.Type, remote.Name, settings))
            {
                excludedSkipCount++;
                continue;
            }

            string? localDest = PathResolverService.ResolveLocalFilePath(remote.Type, remote.Name, baseLarian, bg3InstallDir);
            if (string.IsNullOrWhiteSpace(localDest))
            {
                logCallback($"[WARN] Skipping [{remote.Type}] {remote.Name} (BG3 install directory not set).");
                continue;
            }

            bool needsDownload = !localMap.TryGetValue(remote.Key, out var localEntry)
                || !string.Equals(localEntry.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(localDest);

            if (needsDownload)
            {
                toDownload.Add((remote, localDest));
            }
            else
            {
                upToDateCount++;
            }
        }

        var toDelete = new List<(ModHashEntry Local, string LocalPath)>();
        foreach (var local in localEntries)
        {
            if (ModHashService.ShouldSkipEntry(local.Type, local.Name, settings))
                continue;

            if (!remoteMap.ContainsKey(local.Key))
            {
                string? localPath = PathResolverService.ResolveLocalFilePath(local.Type, local.Name, baseLarian, bg3InstallDir);
                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                {
                    toDelete.Add((local, localPath));
                }
            }
        }

        string exclInfo = excludedSkipCount > 0 ? $" | {excludedSkipCount} Excluded" : "";
        logCallback($"[CLIENT] Comparison Result: {upToDateCount} Up-to-Date | {toDownload.Count} To Download | {toDelete.Count} Obsolete To Delete{exclInfo}");
        if (toDownload.Count > 0)
        {
            logCallback($"[CLIENT] Transfer Mode: {destStorageDesc} ({maxConcurrentDownloads} Parallel HTTP Stream(s), Write Buffer {destBufferSize / (1024 * 1024)} MB)");
        }

        int downloadedCount = 0;
        int failedCount = 0;
        var failedKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        int deletedCount = 0;
        int startedOrCompletedFiles = 0;
        long aggregateBytesDownloaded = 0;
        var batchSw = Stopwatch.StartNew();
        long lastUiReportTick = 0;

        await Parallel.ForEachAsync(
            toDownload.Select((item, idx) => (item.Remote, item.LocalDestPath, Index: idx + 1)),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrentDownloads,
                CancellationToken = cancellationToken
            },
            async (work, ct) =>
            {
                var (remote, localDest, fileIdx) = work;
                string relativeNginxUrl = remote.GetNginxRelativeUrl();
                string fileDownloadUrl = $"{baseHttpUrl.TrimEnd('/')}{relativeNginxUrl}";

                try
                {
                    await DownloadFileWithProgressAsync(
                        http,
                        fileDownloadUrl,
                        localDest,
                        remote,
                        fileIdx,
                        toDownload.Count,
                        isDestSsd,
                        destBufferSize,
                        bytesDelta =>
                        {
                            long totalNow = Interlocked.Add(ref aggregateBytesDownloaded, bytesDelta);
                            long nowTick = Environment.TickCount64;
                            long prevTick = Interlocked.Read(ref lastUiReportTick);
                            if (nowTick - prevTick >= 100 && Interlocked.CompareExchange(ref lastUiReportTick, nowTick, prevTick) == prevTick)
                            {
                                double elapsedSec = Math.Max(0.05, batchSw.Elapsed.TotalSeconds);
                                double aggSpeed = totalNow / elapsedSec;
                                int doneCount = Volatile.Read(ref downloadedCount);
                                double overallPct = 25.0 + (Math.Min(toDownload.Count, doneCount + 0.5) * 75.0 / Math.Max(1, toDownload.Count));
                                string shortName = remote.Name.Length <= 32 ? remote.Name : remote.Name.Substring(0, 29) + "...";
                                string statusLine = $"[{Math.Min(doneCount + 1, toDownload.Count)}/{toDownload.Count}] [{remote.Type}] {shortName} @ {PathResolverService.FormatSpeed(aggSpeed)}";
                                progressCallback?.Invoke(statusLine, overallPct);
                                detailedProgressCallback?.Invoke(statusLine, overallPct, aggSpeed);
                            }
                        },
                        logCallback,
                        ct);

                    int done = Interlocked.Increment(ref downloadedCount);
                    double elapsedSec = Math.Max(0.05, batchSw.Elapsed.TotalSeconds);
                    double aggSpeed = Interlocked.Read(ref aggregateBytesDownloaded) / elapsedSec;
                    double overallPct = 25.0 + (done * 75.0 / Math.Max(1, toDownload.Count));
                    string doneLine = $"[{done}/{toDownload.Count}] Downloaded {remote.Name} @ {PathResolverService.FormatSpeed(aggSpeed)}";
                    progressCallback?.Invoke(doneLine, overallPct);
                    detailedProgressCallback?.Invoke(doneLine, overallPct, aggSpeed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref startedOrCompletedFiles);
                    Interlocked.Increment(ref failedCount);
                    failedKeys[remote.Key] = 1;
                    logCallback($"[ERROR] Failed to download [{remote.Type}] {remote.Name}: {ex.Message}");
                }
            });

        foreach (var (local, localPath) in toDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(localPath);
                deletedCount++;
                logCallback($"[DELETE] [{local.Type,-9}] {Path.GetFileName(localPath),-32} : [DELETED FROM LOCAL]");
            }
            catch (Exception ex)
            {
                logCallback($"[ERROR] Failed to delete obsolete file {local.Name}: {ex.Message}");
            }
        }

        // Update local ModHash.txt to reflect synchronized state (excluding any files that failed to download)
        try
        {
            string localHashPath = Path.Combine(baseLarian, PathResolverService.ModHashFileName);
            var updatedLines = new StringBuilder();
            updatedLines.AppendLine("{Type} {Hash} {Name} {FilePath}");
            foreach (var remote in remoteEntries)
            {
                if (failedKeys.ContainsKey(remote.Key))
                {
                    if (localMap.TryGetValue(remote.Key, out var prevLocal) &&
                        !string.IsNullOrWhiteSpace(prevLocal.DirectFilePath) &&
                        File.Exists(prevLocal.DirectFilePath))
                    {
                        updatedLines.AppendLine(prevLocal.ToModHashLine());
                    }
                    continue;
                }

                string? resolvedLocal = PathResolverService.ResolveLocalFilePath(remote.Type, remote.Name, baseLarian, bg3InstallDir);
                var syncedEntry = new ModHashEntry
                {
                    Type = remote.Type,
                    Hash = remote.Hash,
                    Name = remote.Name,
                    DirectFilePath = resolvedLocal ?? remote.DirectFilePath
                };
                updatedLines.AppendLine(syncedEntry.ToModHashLine());
            }
            await File.WriteAllTextAsync(localHashPath, updatedLines.ToString(), new UTF8Encoding(false), cancellationToken);
        }
        catch { }

        if (failedCount > 0)
        {
            string warnMsg = $"Sync Incomplete: {failedCount} Failed | Downloaded: {downloadedCount} | Up-to-Date: {upToDateCount}";
            progressCallback?.Invoke(warnMsg, Math.Clamp(25.0 + (downloadedCount * 75.0 / Math.Max(1, toDownload.Count)), 0.0, 99.0));
            detailedProgressCallback?.Invoke(warnMsg, Math.Clamp(25.0 + (downloadedCount * 75.0 / Math.Max(1, toDownload.Count)), 0.0, 99.0), 0.0);
            logCallback($"[WARN] Sync Incomplete — {failedCount} file(s) failed to download (Downloaded: {downloadedCount} | Up-to-Date: {upToDateCount} | Deleted: {deletedCount}). Click CLIENT to retry.");
            PathResolverService.CompactAndTrimWorkingSet();
            return false;
        }

        string finalMsg = $"Sync Complete! Downloaded: {downloadedCount} | Up-to-Date: {upToDateCount} | Deleted: {deletedCount}";
        progressCallback?.Invoke(finalMsg, 100.0);
        detailedProgressCallback?.Invoke(finalMsg, 100.0, 0.0);
        logCallback($"[SUCCESS] Sync Complete! Files Downloaded: {downloadedCount} | Up-to-Date: {upToDateCount} | Local Files Deleted: {deletedCount}");
        PathResolverService.CompactAndTrimWorkingSet();
        return true;
    }

    private static async Task DownloadFileWithProgressAsync(
        HttpClient http,
        string url,
        string destPath,
        ModHashEntry entry,
        int fileIndex,
        int totalFiles,
        bool isDestSsd,
        int writeBufferSize,
        Action<int>? onBytesRead,
        Action<string> logCallback,
        CancellationToken cancellationToken)
    {
        string? parentDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        string tempPath = $"{destPath}.{Guid.NewGuid():N}.bg3sync.tmp";
        RuntimeTempCleanupService.TrackTempFile(tempPath);
        HttpResponseMessage? initialResp = null;
        try
        {
            const long InitialRangeThreshold = 32L * 1024 * 1024; // 32 MB
            if (isDestSsd)
            {
                using var initialReq = new HttpRequestMessage(HttpMethod.Get, url);
                initialReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, InitialRangeThreshold - 1);
                initialResp = await http.SendAsync(initialReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (initialResp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Empty (0-byte) file returns 416 on Range: bytes=0-*; drain small 416 body so socket returns to pool, then GET
                    try { _ = await initialResp.Content.ReadAsByteArrayAsync(cancellationToken); } catch { }
                    initialResp.Dispose();
                    initialResp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
            }
            else
            {
                initialResp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            initialResp.EnsureSuccessStatusCode();

            long totalRead = 0;
            var sw = Stopwatch.StartNew();

            string fileName = Path.GetFileName(destPath);
            string shortName = fileName.Length <= 32 ? fileName : fileName.Substring(0, 29) + "...";

            var contentRange = initialResp.Content.Headers.ContentRange;
            long fullFileBytes = contentRange?.Length ?? (initialResp.Content.Headers.ContentLength ?? 0);
            long firstSegmentEnd = contentRange?.To ?? ((initialResp.Content.Headers.ContentLength ?? InitialRangeThreshold) - 1);
            long firstSegmentBytes = firstSegmentEnd + 1;

            // For large files (> 32 MB) on SSD where Range succeeded (206 Partial Content), keep Segment 0 streaming on initialResp
            // (zero aborted TCP sockets / zero ephemeral port churn!) and fetch the remaining tail across 3 parallel Range streams.
            if (isDestSsd &&
                initialResp.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                fullFileBytes > InitialRangeThreshold &&
                firstSegmentBytes > 0 &&
                firstSegmentBytes < fullFileBytes)
            {
                var seg0Resp = initialResp;
                initialResp = null; // Ownership transferred to segmentTasks[0]

                const int tailSegments = 3;
                long remainingBytes = fullFileBytes - firstSegmentBytes;
                long tailSegmentSize = (remainingBytes + tailSegments - 1) / tailSegments;

                using (var handle = File.OpenHandle(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous))
                {
                    RandomAccess.SetLength(handle, fullFileBytes);
                    var segmentTasks = new Task[1 + tailSegments];

                    segmentTasks[0] = Task.Run(async () =>
                    {
                        using (seg0Resp)
                        {
                            await using var seg0Stream = await seg0Resp.Content.ReadAsStreamAsync(cancellationToken);
                            byte[] seg0Buf = ArrayPool<byte>.Shared.Rent(DownloadChunkSize);
                            try
                            {
                                long writePos = 0;
                                int r;
                                while ((r = await seg0Stream.ReadAsync(seg0Buf.AsMemory(0, DownloadChunkSize), cancellationToken)) > 0)
                                {
                                    await RandomAccess.WriteAsync(handle, seg0Buf.AsMemory(0, r), writePos, cancellationToken);
                                    writePos += r;
                                    Interlocked.Add(ref totalRead, r);
                                    onBytesRead?.Invoke(r);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(seg0Buf);
                            }
                        }
                    }, cancellationToken);

                    for (int t = 0; t < tailSegments; t++)
                    {
                        long startOffset = firstSegmentBytes + (t * tailSegmentSize);
                        long endOffset = Math.Min(fullFileBytes - 1, startOffset + tailSegmentSize - 1);
                        if (startOffset > endOffset)
                        {
                            segmentTasks[1 + t] = Task.CompletedTask;
                            continue;
                        }

                        segmentTasks[1 + t] = Task.Run(async () =>
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startOffset, endOffset);
                            using var segResp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                            segResp.EnsureSuccessStatusCode();

                            await using var segStream = await segResp.Content.ReadAsStreamAsync(cancellationToken);
                            byte[] segBuf = ArrayPool<byte>.Shared.Rent(DownloadChunkSize);
                            try
                            {
                                long writePos = startOffset;
                                int r;
                                while ((r = await segStream.ReadAsync(segBuf.AsMemory(0, DownloadChunkSize), cancellationToken)) > 0)
                                {
                                    await RandomAccess.WriteAsync(handle, segBuf.AsMemory(0, r), writePos, cancellationToken);
                                    writePos += r;
                                    Interlocked.Add(ref totalRead, r);
                                    onBytesRead?.Invoke(r);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(segBuf);
                            }
                        }, cancellationToken);
                    }

                    await Task.WhenAll(segmentTasks);
                }
            }
            else
            {
                var singleResp = initialResp;
                initialResp = null;
                using (singleResp)
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadChunkSize);
                    try
                    {
                        await using (var contentStream = await singleResp.Content.ReadAsStreamAsync(cancellationToken))
                        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
                        {
                            int read;
                            while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, DownloadChunkSize), cancellationToken)) > 0)
                            {
                                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                                totalRead += read;
                                onBytesRead?.Invoke(read);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }

            sw.Stop();
            if (fullFileBytes > 0 && totalRead != fullFileBytes)
            {
                throw new IOException($"Incomplete transfer for {fileName}: received {totalRead} of {fullFileBytes} bytes.");
            }
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
            File.Move(tempPath, destPath);

            double avgSpeed = sw.Elapsed.TotalSeconds > 0 ? totalRead / sw.Elapsed.TotalSeconds : totalRead;
            logCallback($"[HTTP-GET] [{entry.Type,-9}] {shortName,-32} : 100%  {PathResolverService.FormatBytes(totalRead),10}  [{PathResolverService.FormatSpeed(avgSpeed),12}] (from Host: {entry.DirectFilePath})");
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        finally
        {
            initialResp?.Dispose();
        }
    }
}
