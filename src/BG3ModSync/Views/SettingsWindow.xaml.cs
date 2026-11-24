using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BG3ModSync.Models;
using BG3ModSync.Services;
using Microsoft.Win32;

namespace BG3ModSync.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<string> _logCallback;
    private readonly bool _isFirstRunPrompt;

    public SettingsWindow(AppSettings settings, Action<string> logCallback, bool isFirstRunPrompt = false)
    {
        InitializeComponent();
        _settings = settings;
        _logCallback = logCallback;
        _isFirstRunPrompt = isFirstRunPrompt;

        if (_isFirstRunPrompt)
        {
            Title = "First-Time Setup — Verify Mod & Game Locations";
            HeaderTitleText.Text = "FIRST-TIME SETUP — VERIFY LOCATIONS";
            HeaderSubtitleText.Text = "We auto-detected your Baldur's Gate 3 Mods and Game folders. Please check if these locations are correct before starting:";
            SaveConfirmBtn.Content = "✔ Yes, Locations Are Correct";
        }

        LarianPathTextBox.Text = PathResolverService.GetLarianBasePath(_settings);
        Bg3InstallPathTextBox.Text = PathResolverService.GetBg3InstallDir(_settings) ?? "";

        UpdateValidationBadges();
        Loaded += (_, _) => PathResolverService.CompactAndTrimWorkingSet();
    }

    private void LarianPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateValidationBadges();
    }

    private void Bg3InstallPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateValidationBadges();
    }

    private void UpdateValidationBadges()
    {
        if (LarianValidationText == null || Bg3ValidationText == null) return;

        string larian = LarianPathTextBox.Text.Trim().Trim('"');
        if (!string.IsNullOrEmpty(larian) && Directory.Exists(larian))
        {
            string modsSub = Path.Combine(larian, "Mods");
            bool hasMods = Directory.Exists(modsSub);
            LarianValidationText.Text = hasMods
                ? $"✓ Valid Larian Folder (Found '{modsSub}')"
                : $"✓ Folder exists ('Mods' subfolder will be created inside '{larian}')";
            LarianValidationText.Foreground = new SolidColorBrush(Color.FromRgb(108, 203, 95));
        }
        else
        {
            LarianValidationText.Text = "⚠ Folder does not exist yet — please check or browse for your Larian Studios\\Baldur's Gate 3 folder.";
            LarianValidationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 216, 107));
        }

        string bg3 = Bg3InstallPathTextBox.Text.Trim().Trim('"');
        // Normalize if user selected ..\Baldurs Gate 3\bin directly
        if (bg3.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(bg3, "bg3.exe")))
        {
            string? parent = Directory.GetParent(bg3)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                bg3 = parent;
            }
        }

        if (PathResolverService.IsValidBg3InstallDir(bg3, out string exePath))
        {
            Bg3ValidationText.Text = $"✓ Verified Game Folder! Found: {exePath}";
            Bg3ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(108, 203, 95));
        }
        else
        {
            string expectedExe = string.IsNullOrEmpty(bg3)
                ? @"..\Baldurs Gate 3\bin\bg3.exe"
                : Path.Combine(bg3, "bin", "bg3.exe");
            Bg3ValidationText.Text = $"✗ Could not find '{expectedExe}' — Please click Browse or Auto to select your 'Baldurs Gate 3' folder.";
            Bg3ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
        }
    }

    private void BrowseLarianBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Larian Studios Baldur's Gate 3 AppData Folder"
        };
        if (dlg.ShowDialog(this) == true)
        {
            LarianPathTextBox.Text = dlg.FolderName;
            UpdateValidationBadges();
        }
    }

    private void ResetLarianBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.CustomLarianPath = "";
        LarianPathTextBox.Text = PathResolverService.GetLarianBasePath(null);
        UpdateValidationBadges();
    }

    private void BrowseBg3InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Baldur's Gate 3 Installation Folder (e.g. ...\\steamapps\\common\\Baldurs Gate 3)"
        };
        if (dlg.ShowDialog(this) == true)
        {
            string folder = dlg.FolderName;
            if (folder.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(folder, "bg3.exe")))
            {
                folder = Directory.GetParent(folder)?.FullName ?? folder;
            }
            Bg3InstallPathTextBox.Text = folder;
            UpdateValidationBadges();
        }
    }

    private void ResetBg3InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.CustomBg3InstallPath = "";
        Bg3InstallPathTextBox.Text = PathResolverService.GetBg3InstallDir(null) ?? "";
        UpdateValidationBadges();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string larian = LarianPathTextBox.Text.Trim().Trim('"');
        string bg3 = Bg3InstallPathTextBox.Text.Trim().Trim('"');

        if (bg3.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(bg3, "bg3.exe")))
        {
            bg3 = Directory.GetParent(bg3)?.FullName ?? bg3;
        }

        if (!PathResolverService.IsValidBg3InstallDir(bg3, out string foundExe))
        {
            var res = MessageBox.Show(
                this,
                $"Could not find 'bin\\bg3.exe' inside:\n{(string.IsNullOrEmpty(bg3) ? "(Empty Path)" : bg3)}\n\n" +
                "Are you sure you want to save this location anyway?",
                "bg3.exe Not Found in Location",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes)
            {
                return;
            }
        }
        else
        {
            _logCallback($"[LOCATIONS] Verified BG3 executable: {foundExe}");
        }

        if (!string.IsNullOrEmpty(larian))
            _settings.CustomLarianPath = larian;

        if (!string.IsNullOrEmpty(bg3))
            _settings.CustomBg3InstallPath = bg3;

        _settings.FirstRunLocationsConfirmed = true;
        _settings.Save();
        _logCallback($"[SETTINGS] Confirmed Game & Mod paths. Larian Path: {PathResolverService.GetLarianBasePath(_settings)} | BG3 Install: {PathResolverService.GetBg3InstallDir(_settings)}");
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();
}
