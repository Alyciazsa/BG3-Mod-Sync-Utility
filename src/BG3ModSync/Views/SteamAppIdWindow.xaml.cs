using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using BG3ModSync.Models;
using BG3ModSync.Services;

namespace BG3ModSync.Views;

public partial class SteamAppIdWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly AppSettings _settings;
    private readonly Action<string> _logCallback;
    private bool _isUpdatingUi;

    public uint SelectedAppId { get; private set; }

    public SteamAppIdWindow(AppSettings settings, Action<string> logCallback)
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            }
            catch { }
        };

        _settings = settings;
        _logCallback = logCallback;
        SelectedAppId = _settings.SteamAppId > 0 ? _settings.SteamAppId : SteamP2PRelayService.DefaultBg3AppId;

        _isUpdatingUi = true;
        AppIdTextBox.Text = SelectedAppId.ToString();
        if (SelectedAppId == SteamP2PRelayService.DefaultBg3AppId)
            PresetBg3Radio.IsChecked = true;
        else if (SelectedAppId == SteamP2PRelayService.SpacewarAppId)
            PresetSpacewarRadio.IsChecked = true;
        else
            PresetCustomRadio.IsChecked = true;
        _isUpdatingUi = false;
    }

    private void PresetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi) return;
        _isUpdatingUi = true;
        if (PresetBg3Radio.IsChecked == true)
        {
            AppIdTextBox.Text = SteamP2PRelayService.DefaultBg3AppId.ToString();
        }
        else if (PresetSpacewarRadio.IsChecked == true)
        {
            AppIdTextBox.Text = SteamP2PRelayService.SpacewarAppId.ToString();
        }
        else if (PresetCustomRadio.IsChecked == true)
        {
            AppIdTextBox.Focus();
            AppIdTextBox.SelectAll();
        }
        _isUpdatingUi = false;
    }

    private void AppIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi) return;
        _isUpdatingUi = true;
        string raw = AppIdTextBox.Text.Trim();
        if (uint.TryParse(raw, out uint val))
        {
            if (val == SteamP2PRelayService.DefaultBg3AppId)
                PresetBg3Radio.IsChecked = true;
            else if (val == SteamP2PRelayService.SpacewarAppId)
                PresetSpacewarRadio.IsChecked = true;
            else
                PresetCustomRadio.IsChecked = true;
        }
        _isUpdatingUi = false;
    }

    private void ResetDefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        PresetBg3Radio.IsChecked = true;
        AppIdTextBox.Text = SteamP2PRelayService.DefaultBg3AppId.ToString();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string raw = AppIdTextBox.Text.Trim();
        if (!uint.TryParse(raw, out uint parsedAppId) || parsedAppId == 0)
        {
            MessageBox.Show(
                this,
                "Please enter a valid positive numeric Steam AppID (e.g., 1086940 for Baldur's Gate 3 or 480 for Spacewar).",
                "Invalid Steam AppID",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedAppId = parsedAppId;
        _settings.SteamAppId = parsedAppId;
        _settings.Save();
        _logCallback($"[STEAM] Steam AppID updated to {SteamP2PRelayService.GetAppIdDisplayName(parsedAppId)}.");

        DialogResult = true;
        Close();
    }
}
