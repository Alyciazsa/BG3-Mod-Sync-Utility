using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using BG3ModSync.Models;
using Microsoft.Win32;

namespace BG3ModSync.Services;

public static class PathResolverService
{
    public const string ModHashFileName = "ModHash.txt";
    private static readonly ConcurrentDictionary<char, bool> _driveSsdCache = new();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// Compacts the .NET Large Object Heap, runs a full generation GC, and trims the OS working set
    /// so idle memory remains lightweight (~45-85 MB) instead of retaining temporary file/network buffers.
    /// </summary>
    public static void CompactAndTrimWorkingSet()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            IntPtr handle = Process.GetCurrentProcess().Handle;
            EmptyWorkingSet(handle);
            SetProcessWorkingSetSize(handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            // Ignore working set trim errors
        }
    }

    public static bool IsValidBg3InstallDir(string? path, out string resolvedExePath)
    {
        resolvedExePath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            string clean = path.Trim().Trim('"');
            if (!Directory.Exists(clean)) return false;

            string candidate1 = Path.Combine(clean, "bin", "bg3.exe");
            if (File.Exists(candidate1))
            {
                resolvedExePath = candidate1;
                return true;
            }

            // Also handle if the user selected the 'bin' folder itself
            string candidate2 = Path.Combine(clean, "bg3.exe");
            if (File.Exists(candidate2))
            {
                resolvedExePath = candidate2;
                return true;
            }
        }
        catch { }

        return false;
    }

    public static bool IsValidLarianDataDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string clean = path.Trim().Trim('"');
            return Directory.Exists(clean) &&
                   (Directory.Exists(Path.Combine(clean, "Mods")) ||
                    Directory.Exists(Path.Combine(clean, "PlayerProfiles")) ||
                    clean.Contains("Baldur's Gate 3", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static string GetLarianBasePath(AppSettings? settings = null)
    {
        if (settings != null && !string.IsNullOrWhiteSpace(settings.CustomLarianPath) && Directory.Exists(settings.CustomLarianPath))
        {
            return settings.CustomLarianPath;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Larian Studios\Baldur's Gate 3"
        );
    }

    public static string? GetBg3InstallDir(AppSettings? settings = null)
    {
        if (settings != null && !string.IsNullOrWhiteSpace(settings.CustomBg3InstallPath) && Directory.Exists(settings.CustomBg3InstallPath))
        {
            return settings.CustomBg3InstallPath;
        }

        string[] regSubKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1086940",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1086940"
        };

        RegistryHive[] hives = { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
        foreach (var hive in hives)
        {
            foreach (var subKey in regSubKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(subKey);
                    if (key?.GetValue("InstallLocation") is string installLoc && !string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc))
                    {
                        return installLoc;
                    }
                }
                catch
                {
                    // Ignore registry access exceptions
                }
            }
        }

        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steamKey?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
            {
                string vdfFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfFile))
                {
                    foreach (var line in File.ReadAllLines(vdfFile))
                    {
                        if (line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split('"');
                            if (parts.Length >= 4)
                            {
                                string libPath = parts[3].Replace(@"\\", @"\");
                                string candidate = Path.Combine(libPath, "steamapps", "common", "Baldurs Gate 3");
                                if (Directory.Exists(candidate))
                                {
                                    return candidate;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;
                string[] candidateSubPaths =
                {
                    Path.Combine(root, "SteamLibrary", "steamapps", "common", "Baldurs Gate 3"),
                    Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "Baldurs Gate 3"),
                    Path.Combine(root, "Program Files", "Steam", "steamapps", "common", "Baldurs Gate 3"),
                    Path.Combine(root, "Steam", "steamapps", "common", "Baldurs Gate 3"),
                    Path.Combine(root, "Games", "Steam", "steamapps", "common", "Baldurs Gate 3"),
                    Path.Combine(root, "000.gaming", "steam", "steamapps", "common", "Baldurs Gate 3"),
                    Path.Combine(root, "Gaming", "Steam", "steamapps", "common", "Baldurs Gate 3"),
                    Path.Combine(root, "Baldurs Gate 3")
                };

                foreach (var candidate in candidateSubPaths)
                {
                    if (IsValidBg3InstallDir(candidate, out _))
                        return candidate;
                }

                // Also check 1 level deep on each drive (e.g. X:\000.gaming\steam\steamapps\common\Baldurs Gate 3)
                foreach (var topDir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        string sub1 = Path.Combine(topDir, "steam", "steamapps", "common", "Baldurs Gate 3");
                        if (IsValidBg3InstallDir(sub1, out _)) return sub1;

                        string sub2 = Path.Combine(topDir, "SteamLibrary", "steamapps", "common", "Baldurs Gate 3");
                        if (IsValidBg3InstallDir(sub2, out _)) return sub2;

                        string sub3 = Path.Combine(topDir, "steamapps", "common", "Baldurs Gate 3");
                        if (IsValidBg3InstallDir(sub3, out _)) return sub3;
                    }
                    catch { }
                }
            }
            catch
            {
                // Ignore
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the target local file path on the machine for a given ModHashEntry (LOADORDER, PAK, JSON, GAMEDATA).
    /// </summary>
    public static string? ResolveLocalFilePath(string typeStr, string nameStr, string baseLarian, string? bg3InstallDir)
    {
        switch (typeStr.ToUpperInvariant())
        {
            case "LOADORDER":
                return Path.Combine(baseLarian, "PlayerProfiles", "Public", "modsettings.lsx");

            case "PAK":
                return Path.Combine(baseLarian, "Mods", nameStr);

            case "JSON":
                return Path.Combine(baseLarian, nameStr.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));

            case "GAMEDATA":
                if (string.IsNullOrWhiteSpace(bg3InstallDir)) return null;
                string cleanName = nameStr.Replace('\\', '/');
                const string prefix = "/Baldurs Gate 3/bin/";
                string rel = cleanName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? cleanName.Substring(prefix.Length)
                    : cleanName.TrimStart('/');
                return Path.Combine(bg3InstallDir, "bin", rel.Replace('/', Path.DirectorySeparatorChar));

            default:
                return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        int nInBufferSize,
        out DeviceSeekPenaltyDescriptor lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public uint AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
        private byte _pad1;
        private byte _pad2;
        private byte _pad3;
    }

    public static bool IsUncOrNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
            trimmed = trimmed.Substring(4);
        return trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
               trimmed.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSsdDrive(string pathOrDrive)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pathOrDrive)) return false;

            string trimmed = pathOrDrive.Trim();
            if (IsUncOrNetworkPath(trimmed)) return false;

            char driveLetter;
            if (trimmed.Length <= 3 && char.IsLetter(trimmed[0]))
            {
                driveLetter = char.ToUpperInvariant(trimmed[0]);
            }
            else
            {
                string resolvedPath = File.Exists(trimmed)
                    ? ResolveRealFilePath(trimmed)
                    : ResolveRealDirectoryPath(trimmed);
                if (IsUncOrNetworkPath(resolvedPath)) return false;
                string? root = Path.GetPathRoot(resolvedPath);
                if (string.IsNullOrEmpty(root) || !char.IsLetter(root[0])) return false;
                driveLetter = char.ToUpperInvariant(root[0]);
            }

            if (_driveSsdCache.TryGetValue(driveLetter, out bool cachedIsSsd))
            {
                return cachedIsSsd;
            }

            bool? detected = null;

            // 1. Native Win32 IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceSeekPenaltyProperty) — instant, no Admin needed
            const uint FILE_SHARE_READ = 1;
            const uint FILE_SHARE_WRITE = 2;
            const uint OPEN_EXISTING = 3;
            const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
            const uint StorageDeviceSeekPenaltyProperty = 7;

            IntPtr handle = CreateFileW(
                $@"\\.\{driveLetter}:",
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                try
                {
                    var query = new StoragePropertyQuery
                    {
                        PropertyId = StorageDeviceSeekPenaltyProperty,
                        QueryType = 0,
                        AdditionalParameters = 0
                    };
                    if (DeviceIoControl(
                        handle,
                        IOCTL_STORAGE_QUERY_PROPERTY,
                        ref query,
                        Marshal.SizeOf<StoragePropertyQuery>(),
                        out var desc,
                        Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                        out uint bytesReturned,
                        IntPtr.Zero) && bytesReturned >= 9)
                    {
                        detected = desc.IncursSeekPenalty == 0;
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            // 2. Guarded PowerShell fallback (guards $p so empty Get-Partition NEVER leaks unrelated disks!)
            if (!detected.HasValue)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"$p = Get-Partition -DriveLetter '{driveLetter}' -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) {{ (Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object {{ $_.DeviceId -eq $p.DiskNumber }}).MediaType }}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(3000);
                    if (output.Contains("SSD", StringComparison.OrdinalIgnoreCase) &&
                        !output.Contains("HDD", StringComparison.OrdinalIgnoreCase))
                    {
                        detected = true;
                    }
                    else if (output.Contains("HDD", StringComparison.OrdinalIgnoreCase))
                    {
                        detected = false;
                    }
                }
            }

            // Safe fallback to HDD (1 sequential worker) when drive type cannot be confirmed
            bool isSsd = detected ?? false;
            _driveSsdCache[driveLetter] = isSsd;
            return isSsd;
        }
        catch
        {
            // Default to safe HDD single-worker mode on error
        }
        return false;
    }

    /// <summary>
    /// Detects whether the Mods folder (and any symlinked .pak files inside it) physically reside on SSD or HDD.
    /// If the Mods folder itself is a symlink/junction to an HDD, OR if .pak files inside Mods are symlinked from an HDD,
    /// selects HDD mode (1 Worker, 16 MB Buffer) to prevent HDD multi-scan head thrashing.
    /// </summary>
    public static (bool IsSsd, string DriveSummary) DetectModsStorageMode(
        string modsDir,
        string baseLarian,
        IReadOnlyList<ModHashEntry> pakEntries)
    {
        string nominalPath = Directory.Exists(modsDir) ? modsDir : baseLarian;
        string nominalDrive = IsUncOrNetworkPath(nominalPath)
            ? nominalPath
            : (Path.GetPathRoot(Path.GetFullPath(nominalPath)) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();

        string resolvedModsDir = Directory.Exists(modsDir)
            ? ResolveRealDirectoryPath(modsDir)
            : ResolveRealDirectoryPath(baseLarian);

        if (IsUncOrNetworkPath(resolvedModsDir))
        {
            return (false, $"{resolvedModsDir} (Network/UNC -> HDD Mode)");
        }

        string resolvedModsDrive = (Path.GetPathRoot(resolvedModsDir) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();

        var driveCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int symlinkedPakCount = 0;

        foreach (var entry in pakEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.DirectFilePath)) continue;
            string realPath = ResolveRealFilePath(entry.DirectFilePath);
            if (IsUncOrNetworkPath(realPath))
            {
                return (false, $"Network/UNC Symlink (from {nominalDrive})");
            }
            string drv = (Path.GetPathRoot(realPath) ?? resolvedModsDrive).TrimEnd('\\').ToUpperInvariant();
            driveCounts[drv] = driveCounts.TryGetValue(drv, out int c) ? c + 1 : 1;

            if (!string.Equals(drv, nominalDrive, StringComparison.OrdinalIgnoreCase))
            {
                symlinkedPakCount++;
            }
        }

        // Always include the real Mods folder destination drive (e.g. C:\...\Mods symlinked to D:\BG3Mods)
        // so empty Mods folders or mixed symlinks still respect the Mods folder's physical HDD!
        if (!driveCounts.ContainsKey(resolvedModsDrive))
        {
            driveCounts[resolvedModsDrive] = 0;
        }

        // Order physical drives by number of .pak files hosted on each drive
        var orderedDrives = driveCounts
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();

        // Check every physical drive where .pak files reside; if ANY drive is HDD, use HDD mode (1 Worker) to prevent HDD thrashing!
        bool allDrivesAreSsd = true;
        string? detectedHddDrive = null;
        foreach (var drv in orderedDrives)
        {
            if (!IsSsdDrive(drv))
            {
                allDrivesAreSsd = false;
                detectedHddDrive = drv;
                break;
            }
        }

        string primaryDrive = detectedHddDrive ?? orderedDrives[0];
        bool isCrossDriveSymlink = !string.Equals(primaryDrive, nominalDrive, StringComparison.OrdinalIgnoreCase) ||
                                   !string.Equals(resolvedModsDrive, nominalDrive, StringComparison.OrdinalIgnoreCase) ||
                                   symlinkedPakCount > 0;

        string driveSummary = isCrossDriveSymlink && !string.Equals(primaryDrive, nominalDrive, StringComparison.OrdinalIgnoreCase)
            ? $"{primaryDrive} (Symlinked from {nominalDrive})"
            : primaryDrive;

        return (allDrivesAreSsd, driveSummary);
    }

    public static List<DriveDisplayItem> GetAvailableDrives(string selectedDriveLetter)
    {
        var list = new List<DriveDisplayItem>();
        string cleanSelected = string.IsNullOrWhiteSpace(selectedDriveLetter) ? "C" : selectedDriveLetter.TrimEnd(':', '\\').ToUpperInvariant();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string letter = drive.Name.Substring(0, 1).ToUpperInvariant();
                long free = drive.AvailableFreeSpace;
                list.Add(new DriveDisplayItem
                {
                    DriveLetter = letter,
                    FreeSpaceBytes = free,
                    TotalSpaceBytes = drive.TotalSize,
                    FormattedFreeSpace = FormatBytes(free),
                    IsSelected = letter == cleanSelected
                });
            }
            catch
            {
                // Ignore inaccessible drives
            }
        }
        return list;
    }

    public static bool IsSymbolicLink(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves Windows NTFS Directory Symbolic Links / Junctions (including ancestor directories) to their real underlying directory path.
    /// </summary>
    public static string ResolveRealDirectoryPath(string dirPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(dirPath);
            if (!Directory.Exists(fullPath)) return fullPath;

            // Check if the directory itself or any parent directory is a symlink/junction
            var current = new DirectoryInfo(fullPath);
            var relativeTail = new Stack<string>();
            while (current != null && current.Exists)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = Directory.ResolveLinkTarget(current.FullName, returnFinalTarget: true);
                    if (target != null && target.Exists)
                    {
                        string resolvedBase = target.FullName;
                        while (relativeTail.Count > 0)
                        {
                            resolvedBase = Path.Combine(resolvedBase, relativeTail.Pop());
                        }
                        return Path.GetFullPath(resolvedBase);
                    }
                }
                if (current.Parent == null) break;
                relativeTail.Push(current.Name);
                current = current.Parent;
            }
            return fullPath;
        }
        catch
        {
            return Path.GetFullPath(dirPath);
        }
    }

    /// <summary>
    /// Resolves Windows NTFS Symbolic Links (both individual file symlinks such as Nexus Vortex deployed .pak links
    /// AND parent directory symlinks/junctions) to their real underlying physical file path.
    /// </summary>
    public static string ResolveRealFilePath(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (IsSymbolicLink(fullPath))
            {
                var target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                if (target != null && File.Exists(target.FullName))
                {
                    return Path.GetFullPath(target.FullName);
                }
            }

            string? parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                string resolvedParent = ResolveRealDirectoryPath(parentDir);
                if (!string.Equals(parentDir, resolvedParent, StringComparison.OrdinalIgnoreCase))
                {
                    string candidate = Path.Combine(resolvedParent, Path.GetFileName(fullPath));
                    if (IsSymbolicLink(candidate))
                    {
                        var target = File.ResolveLinkTarget(candidate, returnFinalTarget: true);
                        if (target != null && File.Exists(target.FullName))
                        {
                            return Path.GetFullPath(target.FullName);
                        }
                    }
                    return Path.GetFullPath(candidate);
                }
            }

            return fullPath;
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Returns the real byte size of a file, following Windows Symbolic Links / Vortex links so it never reports 0 B for symlinked .pak files.
    /// </summary>
    public static long GetRealFileSize(string path)
    {
        try
        {
            string realPath = ResolveRealFilePath(path);
            var fi = new FileInfo(realPath);
            if (fi.Exists && fi.Length > 0)
            {
                return fi.Length;
            }

            // Fallback: open file stream handle which follows Windows OS reparse points directly
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Length;
        }
        catch
        {
            return 0;
        }
    }

    public static long GetDirectorySize(string path)
    {
        if (File.Exists(path))
        {
            return GetRealFileSize(path);
        }
        if (!Directory.Exists(path)) return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                total += GetRealFileSize(file);
            }
        }
        catch { }
        return total;
    }

    public static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{(long)bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024.0 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        return $"{FormatBytes(bytesPerSec)}/s";
    }
}
