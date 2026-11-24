using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BG3ModSync.Models;

public class AppSettings
{
    [JsonPropertyName("last_ip")]
    public string LastHostTarget { get; set; } = "";

    [JsonPropertyName("last_steam_target")]
    public string LastSteamTarget { get; set; } = "";

    [JsonPropertyName("last_wan_lan_ip")]
    public string LastWanLanIp { get; set; } = "";

    [JsonPropertyName("last_vpn_ip")]
    public string LastVpnIp { get; set; } = "";

    [JsonPropertyName("nginx_port")]
    public int NginxPort { get; set; } = 27000;

    [JsonPropertyName("backup_drive")]
    public string BackupDrive { get; set; } = "C";

    [JsonPropertyName("custom_backup_path")]
    public string CustomBackupPath { get; set; } = "";

    [JsonPropertyName("use_custom_backup_path")]
    public bool UseCustomBackupPath { get; set; } = false;

    [JsonPropertyName("custom_larian_path")]
    public string CustomLarianPath { get; set; } = "";

    [JsonPropertyName("custom_bg3_install_path")]
    public string CustomBg3InstallPath { get; set; } = "";

    [JsonPropertyName("connection_mode")]
    public string ConnectionMode { get; set; } = "Auto"; // Auto, SteamP2P, WAN_LAN, VPN

    [JsonPropertyName("steam_app_id")]
    public uint SteamAppId { get; set; } = 1086940; // Default: 1086940 (Baldur's Gate 3), 480 (Spacewar Experimental/Testing)

    [JsonPropertyName("first_run_locations_confirmed")]
    public bool FirstRunLocationsConfirmed { get; set; } = false;

    [JsonPropertyName("launch_graphics_api")]
    public string LaunchGraphicsApi { get; set; } = "Vulkan"; // "Vulkan" (bg3.exe) or "DX11" (bg3_dx11.exe)

    [JsonPropertyName("excluded_pak_rules")]
    public List<string> ExcludedPakRules { get; set; } = GetDefaultPakExclusions();

    [JsonPropertyName("excluded_json_rules")]
    public List<string> ExcludedJsonRules { get; set; } = GetDefaultJsonExclusions();

    [JsonPropertyName("excluded_gamedata_rules")]
    public List<string> ExcludedGamedataRules { get; set; } = GetDefaultGamedataExclusions();

    [JsonPropertyName("excluded_loadorder_rules")]
    public List<string> ExcludedLoadOrderRules { get; set; } = GetDefaultLoadOrderExclusions();

    public static List<string> GetDefaultPakExclusions() => new()
    {
        "english.pak",
        "modhash.txt",
        "backup",
        ".txt",
        ".log",
        ".json",
        ".bak",
        ".tmp"
    };

    public static List<string> GetDefaultJsonExclusions() => new()
    {
        "Armory",
        "CC_Preset",
        "CPF",
        "backup",
        ".log"
    };

    public static List<string> GetDefaultGamedataExclusions() => new()
    {
        "vortex_backup",
        "backup",
        ".log",
        ".tmp",
        ".bak"
    };

    public static List<string> GetDefaultLoadOrderExclusions() => new();

    public static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Larian Studios\Baldur's Gate 3\ModSyncSettings.json"
    );

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    if (loaded.NginxPort <= 0 || loaded.NginxPort > 65535 || loaded.NginxPort == 8080)
                        loaded.NginxPort = 27000;
                    if (string.IsNullOrWhiteSpace(loaded.BackupDrive))
                        loaded.BackupDrive = "C";
                    if (loaded.SteamAppId == 0)
                        loaded.SteamAppId = 1086940;
                    loaded.ExcludedPakRules ??= GetDefaultPakExclusions();
                    loaded.ExcludedJsonRules ??= GetDefaultJsonExclusions();
                    loaded.ExcludedGamedataRules ??= GetDefaultGamedataExclusions();
                    loaded.ExcludedLoadOrderRules ??= GetDefaultLoadOrderExclusions();
                    return loaded;
                }
            }
        }
        catch
        {
            // Ignore malformed settings and return default
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    public string GetEffectiveBackupRoot()
    {
        if (UseCustomBackupPath && !string.IsNullOrWhiteSpace(CustomBackupPath))
        {
            return CustomBackupPath;
        }
        string drive = string.IsNullOrWhiteSpace(BackupDrive) ? "C" : BackupDrive.TrimEnd(':', '\\');
        return $"{drive}:\\BG3MOD";
    }
}
