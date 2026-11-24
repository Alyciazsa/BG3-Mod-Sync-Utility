using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BG3ModSync.Models;
using BG3ModSync.Services;
using Microsoft.Win32;

namespace BG3ModSync.Views;

public partial class BackupRestoreWindow : Window
{
    private readonly AppSettings _settings;
    private readonly BackupRestoreService _backupService;
    private readonly Action<string> _logCallback;
    private bool _isInitializing = true;

    public BackupRestoreWindow(AppSettings settings, BackupRestoreService backupService, Action<string> logCallback)
    {
        InitializeComponent();
        _settings = settings;
        _backupService = backupService;
        _logCallback = logCallback;

        LoadInitialState();
    }

    private void LoadInitialState()
    {
        _isInitializing = true;
        var drives = PathResolverService.GetAvailableDrives(_settings.BackupDrive);
        DriveComboBox.ItemsSource = drives;
        var selectedDrive = drives.FirstOrDefault(d => d.IsSelected) ?? drives.FirstOrDefault();
        DriveComboBox.SelectedItem = selectedDrive;

        CustomBackupPathBox.Text = _settings.CustomBackupPath;
        UseCustomPathRadio.IsChecked = _settings.UseCustomBackupPath;
        UseDriveRadio.IsChecked = !_settings.UseCustomBackupPath;
        BackupNameTextBox.Text = $"MK{DateTime.Now:MMdd}";

        _isInitializing = false;
        RefreshUI();
    }

    private void RefreshUI()
    {
        var (pakCount, modsBytes, estTotalBytes) = BackupRestoreService.GetCurrentModsStats(_settings);
        StatsTextBlock.Text = $"Current .pak files in Mods: {pakCount}   |   Mods Size: {PathResolverService.FormatBytes(modsBytes)}   |   Full Backup Est: {PathResolverService.FormatBytes(estTotalBytes)}";

        string activeRoot = _settings.GetEffectiveBackupRoot();
        ActiveBackupRootText.Text = $"Active Backup Path: {activeRoot}";

        DriveComboBox.IsEnabled = UseDriveRadio.IsChecked == true;
        CustomBackupPathBox.IsEnabled = UseCustomPathRadio.IsChecked == true;
        BrowseCustomBackupBtn.IsEnabled = UseCustomPathRadio.IsChecked == true;

        var backups = BackupRestoreService.GetBackups(_settings);
        BackupsListBox.ItemsSource = backups;
        if (backups.Count > 0)
        {
            BackupsListBox.SelectedIndex = 0;
        }
    }

    private void BackupModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.UseCustomBackupPath = UseCustomPathRadio.IsChecked == true;
        _settings.Save();
        RefreshUI();
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (DriveComboBox.SelectedItem is DriveDisplayItem drv)
        {
            _settings.BackupDrive = drv.DriveLetter;
            _settings.Save();
            _logCallback($"[BACKUP] Backup drive set to {drv.DriveLetter}:\\");
            RefreshUI();
        }
    }

    private void CustomBackupPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.CustomBackupPath = CustomBackupPathBox.Text.Trim();
        _settings.Save();
        ActiveBackupRootText.Text = $"Active Backup Path: {_settings.GetEffectiveBackupRoot()}";
    }

    private void BrowseCustomBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Custom Backup Folder Location"
        };
        if (dialog.ShowDialog(this) == true)
        {
            CustomBackupPathBox.Text = dialog.FolderName;
            UseCustomPathRadio.IsChecked = true;
            _settings.CustomBackupPath = dialog.FolderName;
            _settings.UseCustomBackupPath = true;
            _settings.Save();
            _logCallback($"[BACKUP] Custom backup location set to: {dialog.FolderName}");
            RefreshUI();
        }
    }

    private void OpenBackupFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = _settings.GetEffectiveBackupRoot();
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo
            {
                FileName = root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CreateBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        string bName = BackupNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(bName))
        {
            MessageBox.Show(this, "Please enter a Backup Name (e.g., MK3).", "Backup Name Required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CreateBackupBtn.IsEnabled = false;
        try
        {
            bool ok = await _backupService.CreateBackupAsync(bName, _settings, _logCallback);
            RefreshUI();
            if (ok)
            {
                MessageBox.Show(this, $"Backup '{bName}' created successfully in:\n{_settings.GetEffectiveBackupRoot()}", "Backup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, "Backup failed. Check the detailed log in the main window for details.", "Backup Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            CreateBackupBtn.IsEnabled = true;
        }
    }

    private async void RestoreBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsListBox.SelectedItem is not BackupItem selected)
        {
            MessageBox.Show(this, "Please select a backup from the list to restore.", "No Backup Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Are you sure you want to restore '{selected.Name}'?\n\nThis will replace your current modsettings.lsx, Mods, and Script Extender folders.",
            "Confirm Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        RestoreBackupBtn.IsEnabled = false;
        try
        {
            bool deleteAfter = DeleteAfterRestoreCheckBox.IsChecked == true;
            bool ok = await _backupService.RestoreBackupAsync(selected, deleteAfter, _settings, _logCallback);
            RefreshUI();
            if (ok)
            {
                MessageBox.Show(this, $"Successfully restored mods from '{selected.Name}'.", "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            RestoreBackupBtn.IsEnabled = true;
        }
    }

    private async void DeleteBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsListBox.SelectedItem is not BackupItem selected)
        {
            MessageBox.Show(this, "Please select a backup from the list to delete.", "No Backup Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Permanently delete backup '{selected.Name}'?",
            "Confirm Delete Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        DeleteBackupBtn.IsEnabled = false;
        try
        {
            await _backupService.DeleteBackupAsync(selected, _logCallback);
            RefreshUI();
        }
        finally
        {
            DeleteBackupBtn.IsEnabled = true;
        }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => RefreshUI();

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
