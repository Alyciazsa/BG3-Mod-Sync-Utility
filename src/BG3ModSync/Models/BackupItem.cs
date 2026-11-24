using System;

namespace BG3ModSync.Models;

public class BackupItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public DateTime CreatedTime { get; set; }
    public int PakCount { get; set; }
    public string FormattedSize { get; set; } = "";

    public string DisplaySummary => $"{Name}  •  {PakCount} PAKs  •  {FormattedSize}  •  {CreatedTime:yyyy-MM-dd HH:mm}";
}

public class DriveDisplayItem
{
    public string DriveLetter { get; set; } = "C";
    public long FreeSpaceBytes { get; set; }
    public long TotalSpaceBytes { get; set; }
    public string FormattedFreeSpace { get; set; } = "";
    public bool IsSelected { get; set; }

    public string DisplayLabel => $"{DriveLetter}:\\   ({FormattedFreeSpace} Free)";
}
