using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BG3ModSync.Models;

namespace BG3ModSync.Services;

public class BackupRestoreService
{
    public static (int PakCount, long TotalModsBytes, long EstimatedFullBackupBytes) GetCurrentModsStats(AppSettings settings)
    {
        var (pakCount, _, modsSize, estTotal) = GetCurrentModsStatsWithSymlinks(settings);
        return (pakCount, modsSize, estTotal);
    }

    public static (int PakCount, int SymlinkPakCount, long TotalModsBytes, long EstimatedFullBackupBytes) GetCurrentModsStatsWithSymlinks(AppSettings settings)
    {
        string baseLarian = PathResolverService.GetLarianBasePath(settings);
        string modsDir = Path.Combine(baseLarian, "Mods");
        string modSettingsPath = Path.Combine(baseLarian, "PlayerProfiles", "Public", "modsettings.lsx");
        string seDir = Path.Combine(baseLarian, "Script Extender");

        int pakCount = 0;
        int symlinkPakCount = 0;
        long modsSize = 0;
        if (Directory.Exists(modsDir))
        {
            foreach (var f in Directory.EnumerateFiles(modsDir))
            {
                string fname = Path.GetFileName(f);
                if (!ModHashService.ShouldSkipPak(fname, settings.ExcludedPakRules))
                {
                    pakCount++;
                    if (PathResolverService.IsSymbolicLink(f))
                    {
                        symlinkPakCount++;
                    }
                    modsSize += PathResolverService.GetRealFileSize(f);
                }
            }
        }

        long estTotal = modsSize;
        if (File.Exists(modSettingsPath))
            estTotal += PathResolverService.GetRealFileSize(modSettingsPath);
        if (Directory.Exists(seDir))
            estTotal += PathResolverService.GetDirectorySize(seDir);

        return (pakCount, symlinkPakCount, modsSize, estTotal);
    }

