using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace BG3ModSync.Services;

public static class RuntimeTempCleanupService
{
    private static readonly string _larianDefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Larian Studios\Baldur's Gate 3");

    private static readonly string _sessionRuntimeDir = Path.Combine(
        _larianDefaultRoot,
        ".bg3modsync_runtime");

    private static readonly ConcurrentBag<string> _trackedTempFiles = new();
    private static readonly ConcurrentBag<string> _trackedTempDirs = new();
    private static int _cleanupExecuted;

    public static string SessionRuntimeDir
    {
        get
        {
            EnsureHiddenRuntimeDir();
            return _sessionRuntimeDir;
        }
    }

    private static void EnsureHiddenRuntimeDir()
    {
        try
        {
            Directory.CreateDirectory(_sessionRuntimeDir);
            var di = new DirectoryInfo(_sessionRuntimeDir);
            if ((di.Attributes & FileAttributes.Hidden) == 0)
            {
                di.Attributes |= FileAttributes.Hidden;
            }
        }
        catch
        {
            // Ignore attribute errors if filesystem doesn't support hidden bit
        }
    }

    private static void ClearAttributesRecursive(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                }
                catch { }
            }

            foreach (var subDir in Directory.EnumerateDirectories(dirPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var di = new DirectoryInfo(subDir);
                    if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                    {
                        di.Attributes &= ~(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System);
                    }
                }
                catch { }
            }

            var rootInfo = new DirectoryInfo(dirPath);
            if ((rootInfo.Attributes & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System)) != 0)
            {
                rootInfo.Attributes &= ~(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System);
            }
        }
        catch { }
    }

    public static void TrackTempFile(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _trackedTempFiles.Add(filePath);
        }
    }

    public static void TrackTempDirectory(string dirPath)
    {
        if (!string.IsNullOrWhiteSpace(dirPath))
        {
            _trackedTempDirs.Add(dirPath);
        }
    }

    /// <summary>
    /// Cleans up legacy %TEMP%\BG3ModSync_Runtime_* folders and any leftover hidden runtime folder from crashed sessions.
    /// </summary>
    public static void CleanupStaleSessionsOnStartup()
    {
        try
        {
            string tempRoot = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(tempRoot, "BG3ModSync_Runtime_*"))
            {
                try
                {
                    ClearAttributesRecursive(dir);
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Another instance may still be running
                }
            }
        }
        catch
        {
            // Ignore
        }

        try
        {
            if (Process.GetProcessesByName("BG3ModSync").Length <= 1 && Directory.Exists(_sessionRuntimeDir))
            {
                foreach (var p in Process.GetProcessesByName("nginx"))
                {
                    try
                    {
                        string? fullPath = p.MainModule?.FileName;
                        if (fullPath != null && fullPath.StartsWith(_sessionRuntimeDir, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(1000);
                        }
                    }
                    catch
                    {
                        // Ignore permission / exited process errors
                    }
                }

                ClearAttributesRecursive(_sessionRuntimeDir);
                Directory.Delete(_sessionRuntimeDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors on startup
        }

        EnsureHiddenRuntimeDir();
    }

    /// <summary>
    /// Removes all temporary files and directories created during this session when the program closes.
    /// </summary>
    public static void CleanupAllOnExit()
    {
        if (System.Threading.Interlocked.Exchange(ref _cleanupExecuted, 1) == 1)
            return;

        // 1. Delete tracked individual temporary files (.bg3sync.tmp, steam_appid.txt, etc.)
        foreach (var file in _trackedTempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    File.Delete(file);
                }
            }
            catch
            {
                // Ignore if locked; post-exit cmd will remove it
            }
        }

        // Also ensure steam_appid.txt next to exe or working dir is removed if created
        string baseSteamAppId = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt");
        string cwdSteamAppId = Path.Combine(Environment.CurrentDirectory, "steam_appid.txt");
        try { if (File.Exists(baseSteamAppId)) File.Delete(baseSteamAppId); } catch { }
        try { if (File.Exists(cwdSteamAppId)) File.Delete(cwdSteamAppId); } catch { }

        // 2. Delete tracked temporary directories
        foreach (var dir in _trackedTempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    ClearAttributesRecursive(dir);
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Ignore
            }
        }

        // 3. Delete this session's hidden runtime folder (%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\.bg3modsync_runtime)
        try
        {
            if (Directory.Exists(_sessionRuntimeDir))
            {
                ClearAttributesRecursive(_sessionRuntimeDir);
                Directory.Delete(_sessionRuntimeDir, recursive: true);
            }
        }
        catch
        {
            // If a child process (like nginx.exe) is still releasing its handle, schedule silent post-exit deletion
        }

        // 4. Spawn a silent, windowless post-exit cleanup command to guarantee 0 leftover temp files
        //    even if Windows held a brief lock while the process was shutting down.
        try
        {
            string cmdArgs = $"/c ping 127.0.0.1 -n 2 >nul & attrib -h -r -s \"{_sessionRuntimeDir}\" >nul 2>nul & rd /s /q \"{_sessionRuntimeDir}\" 2>nul & del /f /q \"{baseSteamAppId}\" 2>nul & del /f /q \"{cwdSteamAppId}\" 2>nul";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            // Ignore
        }
    }
}
