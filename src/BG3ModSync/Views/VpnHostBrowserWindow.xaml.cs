using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BG3ModSync.Services;

namespace BG3ModSync.Views;

public partial class VpnHostBrowserWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly VpnHostDiscoveryService _discoveryService;
    private readonly int _configuredNginxPort;
    private readonly Action<string> _logCallback;
    private readonly DispatcherTimer _autoRefreshTimer;
    private CancellationTokenSource? _scanCts;
    private bool _isRefreshing;

    public VpnDiscoveredHostInfo? SelectedHost { get; private set; }

    public VpnHostBrowserWindow(VpnHostDiscoveryService discoveryService, int configuredNginxPort, Action<string> logCallback)
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

        _discoveryService = discoveryService;
        _configuredNginxPort = configuredNginxPort > 0 ? configuredNginxPort : 27000;
        _logCallback = logCallback;

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.0) };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshHostsAsync();

        Loaded += async (_, _) =>
        {
            await RefreshHostsAsync();
            _autoRefreshTimer.Start();
        };

        Closed += (_, _) =>
        {
            _autoRefreshTimer.Stop();
            try { _scanCts?.Cancel(); } catch { }
            try { _scanCts?.Dispose(); } catch { }
            _scanCts = null;
        };
    }

    private async Task RefreshHostsAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        CancellationTokenSource? cts = null;
        try
        {
            RefreshHostsBtn.IsEnabled = false;
            ScanStateText.Text = "Scanning VPNs...";

            cts = new CancellationTokenSource();
            _scanCts = cts;
            var token = cts.Token;

            var localAll = NetworkDiscoveryService.GetLocalAndVpnInterfaces(_configuredNginxPort);
            var localVpns = localAll.Where(e => e.Category == NetworkCategory.VPN).ToList();

            if (localVpns.Count == 0)
            {
                VpnAdapterStatusText.Text = "No active VPN adapters detected. Connect Tailscale, ZeroTier, NetBird, Radmin VPN, Hamachi, WireGuard, or OpenVPN first.";
                HostsCountHeaderText.Text = "Online BG3ModSync Hosts (0)";
                HostsListBox.ItemsSource = null;
                EmptyStateText.Text = "No active VPN network interface is connected on this PC.\nConnect to your VPN first, then click ↻ Refresh.";
                EmptyStateText.Visibility = Visibility.Visible;
                ScanStateText.Text = "No VPN Adapter";
                return;
            }

            string adapterSummary = string.Join("  |  ", localVpns.Select(v =>
            {
                string prov = string.IsNullOrWhiteSpace(v.VpnProvider) ? "VPN" : v.VpnProvider;
                return $"{prov}: {v.IpAddress}";
            }));
            VpnAdapterStatusText.Text = $"Active VPNs: {adapterSummary}  •  UDP {VpnHostDiscoveryService.DiscoveryUdpPort} + HTTP :{_configuredNginxPort}";

            var found = await _discoveryService.DiscoverVpnHostsAsync(_configuredNginxPort, _logCallback, token);
            if (token.IsCancellationRequested) return;

            string? prevIp = (HostsListBox.SelectedItem as VpnDiscoveredHostInfo)?.VpnIp;
            HostsCountHeaderText.Text = $"Online BG3ModSync Hosts ({found.Count})";
            HostsListBox.ItemsSource = found;

            if (!string.IsNullOrWhiteSpace(prevIp))
            {
                HostsListBox.SelectedItem = found.FirstOrDefault(h => string.Equals(h.VpnIp, prevIp, StringComparison.OrdinalIgnoreCase));
            }
            if (HostsListBox.SelectedItem == null && found.Count > 0)
            {
                HostsListBox.SelectedIndex = 0;
            }

            if (found.Count == 0)
            {
                EmptyStateText.Text = $"No online BG3ModSync hosts found yet on your VPN networks.\nAsk your friend to press HOST in BG3ModSync (Port {_configuredNginxPort} or UDP {VpnHostDiscoveryService.DiscoveryUdpPort}).";
                EmptyStateText.Visibility = Visibility.Visible;
                ScanStateText.Text = $"Idle • 0 found ({DateTime.Now:HH:mm:ss})";
            }
            else
            {
                EmptyStateText.Visibility = Visibility.Collapsed;
                ScanStateText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed or cancelled
        }
        catch (Exception ex)
        {
            ScanStateText.Text = "Scan error";
            _logCallback($"[WARN] VPN host browser scan error: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;
            }
            cts?.Dispose();
            RefreshHostsBtn.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    private async void RefreshHostsBtn_Click(object sender, RoutedEventArgs e)
    {
        await RefreshHostsAsync();
    }

    private void HostsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HostsListBox.SelectedItem is VpnDiscoveredHostInfo)
        {
            SelectHostBtn_Click(sender, e);
        }
    }

    private void SelectHostBtn_Click(object sender, RoutedEventArgs e)
    {
        if (HostsListBox.SelectedItem is not VpnDiscoveredHostInfo host)
        {
            MessageBox.Show(this, "Select an online VPN Host from the list first.", "Find Host", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedHost = host;
        DialogResult = true;
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