    public static List<BackupItem> GetBackups(AppSettings settings)
    {
        var items = new List<BackupItem>();
        string backupRoot = settings.GetEffectiveBackupRoot();
        if (!Directory.Exists(backupRoot)) return items;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(backupRoot, "Backup-*"))
            {
                var di = new DirectoryInfo(dir);
                string modsSub = Path.Combine(dir, "Mods");
                int pakCount = 0;
                if (Directory.Exists(modsSub))
                {
                    pakCount = Directory.EnumerateFiles(modsSub, "*.pak", SearchOption.TopDirectoryOnly).Count();
                }
                long size = PathResolverService.GetDirectorySize(dir);
                items.Add(new BackupItem
                {
                    Name = di.Name,
                    FullPath = di.FullName,
                    CreatedTime = di.CreationTime,
                    PakCount = pakCount,
                    FormattedSize = PathResolverService.FormatBytes(size)
                });
            }
        }
        catch
        {
            // Ignore permission issues
        }

        return items.OrderByDescending(b => b.CreatedTime).ToList();
    }

    public async Task<bool> CreateBackupAsync(
        string backupName,
        AppSettings settings,
        Action<string> logCallback)
    {
        return await Task.Run(() =>
        {
            string cleanName = string.Join("_", backupName.Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                logCallback("[ERROR] Invalid backup name.");
                return false;
            }

            string baseLarian = PathResolverService.GetLarianBasePath(settings);
            string modsDir = Path.Combine(baseLarian, "Mods");
            string modSettingsPath = Path.Combine(baseLarian, "PlayerProfiles", "Public", "modsettings.lsx");
            string seDir = Path.Combine(baseLarian, "Script Extender");

            var (pakCount, _, estSize) = GetCurrentModsStats(settings);
            string backupRoot = settings.GetEffectiveBackupRoot();
            Directory.CreateDirectory(backupRoot);

            try
            {
                string? rootDrive = Path.GetPathRoot(Path.GetFullPath(backupRoot));
                if (!string.IsNullOrEmpty(rootDrive))
                {
                    var di = new DriveInfo(rootDrive);
                    if (di.IsReady && estSize >= di.AvailableFreeSpace)
                    {
                        logCallback($"[ERROR] Can't backup, not enough disk space on {rootDrive}! Required: {PathResolverService.FormatBytes(estSize)} | Available: {PathResolverService.FormatBytes(di.AvailableFreeSpace)}");
                        return false;
                    }
                }
            }
            catch
            {
                // Proceed if drive info check fails on network/custom paths
            }

            string backupDir = Path.Combine(backupRoot, $"Backup-{cleanName}-{pakCount}");
            Directory.CreateDirectory(backupDir);
            logCallback($"[BACKUP] Creating backup '{Path.GetFileName(backupDir)}' in {backupRoot}...");

            if (File.Exists(modSettingsPath))
            {
                File.Copy(modSettingsPath, Path.Combine(backupDir, "modsettings.lsx"), overwrite: true);
                logCallback("[BACKUP] Copied modsettings.lsx");
            }

            if (Directory.Exists(modsDir))
            {
                int copied = CopyDirectoryRecursive(modsDir, Path.Combine(backupDir, "Mods"));
                logCallback($"[BACKUP] Copied Mods directory ({copied} files)");
            }

            if (Directory.Exists(seDir))
            {
                int copied = CopyDirectoryRecursive(seDir, Path.Combine(backupDir, "Script Extender"));
                logCallback($"[BACKUP] Copied Script Extender directory ({copied} files)");
            }

            logCallback($"[BACKUP] Backup created successfully at: {backupDir}");
            return true;
        });
    }

    public async Task<bool> RestoreBackupAsync(
        BackupItem backup,
        bool deleteBackupAfterRestore,
        AppSettings settings,
        Action<string> logCallback)
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(backup.FullPath))
            {
                logCallback($"[ERROR] Backup folder not found: {backup.FullPath}");
                return false;
            }

            string baseLarian = PathResolverService.GetLarianBasePath(settings);
            string modsDir = Path.Combine(baseLarian, "Mods");
            string modSettingsPath = Path.Combine(baseLarian, "PlayerProfiles", "Public", "modsettings.lsx");
            string seDir = Path.Combine(baseLarian, "Script Extender");

            logCallback($"[RESTORE] Restoring mods from '{backup.Name}'...");

            try
            {
                if (File.Exists(modSettingsPath))
                    File.Delete(modSettingsPath);
                // Clear contents without deleting Mods / Script Extender directory symlinks or junctions
                // (preserves Drive C -> HDD/SSD symlink setups!).
                ClearDirectoryPreservingSymlink(modsDir);
                ClearDirectoryPreservingSymlink(seDir);
            }
            catch (Exception ex)
            {
                logCallback($"[WARN] Cleanup before restore encountered a locked file: {ex.Message}");
            }

            string bModSettings = Path.Combine(backup.FullPath, "modsettings.lsx");
            if (File.Exists(bModSettings))
            {
                string? parent = Path.GetDirectoryName(modSettingsPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(bModSettings, modSettingsPath, overwrite: true);
                logCallback("[RESTORE] Restored modsettings.lsx");
            }

            string bMods = Path.Combine(backup.FullPath, "Mods");
            if (Directory.Exists(bMods))
            {
                int count = CopyDirectoryRecursive(bMods, modsDir);
                logCallback($"[RESTORE] Restored Mods directory ({count} files)");
            }

            string bSe = Path.Combine(backup.FullPath, "Script Extender");
            if (Directory.Exists(bSe))
            {
                int count = CopyDirectoryRecursive(bSe, seDir);
                logCallback($"[RESTORE] Restored Script Extender directory ({count} files)");
            }

            if (deleteBackupAfterRestore)
            {
                try
                {
                    Directory.Delete(backup.FullPath, recursive: true);
                    logCallback($"[RESTORE] Restore complete! Backup folder '{backup.Name}' was removed after restore.");
                }
                catch (Exception ex)
                {
                    logCallback($"[WARN] Restore finished, but could not remove backup folder: {ex.Message}");
                }
            }
            else
            {
                logCallback($"[RESTORE] Restore complete! Kept backup folder '{backup.Name}'.");
            }

            return true;
        });
    }

    public async Task<bool> DeleteBackupAsync(BackupItem backup, Action<string> logCallback)
    {
        return await Task.Run(() =>
        {
            if (!Directory.Exists(backup.FullPath))
            {
                logCallback($"[WARN] Backup folder does not exist: {backup.FullPath}");
                return false;
            }
            try
            {
                logCallback($"[BACKUP] Deleting backup '{backup.Name}'...");
                Directory.Delete(backup.FullPath, recursive: true);
                logCallback($"[BACKUP] Backup '{backup.Name}' deleted successfully.");
                return true;
            }
            catch (Exception ex)
            {
                logCallback($"[ERROR] Failed to delete backup '{backup.Name}': {ex.Message}");
                return false;
            }
        });
    }

    private static void ClearDirectoryPreservingSymlink(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;
        foreach (var file in Directory.EnumerateFiles(dirPath))
        {
            try { File.Delete(file); } catch { }
        }
        foreach (var subDir in Directory.EnumerateDirectories(dirPath))
        {
            try
            {
                var attrs = File.GetAttributes(subDir);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(subDir, recursive: false);
                else
                    Directory.Delete(subDir, recursive: true);
            }
            catch { }
        }
    }

    private static int CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        int count = 0;
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            string realSource = PathResolverService.ResolveRealFilePath(file);
            File.Copy(realSource, dest, overwrite: true);
            count++;
        }
        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            string destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
            count += CopyDirectoryRecursive(subDir, destSub);
        }
        return count;
    }
}
