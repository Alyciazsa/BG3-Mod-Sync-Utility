using System;

namespace BG3ModSync.Models;

public class ModHashEntry
{
    /// <summary>
    /// Category type: LOADORDER, PAK, JSON, or GAMEDATA
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// 64-bit XXH3 hash in 16-char lowercase hex
    /// </summary>
    public string Hash { get; set; } = "0000000000000000";

    /// <summary>
    /// Logical identifier / relative name (e.g., modsettings.lsx, MyMod.pak, Script Extender\Config.json, \Baldurs Gate 3\bin\NativeMods\...)
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Direct absolute file path on the machine that generated the hash file.
    /// Used by the Host's portable Nginx server to serve the file directly from disk without copying,
    /// and by the Client to construct the direct Nginx HTTP download URL.
    /// </summary>
    public string DirectFilePath { get; set; } = "";

    /// <summary>
    /// Size in bytes (optional metadata)
    /// </summary>
    public long FileSize { get; set; }

    public string Key => $"{Type}::{Name}";

    /// <summary>
    /// Formats the entry for ModHash.txt:
    /// {Type} {Hash} {Name} {DirectFilePath}
    /// </summary>
    public string ToModHashLine()
    {
        return $"{{{Type}}} {{{Hash}}} {{{Name}}} {{{DirectFilePath}}}";
    }

    /// <summary>
    /// Converts the Host's DirectFilePath stored in ModHash.txt into the corresponding Nginx HTTP path.
    /// Example: "D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin\bink2w64.dll"
    /// -> "/drive/D/SteamLibrary/steamapps/common/Baldurs%20Gate%203/bin/bink2w64.dll"
    /// </summary>
    public string GetNginxRelativeUrl()
    {
        if (!string.IsNullOrWhiteSpace(DirectFilePath) && DirectFilePath.Length >= 3 && DirectFilePath[1] == ':')
        {
            char driveLetter = char.ToUpperInvariant(DirectFilePath[0]);
            string remainder = DirectFilePath.Substring(2).Replace('\\', '/').TrimStart('/');
            string[] segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = Uri.EscapeDataString(segments[i]);
            }
            return $"/drive/{driveLetter}/{string.Join("/", segments)}";
        }

        // Fallback if parsing a legacy 3-column ModHash.txt without DirectFilePath
        string cleanName = Name.Replace('\\', '/').TrimStart('/');
        string[] parts = cleanName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }
        string encodedRel = string.Join("/", parts);

        return Type.ToUpperInvariant() switch
        {
            "LOADORDER" => "/larian/PlayerProfiles/Public/modsettings.lsx",
            "PAK" => $"/larian/Mods/{encodedRel}",
            "JSON" => $"/larian/{encodedRel}",
            "GAMEDATA" => $"/gamedata/{ StripBg3BinPrefix(Name) }",
            _ => $"/larian/{encodedRel}"
        };
    }

    private static string StripBg3BinPrefix(string rawName)
    {
        string clean = rawName.Replace('\\', '/');
        const string prefix = "/Baldurs Gate 3/bin/";
        if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(prefix.Length);
        }
        else
        {
            clean = clean.TrimStart('/');
        }
        string[] parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }
        return string.Join("/", parts);
    }
}
