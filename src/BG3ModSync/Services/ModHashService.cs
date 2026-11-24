using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BG3ModSync.Models;

namespace BG3ModSync.Services;

public class ModHashService
{
    private const int MaxSsdWorkersCap = 8;
    private const int SsdBufferSize = 1024 * 1024; // 1 MB pooled chunk (zero LOH retention, full NVMe/SSD saturation)
    private const int HddBufferSize = 4 * 1024 * 1024; // 4 MB pooled sequential read chunk for HDDs

    private static readonly string[] ExplicitBinFiles =
    {
        "bg3.exe",
        "bg3_dx11.exe",
        "bink2w64.dll",
        "bink2w64_original.dll",
        "DWrite.dll"
    };

    // Matches 4-column {Type} {Hash} {Name} {FilePath} or 3-column {Type} {Hash} {Name}
    private static readonly Regex ModHashLineRegex = new(
        @"^\{(?<type>[^}]+)\}\s+\{(?<hash>[^}]+)\}\s+\{(?<name>[^}]+)\}(?:\s+\{(?<path>[^}]+)\})?$",
        RegexOptions.Compiled
    );

    public static bool MatchesExclusionRules(string pathOrName, IEnumerable<string>? rules)
    {
        if (rules == null || string.IsNullOrWhiteSpace(pathOrName))
            return false;

        string lowerTarget = pathOrName.Replace('\\', '/').ToLowerInvariant();
        string fileName = Path.GetFileName(lowerTarget);

        foreach (var rawRule in rules)
        {
            if (string.IsNullOrWhiteSpace(rawRule)) continue;
            string rule = rawRule.Trim().Replace('\\', '/').ToLowerInvariant();
            if (string.IsNullOrEmpty(rule)) continue;

            if (rule.StartsWith('.'))
            {
                if (fileName.EndsWith(rule, StringComparison.Ordinal) || lowerTarget.EndsWith(rule, StringComparison.Ordinal))
                    return true;
            }
            else
            {
                if (lowerTarget.Contains(rule, StringComparison.Ordinal) || fileName.Contains(rule, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    public static bool ShouldSkipPak(string fileName, IEnumerable<string>? rules = null)
    {
        var activeRules = rules ?? AppSettings.GetDefaultPakExclusions();
        if (MatchesExclusionRules(fileName, activeRules))
            return true;

        return !fileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldSkipJson(string pathOrFolderOrFileName, IEnumerable<string>? rules = null)
    {
        var activeRules = rules ?? AppSettings.GetDefaultJsonExclusions();
        return MatchesExclusionRules(pathOrFolderOrFileName, activeRules);
    }

    public static bool ShouldSkipGamedata(string pathOrFileName, IEnumerable<string>? rules = null)
    {
        var activeRules = rules ?? AppSettings.GetDefaultGamedataExclusions();
        return MatchesExclusionRules(pathOrFileName, activeRules);
    }

    public static bool ShouldSkipLoadOrder(string fileName, IEnumerable<string>? rules = null)
    {
        var activeRules = rules ?? AppSettings.GetDefaultLoadOrderExclusions();
        return MatchesExclusionRules(fileName, activeRules);
    }

    public static bool ShouldSkipEntry(string type, string name, AppSettings settings)
    {
        return type.ToUpperInvariant() switch
        {
            "PAK" => ShouldSkipPak(name, settings.ExcludedPakRules),
            "JSON" => ShouldSkipJson(name, settings.ExcludedJsonRules),
            "GAMEDATA" => ShouldSkipGamedata(name, settings.ExcludedGamedataRules),
            "LOADORDER" => ShouldSkipLoadOrder(name, settings.ExcludedLoadOrderRules),
            _ => false
        };
    }

    public static string ComputeXxHash3Hex(string filePath, int bufferSize)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var hasher = new XxHash3();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.SequentialScan);
            int bytesRead;
            while ((bytesRead = fs.Read(buffer, 0, bufferSize)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, bytesRead));
            }
            ulong digest = hasher.GetCurrentHashAsUInt64();
            return digest.ToString("x16");
        }
        catch
        {
            return "0000000000000000";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<(string ModHashFilePath, List<ModHashEntry> Entries)> GenerateModHashAsync(
        AppSettings settings,
        Action<string> logCallback,
        Action<string, double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            string baseLarian = PathResolverService.GetLarianBasePath(settings);
            Directory.CreateDirectory(baseLarian);
            string outputFile = Path.Combine(baseLarian, PathResolverService.ModHashFileName);

            var categories = new Dictionary<string, List<ModHashEntry>>
            {
                ["LOADORDER"] = new(),
                ["PAK"] = new(),
                ["JSON"] = new(),
                ["GAMEDATA"] = new()
            };

            // 1. LOADORDER
            string modSettingsPath = Path.Combine(baseLarian, "PlayerProfiles", "Public", "modsettings.lsx");
            if (File.Exists(modSettingsPath) && !ShouldSkipLoadOrder("modsettings.lsx", settings.ExcludedLoadOrderRules))
            {
                categories["LOADORDER"].Add(new ModHashEntry
                {
                    Type = "LOADORDER",
                    Name = "modsettings.lsx",
                    DirectFilePath = PathResolverService.ResolveRealFilePath(modSettingsPath)
                });
            }

            // 2. PAK
            string modsDir = Path.Combine(baseLarian, "Mods");
            if (Directory.Exists(modsDir))
            {
                foreach (var file in Directory.EnumerateFiles(modsDir))
                {
                    string fileName = Path.GetFileName(file);
                    if (!ShouldSkipPak(fileName, settings.ExcludedPakRules))
                    {
                        categories["PAK"].Add(new ModHashEntry
                        {
                            Type = "PAK",
                            Name = fileName,
                            DirectFilePath = PathResolverService.ResolveRealFilePath(file)
                        });
                    }
                }
            }

            // 3. JSON (Script Extender)
            string seDir = Path.Combine(baseLarian, "Script Extender");
            if (Directory.Exists(seDir))
            {
                EnumerateSeJsonFiles(seDir, baseLarian, categories["JSON"], settings.ExcludedJsonRules);
            }

            // 4. GAMEDATA (BG3 Install Dir \ bin)
            string? bg3Dir = PathResolverService.GetBg3InstallDir(settings);
            if (!string.IsNullOrWhiteSpace(bg3Dir) && Directory.Exists(bg3Dir))
            {
                string binDir = Path.Combine(bg3Dir, "bin");
                if (Directory.Exists(binDir))
                {
                    foreach (var fname in ExplicitBinFiles)
                    {
                        string fpath = Path.Combine(binDir, fname);
                        if (File.Exists(fpath) && !ShouldSkipGamedata(fname, settings.ExcludedGamedataRules))
                        {
                            categories["GAMEDATA"].Add(new ModHashEntry
                            {
                                Type = "GAMEDATA",
                                Name = $@"\Baldurs Gate 3\bin\{fname}",
                                DirectFilePath = PathResolverService.ResolveRealFilePath(fpath)
                            });
                        }
                    }

                    string nativeModsDir = Path.Combine(binDir, "NativeMods");
                    if (Directory.Exists(nativeModsDir))
                    {
                        foreach (var fpath in Directory.EnumerateFiles(nativeModsDir, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(binDir, fpath);
                            if (!ShouldSkipGamedata(rel, settings.ExcludedGamedataRules))
                            {
                                categories["GAMEDATA"].Add(new ModHashEntry
                                {
                                    Type = "GAMEDATA",
                                    Name = $@"\Baldurs Gate 3\bin\{rel}",
                                    DirectFilePath = PathResolverService.ResolveRealFilePath(fpath)
                                });
                            }
                        }
                    }
                }
            }
            else
            {
                logCallback("[WARN] Baldur's Gate 3 install folder not detected. GAMEDATA scan skipped (can be set in Settings).");
            }

            var (isSsd, driveSummary) = PathResolverService.DetectModsStorageMode(modsDir, baseLarian, categories["PAK"]);
            int maxWorkers = isSsd ? Math.Max(1, Math.Min(MaxSsdWorkersCap, Environment.ProcessorCount - 2)) : 1;
            int bufferSize = isSsd ? SsdBufferSize : HddBufferSize;

            logCallback($"[HASH] Storage Mode: {driveSummary} [{(isSsd ? "SSD" : "HDD")}] ({maxWorkers} Worker{(maxWorkers > 1 ? "s" : "")}, Buffer {bufferSize / (1024 * 1024)} MB)");

            var allResults = new List<ModHashEntry>();
            string[] catOrder = { "LOADORDER", "PAK", "JSON", "GAMEDATA" };

            int totalFilesAll = categories.Values.Sum(l => l.Count);
            int globalDone = 0;

            foreach (var catName in catOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = categories[catName];
                int total = items.Count;
                var sw = Stopwatch.StartNew();

                if (total == 0)
                {
                    logCallback($"[HASH] {catName,-10} : [============] 100%   0/0   [0.0s]");
                    continue;
                }

                // Check if any file in this category resides on an HDD (e.g. symlinked or installed on HDD)
                bool catAllSsd = isSsd && items.All(it => PathResolverService.IsSsdDrive(it.DirectFilePath));
                int catWorkers = catAllSsd ? maxWorkers : 1;
                int catBufferSize = catAllSsd ? SsdBufferSize : HddBufferSize;

                var catResults = new ConcurrentBag<ModHashEntry>();
                int completedInCat = 0;

                if (catWorkers > 1)
                {
                    Parallel.ForEach(
                        items,
                        new ParallelOptions { MaxDegreeOfParallelism = catWorkers, CancellationToken = cancellationToken },
                        item =>
                        {
                            item.Hash = ComputeXxHash3Hex(item.DirectFilePath, catBufferSize);
                            item.FileSize = PathResolverService.GetRealFileSize(item.DirectFilePath);
                            catResults.Add(item);

                            int cur = Interlocked.Increment(ref completedInCat);
                            int gCur = Interlocked.Increment(ref globalDone);
                            double pct = totalFilesAll > 0 ? (gCur * 100.0 / totalFilesAll) : 100.0;
                            progressCallback?.Invoke($"Hashing {catName}: {item.Name} ({cur}/{total})", pct);
                        });
                }
                else
                {
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        item.Hash = ComputeXxHash3Hex(item.DirectFilePath, catBufferSize);
                        item.FileSize = PathResolverService.GetRealFileSize(item.DirectFilePath);
                        catResults.Add(item);

                        completedInCat++;
                        globalDone++;
                        double pct = totalFilesAll > 0 ? (globalDone * 100.0 / totalFilesAll) : 100.0;
                        progressCallback?.Invoke($"Hashing {catName}: {item.Name} ({completedInCat}/{total})", pct);
                    }
                }

                sw.Stop();
                var sorted = catResults.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                allResults.AddRange(sorted);
                logCallback($"[HASH] {catName,-10} : [============] 100%   {total}/{total}   [{sw.Elapsed.TotalSeconds:F1}s]");
            }

            var sb = new StringBuilder();
            sb.AppendLine("{Type} {Hash} {Name} {FilePath}");
            foreach (var entry in allResults)
            {
                sb.AppendLine(entry.ToModHashLine());
            }

            File.WriteAllText(outputFile, sb.ToString(), new UTF8Encoding(false));
            logCallback($"[HASH] Done! Scanned {allResults.Count} files. Saved direct file paths to: {outputFile}");
            progressCallback?.Invoke($"Hash complete ({allResults.Count} files)", 100);
            PathResolverService.CompactAndTrimWorkingSet();

            return (outputFile, allResults);
        }, cancellationToken);
    }

    private static void EnumerateSeJsonFiles(
        string currentDir,
        string baseLarian,
        List<ModHashEntry> targetList,
        List<string> excludedJsonRules)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string rel = Path.GetRelativePath(baseLarian, file);
                if (!ShouldSkipJson(rel, excludedJsonRules))
                {
                    targetList.Add(new ModHashEntry
                    {
                        Type = "JSON",
                        Name = rel,
                        DirectFilePath = PathResolverService.ResolveRealFilePath(file)
                    });
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                string dirName = Path.GetFileName(subDir);
                if (!ShouldSkipJson(dirName, excludedJsonRules))
                {
                    EnumerateSeJsonFiles(subDir, baseLarian, targetList, excludedJsonRules);
                }
            }
        }
        catch
        {
            // Ignore permission errors
        }
    }

    public static List<ModHashEntry> ParseModHashContent(string content)
    {
        var list = new List<ModHashEntry>();
        if (string.IsNullOrWhiteSpace(content)) return list;

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.StartsWith("{Type}", StringComparison.OrdinalIgnoreCase)) continue;

            var match = ModHashLineRegex.Match(trimmed);
            if (match.Success)
            {
                list.Add(new ModHashEntry
                {
                    Type = match.Groups["type"].Value.Trim(),
                    Hash = match.Groups["hash"].Value.Trim(),
                    Name = match.Groups["name"].Value.Trim(),
                    DirectFilePath = match.Groups["path"].Success ? match.Groups["path"].Value.Trim() : ""
                });
            }
        }
        return list;
    }

    public static List<ModHashEntry> ParseModHashFile(string filePath)
    {
        if (!File.Exists(filePath)) return new List<ModHashEntry>();
        try
        {
            return ParseModHashContent(File.ReadAllText(filePath, Encoding.UTF8));
        }
        catch
        {
            return new List<ModHashEntry>();
        }
    }
}
