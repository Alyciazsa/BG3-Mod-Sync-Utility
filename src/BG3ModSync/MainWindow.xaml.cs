using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BG3ModSync.Models;
using BG3ModSync.Services;
using BG3ModSync.Views;

namespace BG3ModSync;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly AppSettings _settings;
    private readonly ModHashService _hashService = new();
    private readonly NginxServerService _nginxService = new();
    private readonly HttpSyncClientService _httpClientService = new();
    private readonly SteamP2PRelayService _steamService = new();
    private readonly BackupRestoreService _backupService = new();
    private readonly UpnpPortForwardService _upnpService = new();
    private readonly VpnHostDiscoveryService _vpnDiscoveryService = new();
    private readonly System.Net.Http.HttpClient _sharedTelemetryHttpClient =
        NetworkDiscoveryService.CreateOptimizedHttpClient(TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(900));

    private List<NetworkEndpointInfo> _allDiscoveredEndpoints = new();
    private List<ModHashEntry> _lastHostedEntries = new();
    private string _lastModHashFilePath = "";
    private CancellationTokenSource? _activeOperationCts;
    private CancellationTokenSource? _portDebounceCts;
    private CancellationTokenSource? _hostRealTimePingCts;
    private CancellationTokenSource? _clientDirectPingCts;
    private string? _activeLiveRouteOverride;
    private string? _lastConnectedHttpClientIp;
    private int _currentHostLivePingMs = -1;
    private int _currentClientToHostPingMs = -1;
    private double _currentLobbyTransferSpeedBytesPerSec;
    private bool _isHostSessionActive;
    private string _hostCustomStatusText = "";
    private bool _isClientConnectedToRemoteHost;
    private string _connectedHostIpPort = "";
    private string _connectedClientBaseHttpUrl = "";
    private string _observedClientSelfIpPort = "";
    private bool _localIsCgnat;
    private HostDirectEndpointsMetadata? _connectedHostMeta;
    private ClientLobbyTelemetry? _latestLocalClientTelemetry;
    private long _lastTelemetrySentTick;
    private int _telemetryHttpInFlight;
    private readonly ConcurrentDictionary<string, long> _lastHttpTelemetryTicksByKey = new();

    private readonly StringBuilder _logBuffer = new();
    private ActivityLogWindow? _logWindow;
    private readonly ObservableCollection<LobbyClientViewModel> _lobbyClients = new();
    private readonly ConcurrentDictionary<ulong, ImageSource?> _avatarCache = new();
    private ulong _currentHostAvatarSteamId;

    private bool _isBusy;
    private bool _isUiInitializing = true;
    private bool _isPopulatingCombo;
    private bool _isUpdatingTargetBoxProgrammatically;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;

        _settings = AppSettings.Load();
        NginxPortBox.Text = _settings.NginxPort.ToString();
        if (string.Equals(_settings.LaunchGraphicsApi, "DX11", StringComparison.OrdinalIgnoreCase))
        {
            PlayDx11Radio.IsChecked = true;
        }
        else
        {
            PlayVulkanRadio.IsChecked = true;
        }
        LobbyClientsItemsControl.ItemsSource = _lobbyClients;

        _steamService.OnLog += AppendLog;
        _steamService.OnLobbyHostJoinRequested += OnSteamLobbyHostJoinRequested;
        _steamService.OnRouteStatusChanged += OnSteamRouteStatusChanged;
        _steamService.OnClientTelemetryReceived += t => OnRemoteClientTelemetryReceived(t, fromHttp: false);
        _steamService.OnClientDisconnected += OnRemoteClientDisconnected;
        _nginxService.OnClientHttpConnected += OnDirectHttpClientConnected;
        _nginxService.OnClientTelemetryReceived += t => OnRemoteClientTelemetryReceived(t, fromHttp: true);

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        }
        catch
        {
            // Ignore on older OS builds
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppendLog("================================================================================");
        AppendLog("  BALDUR'S GATE 3 MOD SYNC UTILITY — PORTABLE NGINX & STEAM P2P EDITION");
        AppendLog("================================================================================");

        // First-time run (or if BG3 game folder is missing ..\Baldurs Gate 3\bin\bg3.exe): ask user to confirm auto-detected Mod & Game folders
        string? initialBg3Dir = PathResolverService.GetBg3InstallDir(_settings);
        if (!_settings.FirstRunLocationsConfirmed || !PathResolverService.IsValidBg3InstallDir(initialBg3Dir, out _))
        {
            var firstRunWin = new SettingsWindow(_settings, AppendLog, isFirstRunPrompt: true)
            {
                Owner = this
            };
            firstRunWin.ShowDialog();
        }

        string larianPath = PathResolverService.GetLarianBasePath(_settings);
        string? bg3Dir = PathResolverService.GetBg3InstallDir(_settings);
        var (pakCount, symlinkPakCount, modsSize, _) = BackupRestoreService.GetCurrentModsStatsWithSymlinks(_settings);

        string symlinkNote = symlinkPakCount > 0
            ? $" — includes {symlinkPakCount} Vortex symlinked files"
            : "";

        AppendLog($"[INIT] Larian AppData Path : {larianPath}");
        AppendLog($"[INIT] BG3 Game Directory  : {bg3Dir ?? "Not detected (Can be set in Select Game Locations)"}");
        AppendLog($"[INIT] Active Mods (.pak)  : {pakCount} files ({PathResolverService.FormatBytes(modsSize)}{symlinkNote})");
        AppendLog($"[INIT] Backup Location     : {_settings.GetEffectiveBackupRoot()}");
        AppendLog($"[INIT] Active Steam AppID  : {SteamP2PRelayService.GetAppIdDisplayName(_settings.SteamAppId)}");

        UpdateSteamAppIdButtonText();
        _steamService.SetConfiguredAppId(_settings.SteamAppId);
        bool steamRunning = SteamP2PRelayService.IsSteamProcessRunning();
        AppendLog($"[INIT] Steam Game Hook    : Standby (Hooks {SteamP2PRelayService.GetAppIdDisplayName(_settings.SteamAppId)} only on HOST / CLIENT)");

        // Do not reuse old Steam Room Codes across app restarts; start blank
        _settings.LastSteamTarget = "";

        _isUiInitializing = true;
        string savedMode = _settings.ConnectionMode;
        if (savedMode == "WAN_LAN")
            ModeWanLanIpRadio.IsChecked = true;
        else if (savedMode == "VPN")
            ModeVpnIpRadio.IsChecked = true;
        else if (savedMode == "SteamP2P")
            ModeSteamP2PRadio.IsChecked = true;
        else
            ModeSteamP2PRadio.IsChecked = steamRunning;

        if (ModeSteamP2PRadio.IsChecked != true && ModeWanLanIpRadio.IsChecked != true && ModeVpnIpRadio.IsChecked != true)
        {
            ModeWanLanIpRadio.IsChecked = true;
        }
        _isUiInitializing = false;

        await RefreshNetworkInterfacesAsync(logToConsole: true);
        UpdateModeUI();
        StartHostRealTimePingTimer();

        AppendLog("[READY] Select your Connection Mode (Steam Hole-Punch / WAN-LAN IP / VPN IP) and click HOST or CLIENT.");
        PathResolverService.CompactAndTrimWorkingSet();
    }

    private void PlayApiRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing || _settings == null) return;
        _settings.LaunchGraphicsApi = PlayDx11Radio.IsChecked == true ? "DX11" : "Vulkan";
        _settings.Save();
    }

    private async void PlayGameTopBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? bg3Dir = PathResolverService.GetBg3InstallDir(_settings);
            if (!PathResolverService.IsValidBg3InstallDir(bg3Dir, out _) || string.IsNullOrWhiteSpace(bg3Dir))
            {
                AppendLog("[WARN] Baldur's Gate 3 install directory is not configured or bin\\bg3.exe is missing. Opening Locations setup...");
                var locWin = new SettingsWindow(_settings, AppendLog, isFirstRunPrompt: true)
                {
                    Owner = this
                };
                if (locWin.ShowDialog() != true)
                {
                    return;
                }
                bg3Dir = PathResolverService.GetBg3InstallDir(_settings);
                if (!PathResolverService.IsValidBg3InstallDir(bg3Dir, out _) || string.IsNullOrWhiteSpace(bg3Dir))
                {
                    return;
                }
            }

            bool useDx11 = PlayDx11Radio.IsChecked == true;
            string exeName = useDx11 ? "bg3_dx11.exe" : "bg3.exe";
            string apiName = useDx11 ? "DirectX 11" : "Vulkan";

            string binDir = Path.Combine(bg3Dir, "bin");
            if (!Directory.Exists(binDir) && File.Exists(Path.Combine(bg3Dir, "bg3.exe")))
            {
                binDir = bg3Dir;
            }

            string exeFullPath = Path.Combine(binDir, exeName);
            if (!File.Exists(exeFullPath))
            {
                // Fallback to bg3.exe if bg3_dx11.exe is not present
                string fallbackPath = Path.Combine(binDir, "bg3.exe");
                if (File.Exists(fallbackPath))
                {
                    exeFullPath = fallbackPath;
                }
                else
                {
                    MessageBox.Show(
                        this,
                        $"Could not find '{exeFullPath}'. Please check your BG3 Game Folder location.",
                        "Game Executable Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            PlayGameTopBtn.IsEnabled = false;
            UpdateProgressUI($"Unhooking Steam API (returning status to Online) before starting {exeName}...", 50);

            // 1. Stop any active Steam P2P relay / client session and call SteamClient.Shutdown()
            // so Steam immediately sees that BG3ModSync is NOT playing any game and shows user as Online
            if (_isHostSessionActive || _nginxService.IsRunning || _isClientConnectedToRemoteHost)
            {
                await StopHostingAndOperationsAsync();
            }

            _steamService.ShutdownCurrentSession(AppendLog);
            UpdateTopRightStatusBadges("Steam Unhooked (Online — Ready to Play)");
            SteamBadgeText.Text = "Steam: Online (Unhooked for Game Launch)";
            SteamBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(108, 203, 95));

            // Wait briefly for Steam IPC pipe to release the AppID session cleanly so Steam shows Online
            await Task.Delay(700);

            // 2. Launch bg3.exe (Vulkan) or bg3_dx11.exe (DX11)
            AppendLog($"[PLAY] Launching Baldur's Gate 3 ({apiName}) -> {exeFullPath}");
            var psi = new ProcessStartInfo
            {
                FileName = exeFullPath,
                WorkingDirectory = binDir,
                UseShellExecute = true
            };
            Process.Start(psi);

            UpdateProgressUI($"Launched Baldur's Gate 3 ({apiName}: {exeName})", 100);
            PathResolverService.CompactAndTrimWorkingSet();
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Failed to launch Baldur's Gate 3: {ex.Message}");
            MessageBox.Show(
                this,
                $"Failed to launch Baldur's Gate 3:\n{ex.Message}",
                "Launch Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            PlayGameTopBtn.IsEnabled = true;
        }
    }

    private void StartHostRealTimePingTimer()
    {
        _hostRealTimePingCts?.Cancel();
        _hostRealTimePingCts = new CancellationTokenSource();
        var token = _hostRealTimePingCts.Token;

        _ = Task.Run(async () =>
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(850) };
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _steamService.RefreshNearestRelaySummary();

                    // Measure live ping for current Host connection / interface every 1s
                    int liveHostPing = await MeasureCurrentHostConnectionPingAsync(http, token);
                    if (liveHostPing >= 0)
                    {
                        _currentHostLivePingMs = liveHostPing;
                    }

                    // If Client is connected via Steam P2P, keep local Client ping & Host card ping synchronized every 1s
                    if (_isClientConnectedToRemoteHost && _steamService.IsClientTunnelActive)
                    {
                        if (_steamService.LastClientRttPingMs >= 0)
                            _currentClientToHostPingMs = _steamService.LastClientRttPingMs;
                        if (!string.IsNullOrWhiteSpace(_steamService.PunchedHostEndpoint))
                            _connectedHostIpPort = _steamService.PunchedHostEndpoint;
                        if (_steamService.LastHandshakedHostEndpoints != null)
                            _connectedHostMeta = _steamService.LastHandshakedHostEndpoints;
                        if (!string.IsNullOrWhiteSpace(_steamService.ObservedClientSelfIpPort))
                            _observedClientSelfIpPort = _steamService.ObservedClientSelfIpPort;

                        if (_latestLocalClientTelemetry != null)
                        {
                            _latestLocalClientTelemetry.PingMs = _currentClientToHostPingMs;
                            // Keep Client's own real IP:Port (NEVER overwrite with _connectedHostIpPort!)
                            if (!string.IsNullOrWhiteSpace(_observedClientSelfIpPort))
                                _latestLocalClientTelemetry.IpAndPort = _observedClientSelfIpPort;
                            _latestLocalClientTelemetry.IsCgnat = _localIsCgnat || _steamService.LastConnectionObservedRelay;
                            UpsertClientInLobby(_latestLocalClientTelemetry);

                            // If Stage 1 Direct HTTP Handoff closed the temporary P2P signaling socket,
                            // keep sending 1s live ping/telemetry to Host over Direct HTTP!
                            if (!_steamService.HasActiveP2PDataConnection && !string.IsNullOrWhiteSpace(_connectedClientBaseHttpUrl))
                            {
                                await SendClientTelemetryOverHttpAsync(_connectedClientBaseHttpUrl, _latestLocalClientTelemetry, _sharedTelemetryHttpClient, token);
                            }
                        }
                    }

                    // If Host has an active Direct HTTP client connected (and no active P2P client overriding it), ping that client every 1s
                    if (_nginxService.IsRunning &&
                        !string.IsNullOrEmpty(_lastConnectedHttpClientIp) &&
                        !_steamService.HasConnectedP2PClients &&
                        !_steamService.IsClientTunnelActive &&
                        _clientDirectPingCts == null)
                    {
                        string clientIpOnly = ExtractIpOnly(_lastConnectedHttpClientIp);
                        int clientPing = await SteamP2PRelayService.MeasureEndpointPingMsAsync(
                            $"{clientIpOnly}:{_nginxService.ActivePort}",
                            null,
                            http,
                            token,
                            allowTcpFallback: false);
                        if (clientPing < 0) clientPing = 1;

                        string classified = NetworkDiscoveryService.ClassifyRemoteOrTargetIp(clientIpOnly, _allDiscoveredEndpoints);
                        _activeLiveRouteOverride = $"{classified} ({_lastConnectedHttpClientIp}) • Ping: {clientPing}ms";

                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            foreach (var c in _lobbyClients)
                            {
                                if (!c.IsSteamMode)
                                {
                                    c.PingDisplay = $"{clientPing} ms";
                                }
                            }
                        });
                    }

                    UpdateTopRightStatusBadges(_activeLiveRouteOverride);
                    UpdateHostLobbyCardUI();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient ping error
                }

                await Task.Delay(1000, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, token);
    }

    private async Task<int> MeasureCurrentHostConnectionPingAsync(System.Net.Http.HttpClient sharedHttp, CancellationToken token)
    {
        try
        {
            if (_nginxService.IsRunning)
            {
                int localNginxPing = await SteamP2PRelayService.MeasureEndpointPingMsAsync(
                    $"127.0.0.1:{_nginxService.ActivePort}",
                    $"http://127.0.0.1:{_nginxService.ActivePort}",
                    sharedHttp,
                    token);
                if (localNginxPing >= 0)
                    return localNginxPing;
            }

            var selectedEp = Dispatcher.Invoke(() =>
                InterfacesComboBox.SelectedItem as NetworkEndpointInfo ?? _allDiscoveredEndpoints.FirstOrDefault());

            if (selectedEp != null && !string.IsNullOrWhiteSpace(selectedEp.IpAddress))
            {
                string targetIp = selectedEp.Category == NetworkCategory.WAN ? "1.1.1.1" : selectedEp.IpAddress;
                int epPing = await SteamP2PRelayService.MeasureEndpointPingMsAsync(
                    $"{targetIp}:{_settings.NginxPort}",
                    null,
                    sharedHttp,
                    token,
                    allowTcpFallback: false);
                if (epPing >= 0)
                    return epPing;
            }

            int fallbackPing = await SteamP2PRelayService.MeasureEndpointPingMsAsync("127.0.0.1:27000", null, sharedHttp, token, allowTcpFallback: false);
            return fallbackPing >= 0 ? fallbackPing : 1;
        }
        catch
        {
            return _currentHostLivePingMs >= 0 ? _currentHostLivePingMs : 1;
        }
    }

    private void StartClientDirectHttpPingLoop(string baseHttpUrl, string routeDesc)
    {
        _clientDirectPingCts?.Cancel();
        _clientDirectPingCts = new CancellationTokenSource();
        var token = _clientDirectPingCts.Token;

        string hostIpPort = baseHttpUrl.Trim();
        if (hostIpPort.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            hostIpPort = hostIpPort.Substring(7);
        else if (hostIpPort.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            hostIpPort = hostIpPort.Substring(8);
        hostIpPort = hostIpPort.TrimEnd('/');

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int pingMs = await SteamP2PRelayService.MeasureEndpointPingMsAsync(hostIpPort, baseHttpUrl, _sharedTelemetryHttpClient, token);
                    if (pingMs < 0) pingMs = 1;
                    _currentClientToHostPingMs = pingMs;

                    if (_latestLocalClientTelemetry != null)
                    {
                        _latestLocalClientTelemetry.PingMs = pingMs;
                        UpsertClientInLobby(_latestLocalClientTelemetry);
                        await SendClientTelemetryOverHttpAsync(baseHttpUrl, _latestLocalClientTelemetry, _sharedTelemetryHttpClient, token, forceImmediate: false);
                    }

                    string liveText = $"{routeDesc} -> {hostIpPort} • Ping: {pingMs}ms";
                    _activeLiveRouteOverride = liveText;
                    UpdateTopRightStatusBadges(liveText);
                    UpdateHostLobbyCardUI();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient error
                }

                await Task.Delay(1000, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, token);
    }

    private void StopClientDirectHttpPingLoop()
    {
        try
        {
            _clientDirectPingCts?.Cancel();
            _clientDirectPingCts = null;
        }
        catch { }
    }

    private async Task RefreshNetworkInterfacesAsync(bool logToConsole)
    {
        int port = int.TryParse(NginxPortBox.Text.Trim(), out int p) && p > 0 ? p : _settings.NginxPort;
        if (logToConsole)
        {
            AppendLog("[NET]  Scanning available WAN, LAN, and VPN network interfaces...");
        }

        _allDiscoveredEndpoints = await NetworkDiscoveryService.DiscoverAllEndpointsAsync(port);

        if (logToConsole)
        {
            if (_allDiscoveredEndpoints.Count == 0)
            {
                AppendLog("[NET]  No active external network interfaces found (Loopback 127.0.0.1 available).");
            }
            else
            {
                foreach (var ep in _allDiscoveredEndpoints)
                {
                    AppendLog($"[NET]  {ep.CategoryBadge,-6} {ep.AdapterName,-28} : {ep.IpAddress} ({ep.Description})");
                }
            }
        }

        try
        {
            var (isCgnat, cgnatReason) = await NetworkDiscoveryService.DetectLocalCgnatAsync(_allDiscoveredEndpoints);
            _localIsCgnat = isCgnat;
            if (logToConsole)
            {
                string badge = isCgnat ? "⚠ CGNAT DETECTED" : "Direct / Standard NAT";
                AppendLog($"[NET]  NAT/CGNAT Status : {badge} — {cgnatReason}");
            }
        }
        catch { }

        PopulateInterfacesComboForCurrentMode();
        if (_isHostSessionActive || _nginxService.IsRunning)
        {
            _vpnDiscoveryService.SetPeriodicBroadcastEnabled(ModeVpnIpRadio.IsChecked == true, _allDiscoveredEndpoints);
        }
        UpdateTopRightStatusBadges();
    }

    private void PopulateInterfacesComboForCurrentMode()
    {
        _isPopulatingCombo = true;
        try
        {
            List<NetworkEndpointInfo> filtered;
            if (ModeVpnIpRadio.IsChecked == true)
            {
                filtered = _allDiscoveredEndpoints.Where(e => e.Category == NetworkCategory.VPN).ToList();
            }
            else if (ModeWanLanIpRadio.IsChecked == true)
            {
                filtered = _allDiscoveredEndpoints.Where(e => e.Category == NetworkCategory.WAN || e.Category == NetworkCategory.LAN).ToList();
            }
            else
            {
                filtered = new List<NetworkEndpointInfo>();
            }

            InterfacesComboBox.ItemsSource = filtered;
            if (filtered.Count > 0)
            {
                int defaultIdx = 0;
                if (ModeWanLanIpRadio.IsChecked == true)
                {
                    int lanIdx = filtered.FindIndex(e => e.Category == NetworkCategory.LAN);
                    if (lanIdx >= 0) defaultIdx = lanIdx;
                }
                InterfacesComboBox.SelectedIndex = defaultIdx;
            }
        }
        finally
        {
            _isPopulatingCombo = false;
        }
    }

    private void SetTargetBoxProgrammatically(string value)
    {
        _isUpdatingTargetBoxProgrammatically = true;
        try
        {
            HostTargetTextBox.Text = value;
        }
        finally
        {
            _isUpdatingTargetBoxProgrammatically = false;
        }
    }

    private void UpdateModeUI()
    {
        PopulateInterfacesComboForCurrentMode();
        var selectedEp = InterfacesComboBox.SelectedItem as NetworkEndpointInfo;

        if (ModeSteamP2PRadio.IsChecked == true)
        {
            _settings.ConnectionMode = "SteamP2P";
            SelectableIpRow.Visibility = Visibility.Collapsed;
            UpnpForwardBtn.Visibility = Visibility.Collapsed;
            PortLabel.Visibility = Visibility.Collapsed;
            NginxPortBox.Visibility = Visibility.Collapsed;
            CopySteamIdBtn.Visibility = Visibility.Visible;
            SelectHostBtn.Visibility = Visibility.Visible;
            SelectHostBtn.Content = "🎯 Select Host";
            SelectHostBtn.ToolTip = "Select an online Steam friend using the same Game / AppID as your Host.";

            TargetInputLabel.Text = "Room / ID:";
            TargetInputLabel.ToolTip = "Host's Session Room Code (BG3-...) or 17-digit SteamID64.";
            HostTargetTextBox.ToolTip = "Enter Host Session Room Code (BG3-...) or 17-digit Host SteamID64.";
            CopyHostInfoBtn.Content = "📋 Copy Code";
            CopyHostInfoBtn.ToolTip = "Copy your current Session Room Code (BG3-...) to clipboard.";

            string steamTarget = (_isHostSessionActive || _nginxService.IsRunning) && _steamService.IsSteamInitialized
                ? _steamService.LocalRoomCode
                : (_settings.LastSteamTarget ?? "");
            SetTargetBoxProgrammatically(steamTarget);
        }
        else if (ModeVpnIpRadio.IsChecked == true)
        {
            _settings.ConnectionMode = "VPN";
            SelectableIpRow.Visibility = Visibility.Visible;
            SelectableIpLabel.Text = "VPN IP:";
            UpnpForwardBtn.Visibility = Visibility.Collapsed;
            PortLabel.Visibility = Visibility.Visible;
            NginxPortBox.Visibility = Visibility.Visible;
            CopySteamIdBtn.Visibility = Visibility.Collapsed;
            SelectHostBtn.Visibility = Visibility.Visible;
            SelectHostBtn.Content = "🔍 Find Host";
            SelectHostBtn.ToolTip = "Find online BG3ModSync Hosts on your active VPN networks (Tailscale, ZeroTier, NetBird, Radmin, Hamachi, WireGuard, OpenVPN).";
            RelayWarningBanner.Visibility = Visibility.Collapsed;

            TargetInputLabel.Text = "VPN IP:";
            TargetInputLabel.ToolTip = "Host's virtual VPN IPv4 address.";
            HostTargetTextBox.ToolTip = "Enter Host VPN IPv4 Address (or click Find Host to scan Tailscale, ZeroTier, NetBird, Radmin, Hamachi, WireGuard, OpenVPN).";
            CopyHostInfoBtn.Content = "📋 Copy IP";
            CopyHostInfoBtn.ToolTip = "Copy your selected VPN IP address to clipboard.";

            string vpnIp = !string.IsNullOrWhiteSpace(_settings.LastVpnIp)
                ? _settings.LastVpnIp
                : (selectedEp?.IpAddress ?? "");
            SetTargetBoxProgrammatically(vpnIp);
        }
        else
        {
            _settings.ConnectionMode = "WAN_LAN";
            SelectableIpRow.Visibility = Visibility.Visible;
            SelectableIpLabel.Text = "IP:";
            PortLabel.Visibility = Visibility.Visible;
            NginxPortBox.Visibility = Visibility.Visible;
            CopySteamIdBtn.Visibility = Visibility.Collapsed;
            SelectHostBtn.Visibility = Visibility.Collapsed;
            SelectHostBtn.Content = "🎯 Select Host";
            RelayWarningBanner.Visibility = Visibility.Collapsed;

            TargetInputLabel.Text = "Host IP:";
            TargetInputLabel.ToolTip = "Host's Public WAN IP or Local LAN IPv4 address.";
            HostTargetTextBox.ToolTip = "Enter Host Public WAN IP or Local LAN IPv4 Address (e.g. 192.168.1.50).";
            CopyHostInfoBtn.Content = "📋 Copy IP";
            CopyHostInfoBtn.ToolTip = "Copy your selected WAN or LAN IP address to clipboard.";

            string wanLanIp = !string.IsNullOrWhiteSpace(_settings.LastWanLanIp)
                ? _settings.LastWanLanIp
                : (selectedEp?.IpAddress ?? "");
            SetTargetBoxProgrammatically(wanLanIp);
        }

        _settings.Save();
        UpdateUpnpButtonVisibility();
        UpdateTopRightStatusBadges();
        UpdateHostLobbyCardUI();
    }

    private void HostTargetTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUiInitializing || _isUpdatingTargetBoxProgrammatically) return;
        string val = HostTargetTextBox.Text.Trim();
        _settings.LastHostTarget = val;

        if (ModeSteamP2PRadio.IsChecked == true)
            _settings.LastSteamTarget = val;
        else if (ModeVpnIpRadio.IsChecked == true)
            _settings.LastVpnIp = val;
        else
            _settings.LastWanLanIp = val;
    }

    /// <summary>
    /// Every time the port in the box changes, waits 5 seconds to finalize,
    /// dynamically updates Host Nginx if running, and attempts UPnP port forwarding.
    /// </summary>
    private async void NginxPortBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUiInitializing) return;

        _portDebounceCts?.Cancel();
        _portDebounceCts = new CancellationTokenSource();
        var token = _portDebounceCts.Token;

        string rawPort = NginxPortBox.Text.Trim();
        if (!int.TryParse(rawPort, out int newPort) || newPort <= 0 || newPort > 65535)
        {
            return;
        }

        UpdateProgressUI($"Port changed to {newPort} — waiting 5s to finalize...", 0);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested) return;

        int previousPort = _settings.NginxPort;
        _settings.NginxPort = newPort;
        _settings.Save();

        foreach (var ep in _allDiscoveredEndpoints)
        {
            ep.Port = newPort;
        }

        AppendLog($"[PORT] Port finalized to {newPort} (after 5s debounce).");

        bool isSteamMode = ModeSteamP2PRadio.IsChecked == true;
        // If Host Portable Nginx server is currently running in Direct IP / VPN mode on a different port, dynamically restart it!
        if (!isSteamMode && _nginxService.IsRunning && _nginxService.ActivePort != newPort && !string.IsNullOrEmpty(_lastModHashFilePath))
        {
            AppendLog($"[NGINX] Dynamically restarting Portable Nginx from port {previousPort} -> {newPort}...");
            _steamService.UpdateHostTargetNginxPort(newPort, _allDiscoveredEndpoints);
            bool restarted = await _nginxService.StartServerAsync(_settings, _lastModHashFilePath, _lastHostedEntries, AppendLog, _steamService.LocalHostEndpoints, useAutoPort: false);
            if (restarted)
            {
                _vpnDiscoveryService.UpdateHostBeaconMetadata(
                    _nginxService.ActivePort,
                    _allDiscoveredEndpoints,
                    _lastHostedEntries.Count,
                    _lastHostedEntries.Sum(entry => entry.FileSize),
                    string.IsNullOrWhiteSpace(_hostCustomStatusText) ? "Hosting" : _hostCustomStatusText,
                    ModeVpnIpRadio.IsChecked == true,
                    _steamService.LocalHostEndpoints.HostName);
                ServerStatusTitle.Text = $"Server: Hosting (Port {_nginxService.ActivePort})";
                HostBtnSubtitle.Text = $"Serving {_lastHostedEntries.Count} files on Port {_nginxService.ActivePort}";
                UpdateHostLobbyCardUI();
            }
        }
        else if (_isHostSessionActive || _nginxService.IsRunning)
        {
            _vpnDiscoveryService.UpdateHostBeaconMetadata(
                _nginxService.IsRunning ? _nginxService.ActivePort : newPort,
                _allDiscoveredEndpoints,
                _lastHostedEntries.Count,
                _lastHostedEntries.Sum(entry => entry.FileSize),
                string.IsNullOrWhiteSpace(_hostCustomStatusText) ? "Hosting" : _hostCustomStatusText,
                ModeVpnIpRadio.IsChecked == true);
        }

        // Remove any previous UPnP port mapping, and ONLY forward via UPnP if Direct WAN IP is selected (never in VPN mode!)
        await _upnpService.RemovePortForwardAsync();
        if (ModeWanLanIpRadio.IsChecked == true &&
            InterfacesComboBox.SelectedItem is NetworkEndpointInfo selWan &&
            selWan.Category == NetworkCategory.WAN)
        {
            await TriggerUpnpPortForwardAsync();
        }
        UpdateTopRightStatusBadges();
        UpdateProgressUI($"Port {newPort} finalized", 100);
    }

    private void UpdateUpnpButtonVisibility()
    {
        if (ModeWanLanIpRadio.IsChecked == true &&
            InterfacesComboBox.SelectedItem is NetworkEndpointInfo selected &&
            selected.Category == NetworkCategory.WAN)
        {
            UpnpForwardBtn.Visibility = Visibility.Visible;
        }
        else
        {
            UpnpForwardBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateTopRightStatusBadges(string? activeLiveRouteOverride = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateTopRightStatusBadges(activeLiveRouteOverride));
            return;
        }

        if (activeLiveRouteOverride != null)
        {
            _activeLiveRouteOverride = string.IsNullOrWhiteSpace(activeLiveRouteOverride) ? null : activeLiveRouteOverride;
        }

        string? effectiveOverride = _activeLiveRouteOverride;

        int wanCount = _allDiscoveredEndpoints.Count(e => e.Category == NetworkCategory.WAN);
        int lanCount = _allDiscoveredEndpoints.Count(e => e.Category == NetworkCategory.LAN);
        int vpnCount = _allDiscoveredEndpoints.Count(e => e.Category == NetworkCategory.VPN);

        var selectedEp = InterfacesComboBox.SelectedItem as NetworkEndpointInfo ?? _allDiscoveredEndpoints.FirstOrDefault();
        string relaySummary = !string.IsNullOrEmpty(_steamService.NearestRelaySummary)
            ? _steamService.NearestRelaySummary
            : "Measuring...";
        string hostPingSuffix = _currentHostLivePingMs >= 0 ? $" • Ping: {_currentHostLivePingMs}ms" : "";

        bool steamProcRunning = _steamService.IsSteamInitialized || SteamP2PRelayService.IsSteamProcessRunning();

        if (!string.IsNullOrWhiteSpace(effectiveOverride))
        {
            ConnectionRouteText.Text = $"From: {effectiveOverride}";
            ConnectionRouteText.Foreground = effectiveOverride.Contains("Relay", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(255, 216, 107))
                : new SolidColorBrush(Color.FromRgb(108, 203, 95));
        }
        else if (ModeSteamP2PRadio.IsChecked == true)
        {
            if (_steamService.IsSteamInitialized)
            {
                string relaySuffix = !string.IsNullOrEmpty(_steamService.NearestRelaySummary)
                    ? $" • Relay: {_steamService.NearestRelaySummary}"
                    : "";
                ConnectionRouteText.Text = $"Mode: Steam Hole-Punch{relaySuffix}{hostPingSuffix}";
            }
            else
            {
                ConnectionRouteText.Text = steamProcRunning
                    ? $"Mode: Steam Hole-Punch (Standby — Hooks on Host/Client){hostPingSuffix}"
                    : $"Mode: Steam P2P (Steam Client Offline){hostPingSuffix}";
            }
            ConnectionRouteText.Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255));
        }
        else if (ModeVpnIpRadio.IsChecked == true)
        {
            var vpnEp = selectedEp?.Category == NetworkCategory.VPN
                ? selectedEp
                : _allDiscoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.VPN);

            ConnectionRouteText.Text = vpnEp != null
                ? $"Mode: Direct VPN IP ({vpnEp.AdapterName} • {vpnEp.IpAddress}:{_settings.NginxPort}){hostPingSuffix}"
                : $"Mode: Direct VPN IP (No VPN Adapter Detected){hostPingSuffix}";
            ConnectionRouteText.Foreground = new SolidColorBrush(Color.FromRgb(230, 184, 92));
        }
        else
        {
            if (selectedEp != null && selectedEp.Category == NetworkCategory.WAN)
            {
                string upnpState = _upnpService.IsPortForwarded ? "UPnP Port Forwarded" : "UPnP Ready";
                ConnectionRouteText.Text = $"Mode: Direct WAN IP ({selectedEp.IpAddress}:{_settings.NginxPort} • {upnpState}){hostPingSuffix}";
            }
            else
            {
                string epText = selectedEp != null ? $"{selectedEp.CategoryBadge} {selectedEp.IpAddress}:{_settings.NginxPort}" : "Direct IP";
                ConnectionRouteText.Text = $"Mode: Direct IP ({epText}){hostPingSuffix}";
            }
            ConnectionRouteText.Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255));
        }

        string steamStatus = _steamService.IsSteamInitialized
            ? $"Relay: {relaySummary}"
            : (steamProcRunning ? "Steam: Standby" : "Steam: Offline");
        SteamBadgeText.Text = $"{steamStatus}{hostPingSuffix}  •  Interfaces: {wanCount} WAN, {lanCount} LAN, {vpnCount} VPN";

        if (ModeSteamP2PRadio.IsChecked == true)
        {
            HostShareSummaryText.Text = _steamService.IsSteamInitialized
                ? $"Room Code: {_steamService.LocalRoomCode} | SteamID: {_steamService.LocalSteamId} | Relay: {relaySummary}"
                : (steamProcRunning
                    ? $"Steam P2P Standby — Press HOST to create Room Code (AppID {_settings.SteamAppId})"
                    : "Steam P2P Mode (Start Steam)");
        }
        else
        {
            string summaryIp = selectedEp != null ? $"{selectedEp.CategoryBadge} {selectedEp.IpAddress}:{_settings.NginxPort}" : $"Port {_settings.NginxPort}";
            HostShareSummaryText.Text = $"Selected Endpoint: {summaryIp}";
        }
    }

    private static string ExtractIpOnly(string? ipOrEndpoint)
    {
        if (string.IsNullOrWhiteSpace(ipOrEndpoint)) return "";
        string s = ipOrEndpoint.Trim();
        int idx = s.LastIndexOf(':');
        return idx > 0 ? s.Substring(0, idx) : s;
    }

    private void OnSteamRouteStatusChanged(string routeDescription, bool isRelayMode, string relayServerAndPing)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _activeLiveRouteOverride = string.IsNullOrWhiteSpace(routeDescription) ? null : routeDescription;
            UpdateTopRightStatusBadges(routeDescription);

            // Only show the yellow warning banner when Direct-Only Policy ACTUALLY blocked transfer (never on Host or transient signaling!).
            bool isBlockedRelay = isRelayMode &&
                                  !string.IsNullOrWhiteSpace(routeDescription) &&
                                  routeDescription.Contains("Blocked", StringComparison.OrdinalIgnoreCase);

            if (isBlockedRelay)
            {
                RelayWarningBanner.Visibility = Visibility.Visible;
                RelayWarningText.Text = $"⚠ Warning: Direct Hole-Punch & Direct HTTP were blocked by NAT/Firewall (Fell back to Steam Relay [{relayServerAndPing}]). Bulk .pak transfer over Steam Relay is blocked to avoid relay throttling/abuse.";
            }
            else
            {
                RelayWarningBanner.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnDirectHttpClientConnected(string remoteClientEndpoint)
    {
        string remoteIpOnly = ExtractIpOnly(remoteClientEndpoint);
        if (string.IsNullOrWhiteSpace(remoteIpOnly)) return;

        Dispatcher.BeginInvoke(() =>
        {
            bool endpointChanged = !string.Equals(_lastConnectedHttpClientIp, remoteClientEndpoint, StringComparison.OrdinalIgnoreCase);
            _lastConnectedHttpClientIp = remoteClientEndpoint;
            RelayWarningBanner.Visibility = Visibility.Collapsed;

            // If Steam P2P is active on Host or Client, do NOT overwrite route status or create duplicate HTTP client entries!
            if (_steamService.HasConnectedP2PClients || _steamService.IsClientTunnelActive || _isClientConnectedToRemoteHost)
            {
                return;
            }

            if (endpointChanged || string.IsNullOrWhiteSpace(_activeLiveRouteOverride))
            {
                string classified = NetworkDiscoveryService.ClassifyRemoteOrTargetIp(remoteIpOnly, _allDiscoveredEndpoints);
                int ping = _currentHostLivePingMs >= 0 ? _currentHostLivePingMs : 1;
                string overrideText = $"{classified} ({remoteClientEndpoint}) • Ping: {ping}ms";
                _activeLiveRouteOverride = overrideText;
                UpdateTopRightStatusBadges(overrideText);
            }

            // Ensure Direct HTTP client appears in Lobby with its real source IP:Port (NEVER Host's ActivePort!)
            if (_lobbyClients.Count == 0)
            {
                int ping = _currentHostLivePingMs >= 0 ? _currentHostLivePingMs : 1;
                UpsertClientInLobby(new ClientLobbyTelemetry
                {
                    ClientKey = $"http_{remoteIpOnly}",
                    SteamId = 0,
                    Name = $"Client ({remoteIpOnly})",
                    IpAndPort = remoteClientEndpoint,
                    PingMs = ping,
                    StageText = "Connected via Direct HTTP",
                    ProgressPercent = 0,
                    SpeedBytesPerSec = 0,
                    IsSteamMode = false
                });
            }
        });
    }

    private void OnRemoteClientTelemetryReceived(ClientLobbyTelemetry telemetry, bool fromHttp = false)
    {
        if (fromHttp && !string.IsNullOrWhiteSpace(telemetry.ClientKey))
        {
            _lastHttpTelemetryTicksByKey[telemetry.ClientKey] = Environment.TickCount64;
        }
        Dispatcher.BeginInvoke(() =>
        {
            UpsertClientInLobby(telemetry);
        });
    }

    private void OnRemoteClientDisconnected(string clientKey)
    {
        // If this client recently switched from temporary Stage 2 P2P signaling to Stage 1 Direct HTTP Handoff,
        // do NOT remove its live Lobby card!
        if (!string.IsNullOrWhiteSpace(clientKey) &&
            _lastHttpTelemetryTicksByKey.TryGetValue(clientKey, out long lastHttpTs) &&
            (Environment.TickCount64 - lastHttpTs) < 5000)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var existing = _lobbyClients.FirstOrDefault(c => c.ClientKey == clientKey);
            if (existing != null)
            {
                _lobbyClients.Remove(existing);
            }
            _currentLobbyTransferSpeedBytesPerSec = 0;
            UpdateHostLobbyCardUI();
        });
    }

    private void UpsertClientInLobby(ClientLobbyTelemetry telemetry)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpsertClientInLobby(telemetry));
            return;
        }

        string incomingIpOnly = ExtractIpOnly(telemetry.IpAndPort);
        if (string.IsNullOrWhiteSpace(telemetry.ClientKey))
        {
            telemetry.ClientKey = telemetry.SteamId > 0 ? $"steam_{telemetry.SteamId}" : $"client_{incomingIpOnly}";
        }

        // Find existing client row by ClientKey, SteamId, Name, same source IP (ignoring ephemeral port change), or temporary placeholder
        var vm = _lobbyClients.FirstOrDefault(c =>
            c.ClientKey == telemetry.ClientKey ||
            (telemetry.SteamId > 0 && c.SteamId == telemetry.SteamId) ||
            (!string.IsNullOrWhiteSpace(telemetry.Name) && string.Equals(c.Name, telemetry.Name, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(incomingIpOnly) && string.Equals(ExtractIpOnly(c.IpPortDisplay), incomingIpOnly, StringComparison.OrdinalIgnoreCase)) ||
            c.ClientKey.StartsWith("conn_", StringComparison.OrdinalIgnoreCase) ||
            c.ClientKey.StartsWith("http_", StringComparison.OrdinalIgnoreCase));

        if (vm == null)
        {
            vm = new LobbyClientViewModel { ClientKey = telemetry.ClientKey };
            _lobbyClients.Add(vm);
        }
        else
        {
            vm.ClientKey = telemetry.ClientKey;
        }

        // Remove any other lingering duplicate rows for the same SteamId, IP, or temporary placeholders
        for (int i = _lobbyClients.Count - 1; i >= 0; i--)
        {
            var item = _lobbyClients[i];
            if (!ReferenceEquals(item, vm) &&
                ((telemetry.SteamId > 0 && item.SteamId == telemetry.SteamId) ||
                 item.ClientKey == telemetry.ClientKey ||
                 (!string.IsNullOrWhiteSpace(incomingIpOnly) && string.Equals(ExtractIpOnly(item.IpPortDisplay), incomingIpOnly, StringComparison.OrdinalIgnoreCase)) ||
                 item.ClientKey.StartsWith("conn_", StringComparison.OrdinalIgnoreCase) ||
                 item.ClientKey.StartsWith("http_", StringComparison.OrdinalIgnoreCase)))
            {
                if (item.IsCgnat) vm.IsCgnat = true;
                _lobbyClients.RemoveAt(i);
            }
        }

        vm.IsSteamMode = telemetry.IsSteamMode;
        if (telemetry.IsCgnat)
        {
            vm.IsCgnat = true;
        }

        if (telemetry.IsSteamMode && telemetry.SteamId > 0)
        {
            vm.SteamId = telemetry.SteamId;
            vm.IdDisplay = $"ID: {telemetry.SteamId}";
        }
        else
        {
            vm.SteamId = 0;
            vm.IdDisplay = "";
            vm.AvatarSource = null;
        }

        if (!string.IsNullOrWhiteSpace(telemetry.Name))
            vm.Name = telemetry.Name;
        if (!string.IsNullOrWhiteSpace(telemetry.IpAndPort))
            vm.IpPortDisplay = telemetry.IpAndPort;
        vm.PingDisplay = telemetry.PingMs >= 0 ? $"{telemetry.PingMs} ms" : "1 ms";
        vm.SpeedDisplay = LobbyClientViewModel.FormatSpeed(telemetry.SpeedBytesPerSec);
        vm.ProgressPercent = telemetry.ProgressPercent;
        if (!string.IsNullOrWhiteSpace(telemetry.StageText))
            vm.StageText = telemetry.StageText;

        // Do not show Ping & Speed of oneself on the Client row when acting as Client
        vm.IsSelf = _isClientConnectedToRemoteHost && !_nginxService.IsRunning && !_isHostSessionActive;

        if (telemetry.IsSteamMode && vm.SteamId > 0 && vm.AvatarSource == null)
        {
            _ = LoadAndAssignAvatarAsync(vm.SteamId, avatar => vm.AvatarSource = avatar);
        }

        _currentLobbyTransferSpeedBytesPerSec = telemetry.SpeedBytesPerSec;
        UpdateHostLobbyCardUI();
    }

    private async Task LoadAndAssignAvatarAsync(ulong steamId, Action<ImageSource?> assignAction)
    {
        if (steamId == 0 || !_steamService.IsSteamInitialized) return;
        if (_avatarCache.TryGetValue(steamId, out var cached) && cached != null)
        {
            await Dispatcher.InvokeAsync(() => assignAction(cached));
            return;
        }

        var bmp = await _steamService.GetSteamAvatarBitmapAsync(steamId);
        if (bmp != null)
        {
            _avatarCache[steamId] = bmp;
            await Dispatcher.InvokeAsync(() => assignAction(bmp));
        }
    }

    private void UpdateHostLobbyCardUI()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateHostLobbyCardUI);
            return;
        }

        bool isSessionActive = _isHostSessionActive || _isClientConnectedToRemoteHost || _nginxService.IsRunning;
        if (!isSessionActive)
        {
            HostLobbyCardBorder.Visibility = Visibility.Collapsed;
            LobbyEmptyStateText.Text = "Session Lobby is empty. Click HOST to start hosting or CLIENT to join a session.";
            LobbyEmptyStateText.Visibility = _lobbyClients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LobbyStatusSummaryText.Text = "Waiting for session...";
            return;
        }

        HostLobbyCardBorder.Visibility = Visibility.Visible;
        LobbyEmptyStateText.Text = "No clients connected in lobby yet. Waiting for client to join...";
        LobbyEmptyStateText.Visibility = _lobbyClients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool isSteamMode = ModeSteamP2PRadio.IsChecked == true;
        var selectedEp = InterfacesComboBox.SelectedItem as NetworkEndpointInfo ?? _allDiscoveredEndpoints.FirstOrDefault();

        // Case 1: Client is actively connected to a remote Host (and not local hosting only)
        if (_isClientConnectedToRemoteHost && !_nginxService.IsRunning && !_isHostSessionActive)
        {
            // Client views the remote Host card -> show Ping & Speed to Host!
            HostLobbyStatsPanel.Visibility = Visibility.Visible;

            bool isClientInSteamMode = _steamService.IsClientTunnelActive && isSteamMode;
            ulong hostSteamId = isClientInSteamMode ? (_connectedHostMeta?.HostSteamId ?? 0) : 0;
            string hostName = _connectedHostMeta?.HostName ?? "";
            if (string.IsNullOrWhiteSpace(hostName))
            {
                hostName = hostSteamId > 0 ? $"Steam Host ({hostSteamId})" : $"Host ({_connectedHostIpPort})";
            }

            HostLobbyNameText.Text = hostName;
            HostLobbyIdText.Text = (isClientInSteamMode && hostSteamId > 0) ? $"ID: {hostSteamId}" : "";
            HostLobbyIpPortText.Text = !string.IsNullOrWhiteSpace(_connectedHostIpPort) ? _connectedHostIpPort : HostTargetTextBox.Text.Trim();
            HostLobbyStateText.Text = "  •  Handshaked & Connected";
            int pingToShow = _currentClientToHostPingMs >= 0 ? _currentClientToHostPingMs : (_steamService.LastClientRttPingMs >= 0 ? _steamService.LastClientRttPingMs : 1);
            HostLobbyPingText.Text = $"{pingToShow} ms";
            HostLobbySpeedText.Text = LobbyClientViewModel.FormatSpeed(_currentLobbyTransferSpeedBytesPerSec);
            LobbyStatusSummaryText.Text = $"Connected to Host {hostName} ({_lobbyClients.Count} Client in Lobby)";

            if (isClientInSteamMode && hostSteamId > 0)
            {
                if (_currentHostAvatarSteamId != hostSteamId)
                {
                    _currentHostAvatarSteamId = hostSteamId;
                    _ = LoadAndAssignAvatarAsync(hostSteamId, bmp =>
                    {
                        HostLobbyAvatarImage.Source = bmp;
                        HostLobbyAvatarFallback.Visibility = bmp != null ? Visibility.Collapsed : Visibility.Visible;
                    });
                }
            }
            else
            {
                _currentHostAvatarSteamId = 0;
                HostLobbyAvatarImage.Source = null;
                HostLobbyAvatarFallback.Visibility = Visibility.Visible;
            }
            return;
        }

        // Case 2: Local machine is Host -> do NOT show Ping & Speed of itself on the Host card!
        HostLobbyStatsPanel.Visibility = Visibility.Collapsed;
        foreach (var clientVm in _lobbyClients)
        {
            clientVm.IsSelf = false;
        }
        ulong localSteamId = (isSteamMode && _steamService.IsSteamInitialized) ? _steamService.LocalSteamId : 0;
        string localName = (isSteamMode && _steamService.IsSteamInitialized && !string.IsNullOrWhiteSpace(_steamService.LocalPersonaName))
            ? _steamService.LocalPersonaName
            : Environment.MachineName;

        HostLobbyNameText.Text = localName;
        HostLobbyIdText.Text = (isSteamMode && localSteamId > 0)
            ? $"ID: {localSteamId}  ({_steamService.LocalRoomCode})"
            : "";

        int port = _nginxService.IsRunning ? _nginxService.ActivePort : _settings.NginxPort;
        string ipStr = selectedEp?.IpAddress ?? _allDiscoveredEndpoints.FirstOrDefault()?.IpAddress ?? "127.0.0.1";
        if (_isClientConnectedToRemoteHost && !string.IsNullOrWhiteSpace(_connectedHostIpPort))
        {
            HostLobbyIpPortText.Text = _connectedHostIpPort;
        }
        else
        {
            HostLobbyIpPortText.Text = $"{ipStr}:{port}";
        }

        if (!string.IsNullOrWhiteSpace(_hostCustomStatusText))
        {
            HostLobbyStateText.Text = $"  •  {_hostCustomStatusText}";
            LobbyStatusSummaryText.Text = $"Host Preparing ({_hostCustomStatusText})  •  {_lobbyClients.Count} Client(s) Connected";
        }
        else if (_nginxService.IsRunning)
        {
            HostLobbyStateText.Text = $"  •  Hosting {_lastHostedEntries.Count} files";
            LobbyStatusSummaryText.Text = $"Hosting Active  •  {_lobbyClients.Count} Client(s) Connected";
        }
        else
        {
            HostLobbyStateText.Text = "  •  Lobby Active";
            LobbyStatusSummaryText.Text = $"Lobby Active  •  {_lobbyClients.Count} Client(s) Connected";
        }

        int hostPing = _currentHostLivePingMs >= 0 ? _currentHostLivePingMs : 1;
        if (_lobbyClients.Count > 0)
        {
            string firstClientPing = _lobbyClients[0].PingDisplay;
            HostLobbyPingText.Text = !string.IsNullOrWhiteSpace(firstClientPing) && firstClientPing != "—"
                ? firstClientPing
                : $"{hostPing} ms";
        }
        else
        {
            HostLobbyPingText.Text = $"{hostPing} ms";
        }

        HostLobbySpeedText.Text = LobbyClientViewModel.FormatSpeed(_currentLobbyTransferSpeedBytesPerSec);

        if (isSteamMode && localSteamId > 0)
        {
            if (_currentHostAvatarSteamId != localSteamId)
            {
                _currentHostAvatarSteamId = localSteamId;
                _ = LoadAndAssignAvatarAsync(localSteamId, bmp =>
                {
                    HostLobbyAvatarImage.Source = bmp;
                    HostLobbyAvatarFallback.Visibility = bmp != null ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        }
        else
        {
            _currentHostAvatarSteamId = 0;
            HostLobbyAvatarImage.Source = null;
            HostLobbyAvatarFallback.Visibility = Visibility.Visible;
        }
    }

    private async Task SendClientTelemetryOverHttpAsync(
        string baseHttpUrl,
        ClientLobbyTelemetry telemetry,
        System.Net.Http.HttpClient? sharedHttp = null,
        CancellationToken cancellationToken = default,
        bool forceImmediate = false)
    {
        if (!forceImmediate && Interlocked.CompareExchange(ref _telemetryHttpInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(telemetry);
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            string url = $"{baseHttpUrl.TrimEnd('/')}/telemetry?data={Uri.EscapeDataString(b64)}";
            var client = sharedHttp ?? _sharedTelemetryHttpClient;
            using var resp = await client.GetAsync(url, cancellationToken);
            if (resp.Headers.TryGetValues("X-Client-Endpoint", out var epVals))
            {
                string observed = epVals.FirstOrDefault()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(observed) && !observed.StartsWith("127.0.0.1", StringComparison.Ordinal))
                {
                    bool changed = !string.Equals(_observedClientSelfIpPort, observed, StringComparison.Ordinal);
                    _observedClientSelfIpPort = observed;
                    if (changed && _latestLocalClientTelemetry != null)
                    {
                        _latestLocalClientTelemetry.IpAndPort = observed;
                        UpsertClientInLobby(_latestLocalClientTelemetry);
                    }
                }
            }
        }
        catch
        {
            // Ignore transient telemetry HTTP errors
        }
        finally
        {
            if (!forceImmediate)
            {
                Interlocked.Exchange(ref _telemetryHttpInFlight, 0);
            }
        }
    }

    private string ResolveClientOwnDisplayEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(_observedClientSelfIpPort))
        {
            return _observedClientSelfIpPort;
        }

        // Fall back to Client's own IP matching the connection scope (NEVER Host's _connectedHostIpPort!)
        string hostIpOnly = ExtractIpOnly(_connectedHostIpPort);
        if (System.Net.IPAddress.TryParse(hostIpOnly, out var hostIp))
        {
            if (NetworkDiscoveryService.IsVpnIpAddress(hostIp))
            {
                var vpn = _allDiscoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.VPN);
                if (vpn != null) return vpn.IpAddress;
            }
            else if (NetworkDiscoveryService.IsPrivateRfc1918Ip(hostIp))
            {
                var lan = _allDiscoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.LAN);
                if (lan != null) return lan.IpAddress;
            }
            else if (!System.Net.IPAddress.IsLoopback(hostIp))
            {
                var wan = _allDiscoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.WAN);
                if (wan != null) return wan.IpAddress;
            }
        }

        return _allDiscoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.WAN)?.IpAddress
            ?? _allDiscoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.LAN)?.IpAddress
            ?? "127.0.0.1";
    }

    private void DispatchLiveClientTelemetry(
        string baseHttpUrl,
        string stageText,
        double progressPercent,
        double speedBytesPerSec,
        bool useSteamMode,
        bool forceImmediate = false)
    {
        long nowTicks = Environment.TickCount64;
        if (!forceImmediate && (nowTicks - Interlocked.Read(ref _lastTelemetrySentTick)) < 180)
        {
            return;
        }
        Interlocked.Exchange(ref _lastTelemetrySentTick, nowTicks);

        ulong localSteamId = (useSteamMode && _steamService.IsSteamInitialized) ? _steamService.LocalSteamId : 0;
        string localName = (useSteamMode && _steamService.IsSteamInitialized && !string.IsNullOrWhiteSpace(_steamService.LocalPersonaName))
            ? _steamService.LocalPersonaName
            : Environment.MachineName;

        string clientEndpoint = ResolveClientOwnDisplayEndpoint();

        int pingMs = _currentClientToHostPingMs >= 0
            ? _currentClientToHostPingMs
            : (_steamService.LastClientRttPingMs >= 0 ? _steamService.LastClientRttPingMs : 1);

        var telemetry = new ClientLobbyTelemetry
        {
            ClientKey = (useSteamMode && localSteamId > 0) ? $"steam_{localSteamId}" : $"ip_{Environment.MachineName}",
            SteamId = localSteamId,
            Name = localName,
            IpAndPort = clientEndpoint,
            PingMs = pingMs,
            SpeedBytesPerSec = speedBytesPerSec,
            ProgressPercent = Math.Clamp(progressPercent, 0, 100),
            StageText = stageText,
            IsSteamMode = useSteamMode,
            IsCgnat = _localIsCgnat || _steamService.LastConnectionObservedRelay
        };

        _latestLocalClientTelemetry = telemetry;
        _currentLobbyTransferSpeedBytesPerSec = speedBytesPerSec;

        // Update local Lobby UI
        UpsertClientInLobby(telemetry);

        // Stream to Host over active Stage 2 P2P data socket, OR over Direct HTTP (/telemetry) when using Stage 1 Direct HTTP Handoff / Direct IP / VPN
        if (_steamService.HasActiveP2PDataConnection)
        {
            _steamService.SendClientTelemetry(telemetry);
        }
        else if (!string.IsNullOrWhiteSpace(baseHttpUrl))
        {
            _ = SendClientTelemetryOverHttpAsync(baseHttpUrl, telemetry, forceImmediate: forceImmediate);
        }
    }

    private void OnSteamLobbyHostJoinRequested(ulong lobbyId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ModeSteamP2PRadio.IsChecked = true;
            string roomCode = SteamP2PRelayService.EncodeRoomCode(lobbyId);
            SetTargetBoxProgrammatically(roomCode);
            _settings.LastSteamTarget = roomCode;
            _settings.Save();
            UpdateTopRightStatusBadges($"Steam Lobby Invite ({roomCode})");
            AppendLog($"[STEAM] Auto-filled Host Room Code from Steam Lobby: {roomCode} (LobbyID: {lobbyId})");
        });
    }

    public void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedLine = $"[{timestamp}] {message}{Environment.NewLine}";
        _logBuffer.Append(formattedLine);
        _logWindow?.AppendLiveLine(formattedLine);
    }

    private void UpdateProgressUI(string statusText, double percentage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProgressUI(statusText, percentage));
            return;
        }

        MainProgressBar.Value = Math.Clamp(percentage, 0, 100);
        ProgressStatusText.Text = statusText;
    }

    private void SaveCurrentTargetSettings()
    {
        _portDebounceCts?.Cancel();
        string val = HostTargetTextBox.Text.Trim();
        _settings.LastHostTarget = val;
        if (ModeSteamP2PRadio.IsChecked == true)
            _settings.LastSteamTarget = val;
        else if (ModeVpnIpRadio.IsChecked == true)
            _settings.LastVpnIp = val;
        else
            _settings.LastWanLanIp = val;

        if (int.TryParse(NginxPortBox.Text.Trim(), out int port) && port > 0 && port <= 65535)
        {
            _settings.NginxPort = port;
            foreach (var ep in _allDiscoveredEndpoints)
            {
                ep.Port = port;
            }
        }
        _settings.Save();
    }

    private bool _isRebindingModePort;

    private async void ConnModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing) return;
        UpdateModeUI();

        // If Host Portable Nginx is already running, keep Direct IP / VPN on the configured static port and Steam P2P on auto-port
        if (_nginxService.IsRunning && _isHostSessionActive && !_isBusy && !_isRebindingModePort)
        {
            bool isSteamMode = ModeSteamP2PRadio.IsChecked == true;
            int staticPort = int.TryParse(NginxPortBox.Text.Trim(), out int p) && p > 0 && p <= 65535
                ? p
                : (_settings.NginxPort > 0 ? _settings.NginxPort : 27000);
            _settings.NginxPort = staticPort;

            bool needsRebind = isSteamMode
                ? (_nginxService.ActivePort == staticPort)
                : (_nginxService.ActivePort != staticPort);

            if (needsRebind)
            {
                _isRebindingModePort = true;
                try
                {
                    int prevPort = _nginxService.ActivePort;
                    await _upnpService.RemovePortForwardAsync(AppendLog);

                    bool restarted = await _nginxService.StartServerAsync(
                        _settings,
                        _lastModHashFilePath,
                        _lastHostedEntries,
                        AppendLog,
                        _steamService.LocalHostEndpoints,
                        useAutoPort: isSteamMode);
                    if (restarted)
                    {
                        _steamService.UpdateHostTargetNginxPort(_nginxService.ActivePort, _allDiscoveredEndpoints);
                        if (!isSteamMode)
                        {
                            if (_steamService.IsSteamInitialized)
                            {
                                _steamService.ShutdownCurrentSession(AppendLog);
                            }
                            _steamService.LocalHostEndpoints.HostName = Environment.MachineName;
                            _steamService.LocalHostEndpoints.HostSteamId = 0;
                            if (ModeWanLanIpRadio.IsChecked == true &&
                                InterfacesComboBox.SelectedItem is NetworkEndpointInfo selWan &&
                                selWan.Category == NetworkCategory.WAN)
                            {
                                _ = _upnpService.TryForwardPortAsync(_nginxService.ActivePort, AppendLog);
                            }
                        }
                        else
                        {
                            if (!_steamService.IsSteamInitialized)
                            {
                                await PromptToLaunchSteamIfNotRunningAsync();
                            }
                            if (_steamService.IsSteamInitialized && !_steamService.ActiveLobby.HasValue)
                            {
                                await _steamService.StartHostRelayAsync(_nginxService.ActivePort, _allDiscoveredEndpoints, AppendLog);
                                SetTargetBoxProgrammatically(_steamService.LocalRoomCode);
                            }
                            _ = _upnpService.TryForwardPortAsync(_nginxService.ActivePort, AppendLog);
                        }
                        _vpnDiscoveryService.UpdateHostBeaconMetadata(
                            _nginxService.ActivePort,
                            _allDiscoveredEndpoints,
                            _lastHostedEntries.Count,
                            _lastHostedEntries.Sum(entry => entry.FileSize),
                            string.IsNullOrWhiteSpace(_hostCustomStatusText) ? "Hosting" : _hostCustomStatusText,
                            ModeVpnIpRadio.IsChecked == true,
                            _steamService.LocalHostEndpoints.HostName);
                        AppendLog(isSteamMode
                            ? $"[NGINX] Switched active Host listener from static port {prevPort} -> Steam P2P Auto-Port {_nginxService.ActivePort}."
                            : $"[NGINX] Switched active Host listener from port {prevPort} -> configured static port {_nginxService.ActivePort}.");
                        ServerStatusTitle.Text = $"Server: Hosting (Port {_nginxService.ActivePort})";
                        HostBtnSubtitle.Text = $"Serving {_lastHostedEntries.Count} files on Port {_nginxService.ActivePort}";
                        UpdateTopRightStatusBadges();
                        UpdateHostLobbyCardUI();
                    }
                }
                catch { }
                finally
                {
                    _isRebindingModePort = false;
                }
            }
            else if (!isSteamMode && _steamService.IsSteamInitialized)
            {
                _steamService.ShutdownCurrentSession(AppendLog);
                _vpnDiscoveryService.SetPeriodicBroadcastEnabled(ModeVpnIpRadio.IsChecked == true, _allDiscoveredEndpoints);
                UpdateTopRightStatusBadges();
                UpdateHostLobbyCardUI();
            }
            else
            {
                _vpnDiscoveryService.SetPeriodicBroadcastEnabled(ModeVpnIpRadio.IsChecked == true, _allDiscoveredEndpoints);
            }
        }
        else if (_isHostSessionActive)
        {
            _vpnDiscoveryService.SetPeriodicBroadcastEnabled(ModeVpnIpRadio.IsChecked == true, _allDiscoveredEndpoints);
        }
    }

    private async void InterfacesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitializing || _isPopulatingCombo) return;

        if (InterfacesComboBox.SelectedItem is NetworkEndpointInfo selected)
        {
            SetTargetBoxProgrammatically(selected.IpAddress);
            if (ModeVpnIpRadio.IsChecked == true)
                _settings.LastVpnIp = selected.IpAddress;
            else if (ModeWanLanIpRadio.IsChecked == true)
                _settings.LastWanLanIp = selected.IpAddress;
            _settings.Save();
        }

        UpdateUpnpButtonVisibility();
        UpdateTopRightStatusBadges();

        if (ModeWanLanIpRadio.IsChecked == true &&
            InterfacesComboBox.SelectedItem is NetworkEndpointInfo selWan &&
            selWan.Category == NetworkCategory.WAN)
        {
            SaveCurrentTargetSettings();
            await TriggerUpnpPortForwardAsync();
        }
    }

    private async void UpnpForwardBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentTargetSettings();
        await TriggerUpnpPortForwardAsync();
    }

    private async Task TriggerUpnpPortForwardAsync()
    {
        UpnpForwardBtn.IsEnabled = false;
        try
        {
            bool ok = await _upnpService.TryForwardPortAsync(_settings.NginxPort, AppendLog);
            UpdateTopRightStatusBadges();
            if (ok)
            {
                UpnpForwardBtn.Content = $"✓ UPnP ({_settings.NginxPort})";
            }
            else
            {
                UpnpForwardBtn.Content = "📡 UPnP";
            }
        }
        finally
        {
            UpnpForwardBtn.IsEnabled = true;
        }
    }

    private async void RefreshInterfacesBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshInterfacesBtn.IsEnabled = false;
        try
        {
            await RefreshNetworkInterfacesAsync(logToConsole: true);
        }
        finally
        {
            RefreshInterfacesBtn.IsEnabled = true;
        }
    }

    // =========================================================================
    // BIG BUTTON 1: HOST MOD SERVER
    // =========================================================================
    private async void HostBigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_nginxService.IsRunning || _isHostSessionActive)
        {
            await StopHostingAndOperationsAsync();
            return;
        }

        if (_isBusy) return;
        SaveCurrentTargetSettings();
        _steamService.SetConfiguredAppId(_settings.SteamAppId);

        if (ModeSteamP2PRadio.IsChecked == true && !_steamService.IsSteamInitialized)
        {
            await PromptToLaunchSteamIfNotRunningAsync();
        }

        _isBusy = true;
        _isHostSessionActive = true;
        _activeOperationCts = new CancellationTokenSource();
        ClientBigButton.IsEnabled = false;
        StopServerFeatureBtn.IsEnabled = true;

        HostBtnTitle.Text = "STOP HOST";
        HostBtnTitle.Foreground = new SolidColorBrush(Color.FromRgb(255, 122, 122));
        HostBtnSubtitle.Text = "Creating Lobby & Scanning Files...";
        ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(96, 205, 255));
        ServerStatusTitle.Text = "Server: Creating Lobby...";

        try
        {
            bool isSteamMode = ModeSteamP2PRadio.IsChecked == true;
            AppendLog("--------------------------------------------------------------------------------");
            AppendLog(isSteamMode
                ? "--> STARTING HOST MODE (Steam P2P Auto-Port — Zero-Copy Portable Nginx HTTP + Steam P2P)"
                : $"--> STARTING HOST MODE (Port {_settings.NginxPort} — Zero-Copy Portable Nginx HTTP + Steam P2P)");

            // =================================================================
            // STEP 1: START NGINX (AUTO-PORT IN STEAM MODE), LAUNCH UPNP IN PARALLEL & CREATE LOBBY
            // =================================================================
            _hostCustomStatusText = "Creating Lobby & Opening Listener...";
            UpdateProgressUI("Step 1/3: Starting listener, UPnP & creating Session Lobby...", 2);
            UpdateHostLobbyCardUI();

            // Refresh active WAN/LAN/VPN interfaces right before publishing Lobby metadata
            // so stale/disconnected VPN (Tailscale/ZeroTier) IPs are not advertised to Clients.
            try
            {
                string? prevSelectedIp = (InterfacesComboBox.SelectedItem as NetworkEndpointInfo)?.IpAddress;
                var refreshedEndpoints = await NetworkDiscoveryService.DiscoverAllEndpointsAsync(_settings.NginxPort, _activeOperationCts.Token);
                if (refreshedEndpoints.Count > 0)
                {
                    _allDiscoveredEndpoints = refreshedEndpoints;
                    PopulateInterfacesComboForCurrentMode();
                    if (!string.IsNullOrWhiteSpace(prevSelectedIp) &&
                        InterfacesComboBox.ItemsSource is IEnumerable<NetworkEndpointInfo> comboItems)
                    {
                        var matching = comboItems.FirstOrDefault(ep => string.Equals(ep.IpAddress, prevSelectedIp, StringComparison.OrdinalIgnoreCase));
                        if (matching != null)
                        {
                            _isPopulatingCombo = true;
                            try { InterfacesComboBox.SelectedItem = matching; }
                            finally { _isPopulatingCombo = false; }
                        }
                    }
                    UpdateTopRightStatusBadges();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            var initialMeta = new HostDirectEndpointsMetadata
            {
                HostSteamId = (isSteamMode && _steamService.IsSteamInitialized) ? _steamService.LocalSteamId : 0,
                HostName = (isSteamMode && _steamService.IsSteamInitialized && !string.IsNullOrWhiteSpace(_steamService.LocalPersonaName))
                    ? _steamService.LocalPersonaName
                    : Environment.MachineName,
                NginxPort = _settings.NginxPort,
                WanIp = _allDiscoveredEndpoints.FirstOrDefault(ep => ep.Category == NetworkCategory.WAN)?.IpAddress ?? "",
                LanIps = _allDiscoveredEndpoints.Where(ep => ep.Category == NetworkCategory.LAN).Select(ep => ep.IpAddress).Distinct().ToList(),
                VpnIps = _allDiscoveredEndpoints.Where(ep => ep.Category == NetworkCategory.VPN).Select(ep => ep.IpAddress).Distinct().ToList(),
                HostingState = "Scanning local files...",
                VpnProviders = _allDiscoveredEndpoints
                    .Where(ep => ep.Category == NetworkCategory.VPN)
                    .Select(ep => string.IsNullOrWhiteSpace(ep.VpnProvider) ? "VPN" : ep.VpnProvider)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            // Start Portable Nginx FIRST so Steam P2P Auto-Port assigns a real free OS ephemeral port before Lobby creation
            bool earlyNginxOk = await _nginxService.StartServerAsync(
                _settings,
                _lastModHashFilePath,
                _lastHostedEntries,
                AppendLog,
                initialMeta,
                useAutoPort: isSteamMode);

            int boundPort = earlyNginxOk ? _nginxService.ActivePort : _settings.NginxPort;

            _vpnDiscoveryService.StartHostBeacon(
                initialMeta.HostName,
                boundPort,
                _allDiscoveredEndpoints,
                0,
                0,
                "Scanning local files...",
                ModeVpnIpRadio.IsChecked == true,
                AppendLog);

            // Launch UPnP port forwarding immediately in parallel while files scan, so WAN direct handoff is ready instantly!
            Task<bool>? upnpTask = null;
            if (isSteamMode ||
                (ModeWanLanIpRadio.IsChecked == true &&
                 InterfacesComboBox.SelectedItem is NetworkEndpointInfo selEp &&
                 selEp.Category == NetworkCategory.WAN))
            {
                upnpTask = _upnpService.TryForwardPortAsync(boundPort, AppendLog, _activeOperationCts.Token);
            }

            if (isSteamMode)
            {
                await _steamService.StartHostRelayAsync(boundPort, _allDiscoveredEndpoints, AppendLog);
            }

            if (isSteamMode && _steamService.IsSteamInitialized)
            {
                SetTargetBoxProgrammatically(_steamService.LocalRoomCode);
                _settings.LastSteamTarget = _steamService.LocalRoomCode;
                _settings.Save();
            }
            else
            {
                _steamService.LocalHostEndpoints.HostName = Environment.MachineName;
                _steamService.LocalHostEndpoints.HostSteamId = 0;
                _steamService.LocalHostEndpoints.NginxPort = boundPort;
            }

            UpdateTopRightStatusBadges();
            _hostCustomStatusText = "Scanning local files...";
            UpdateHostLobbyCardUI();

            // =================================================================
            // STEP 2: SCAN & HASH LOCAL BG3 MOD FILES
            // =================================================================
            UpdateProgressUI("Step 2/3: Scanning and hashing local BG3 files...", 5);
            var (modHashPath, entries) = await _hashService.GenerateModHashAsync(
                _settings,
                AppendLog,
                (scanStatus, scanPct) =>
                {
                    _hostCustomStatusText = $"Scanning files ({scanPct:0}%)...";
                    UpdateProgressUI(scanStatus, scanPct * 0.85);
                    UpdateHostLobbyCardUI();
                },
                _activeOperationCts.Token);

            _lastModHashFilePath = modHashPath;
            _lastHostedEntries = entries;

            // =================================================================
            // STEP 3: UPDATE PORTABLE NGINX MANIFEST & AWAIT PARALLEL UPNP
            // =================================================================
            // Re-evaluate connection mode in case the user switched between Steam P2P and Direct IP / VPN while files were scanning!
            SaveCurrentTargetSettings();
            bool wasSteamMode = isSteamMode;
            isSteamMode = ModeSteamP2PRadio.IsChecked == true;
            _hostCustomStatusText = "Updating Nginx Manifest...";
            UpdateHostLobbyCardUI();
            int effectivePort = !isSteamMode
                ? (_settings.NginxPort > 0 ? _settings.NginxPort : 27000)
                : (_nginxService.IsRunning ? _nginxService.ActivePort : boundPort);

            if (!wasSteamMode && isSteamMode)
            {
                if (!_steamService.IsSteamInitialized)
                {
                    await PromptToLaunchSteamIfNotRunningAsync();
                }
                if (_steamService.IsSteamInitialized)
                {
                    await _steamService.StartHostRelayAsync(effectivePort, _allDiscoveredEndpoints, AppendLog);
                    SetTargetBoxProgrammatically(_steamService.LocalRoomCode);
                    _settings.LastSteamTarget = _steamService.LocalRoomCode;
                    _settings.Save();
                }
            }
            else if (wasSteamMode && !isSteamMode && _steamService.IsSteamInitialized)
            {
                _steamService.ShutdownCurrentSession(AppendLog);
            }

            _steamService.UpdateHostTargetNginxPort(effectivePort, _allDiscoveredEndpoints);
            if (!isSteamMode)
            {
                _steamService.LocalHostEndpoints.HostName = Environment.MachineName;
                _steamService.LocalHostEndpoints.HostSteamId = 0;
            }

            long totalModBytes = entries.Sum(entry => entry.FileSize);
            _steamService.LocalHostEndpoints.FileCount = entries.Count;
            _steamService.LocalHostEndpoints.TotalModBytes = totalModBytes;
            _steamService.LocalHostEndpoints.HostingState = "Hosting";
            _steamService.LocalHostEndpoints.VpnProviders = _allDiscoveredEndpoints
                .Where(ep => ep.Category == NetworkCategory.VPN)
                .Select(ep => string.IsNullOrWhiteSpace(ep.VpnProvider) ? "VPN" : ep.VpnProvider)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            UpdateProgressUI($"Step 3/3: Publishing {entries.Count} files on Portable Nginx HTTP Server (Port {effectivePort})...", 88);
            bool started = await _nginxService.UpdateHostManifestAndReloadAsync(
                _settings,
                modHashPath,
                entries,
                AppendLog,
                _steamService.LocalHostEndpoints,
                useAutoPort: isSteamMode);
            if (!started)
            {
                _isHostSessionActive = false;
                _hostCustomStatusText = "";
                _vpnDiscoveryService.StopHostBeacon();
                _steamService.StopHostRelay();
                if (!_isClientConnectedToRemoteHost && _steamService.IsSteamInitialized)
                {
                    _steamService.ShutdownCurrentSession(AppendLog);
                }
                HostBtnTitle.Text = "HOST";
                HostBtnTitle.Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255));
                HostBtnSubtitle.Text = "Start Mod Server";
                UpdateTopRightStatusBadges();
                UpdateHostLobbyCardUI();
                UpdateProgressUI("Nginx failed to start", 0);
                return;
            }

            if (_nginxService.ActivePort != effectivePort)
            {
                _steamService.UpdateHostTargetNginxPort(_nginxService.ActivePort, _allDiscoveredEndpoints);
            }

            _vpnDiscoveryService.UpdateHostBeaconMetadata(
                _nginxService.ActivePort,
                _allDiscoveredEndpoints,
                entries.Count,
                totalModBytes,
                "Hosting",
                ModeVpnIpRadio.IsChecked == true,
                _steamService.LocalHostEndpoints.HostName);

            if (upnpTask != null)
            {
                if (!upnpTask.IsCompleted)
                {
                    UpdateProgressUI($"Finalizing UPnP Port Forward for TCP {_nginxService.ActivePort}...", 95);
                }
                try { await upnpTask; } catch { }
                // If user switched to VPN IP (or non-WAN Direct IP) during file scan, clean up the early Steam-mode UPnP forward!
                if (wasSteamMode && !isSteamMode)
                {
                    await _upnpService.RemovePortForwardAsync(AppendLog);
                    if (ModeWanLanIpRadio.IsChecked == true &&
                        InterfacesComboBox.SelectedItem is NetworkEndpointInfo selWanAfter &&
                        selWanAfter.Category == NetworkCategory.WAN)
                    {
                        await _upnpService.TryForwardPortAsync(_nginxService.ActivePort, AppendLog, _activeOperationCts.Token);
                    }
                }
            }

            _hostCustomStatusText = "";
            ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(108, 203, 95));
            ServerStatusTitle.Text = $"Server: Hosting (Port {_nginxService.ActivePort})";
            HostBtnTitle.Text = "STOP HOST";
            HostBtnTitle.Foreground = new SolidColorBrush(Color.FromRgb(255, 122, 122));
            HostBtnSubtitle.Text = $"Serving {entries.Count} files on Port {_nginxService.ActivePort}";
            UpdateTopRightStatusBadges();
            UpdateHostLobbyCardUI();

            AppendLog("================================================================================");
            AppendLog("  HOST SERVER IS ONLINE! (0 files copied — serving directly from ModHash.txt)");
            foreach (var ep in _allDiscoveredEndpoints)
            {
                AppendLog($"  • {ep.CategoryBadge,-6} {ep.AdapterName,-26} : http://{ep.IpAddress}:{_nginxService.ActivePort}");
            }
            if (_steamService.IsSteamInitialized)
            {
                AppendLog($"  • [STEAM] Session Room Code            : {_steamService.LocalRoomCode}  (Unique Steam Lobby ID)");
                AppendLog($"  • [STEAM] Permanent Host SteamID       : {_steamService.LocalSteamId}");
                if (!string.IsNullOrEmpty(_steamService.NearestRelaySummary))
                {
                    AppendLog($"  • [STEAM] Nearest Valve Relay Server   : {_steamService.NearestRelaySummary}");
                }
            }
            AppendLog("================================================================================");
            UpdateProgressUI($"Hosting {entries.Count} files on Port {_nginxService.ActivePort}", 100);
        }
        catch (OperationCanceledException)
        {
            _isHostSessionActive = false;
            _hostCustomStatusText = "";
            _vpnDiscoveryService.StopHostBeacon();
            _steamService.StopHostRelay();
            if (!_nginxService.IsRunning && !_isClientConnectedToRemoteHost && _steamService.IsSteamInitialized)
            {
                _steamService.ShutdownCurrentSession(AppendLog);
            }
            HostBtnTitle.Text = "HOST";
            HostBtnTitle.Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255));
            HostBtnSubtitle.Text = "Start Mod Server";
            UpdateTopRightStatusBadges();
            UpdateHostLobbyCardUI();
            AppendLog("[HOST] Operation cancelled by user.");
            UpdateProgressUI("Cancelled", 0);
        }
        catch (Exception ex)
        {
            _isHostSessionActive = false;
            _hostCustomStatusText = "";
            _vpnDiscoveryService.StopHostBeacon();
            _steamService.StopHostRelay();
            if (!_nginxService.IsRunning && !_isClientConnectedToRemoteHost && _steamService.IsSteamInitialized)
            {
                _steamService.ShutdownCurrentSession(AppendLog);
            }
            HostBtnTitle.Text = "HOST";
            HostBtnTitle.Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255));
            HostBtnSubtitle.Text = "Start Mod Server";
            UpdateTopRightStatusBadges();
            UpdateHostLobbyCardUI();
            AppendLog($"[ERROR] Host startup failed: {ex.Message}");
            UpdateProgressUI("Error", 0);
        }
        finally
        {
            _isBusy = false;
            HostBigButton.IsEnabled = true;
            // Keep ClientBigButton enabled even while Hosting so same-PC local testing works!
            ClientBigButton.IsEnabled = true;
            StopServerFeatureBtn.IsEnabled = _nginxService.IsRunning || _isHostSessionActive;
        }
    }

    // =========================================================================
    // BIG BUTTON 2: CLIENT SYNC MODS
    // =========================================================================
    private async void ClientBigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SaveCurrentTargetSettings();

        string targetInput = HostTargetTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetInput))
        {
            AppendLog("[WARN] Please enter the Host target in the box above (Room Code / SteamID, WAN/LAN IP, or VPN IP).");
            HostTargetTextBox.Focus();
            MessageBox.Show(
                this,
                "Please enter the Host target first:\n• Steam Hole-Punch Mode: Room Code (BG3-...) or Host SteamID64\n• Direct IP Mode: Host WAN or LAN IP\n• VPN Mode: Host VPN IP",
                "Host Target Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _isBusy = true;
        _activeOperationCts = new CancellationTokenSource();
        _observedClientSelfIpPort = "";
        _connectedClientBaseHttpUrl = "";
        StopClientDirectHttpPingLoop();
        HostBigButton.IsEnabled = false;
        ClientBigButton.IsEnabled = false;
        StopServerFeatureBtn.IsEnabled = true;

        bool keepClientConnectionAlive = false;
        try
        {
            AppendLog("--------------------------------------------------------------------------------");
            bool isSteamTarget = SteamP2PRelayService.TryParseSteamTarget(targetInput, out ulong hostSteamId);
            bool useSteamMode = ModeSteamP2PRadio.IsChecked == true || isSteamTarget;

            // =====================================================================
            // STEP 1: ESTABLISH CONNECTION FIRST, HANDSHAKE & START 1s PING KEEP-ALIVE
            // =====================================================================
            string baseHttpUrl;
            if (useSteamMode && isSteamTarget)
            {
                _steamService.SetConfiguredAppId(_settings.SteamAppId);
                if (!_steamService.IsSteamInitialized)
                {
                    bool startedSteam = await PromptToLaunchSteamIfNotRunningAsync();
                    if (!startedSteam)
                    {
                        AppendLog("[WARN] Steam is not running. Start Steam manually or switch to Direct IP / VPN mode.");
                        UpdateProgressUI("Waiting for Steam Client", 0);
                        return;
                    }
                }

                string relayInfo = !string.IsNullOrEmpty(_steamService.NearestRelaySummary)
                    ? $" [{_steamService.NearestRelaySummary}]"
                    : "";
                ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(96, 205, 255));
                ServerStatusTitle.Text = "Client: Handshaking (Steam P2P)...";
                UpdateTopRightStatusBadges($"Negotiating Steam P2P{relayInfo}...");
                UpdateProgressUI("Step 1/3: Establishing connection & P2P handshake with Host...", 3);

                string? tunnelUrl = await _steamService.StartClientRelayTunnelAsync(
                    hostSteamId,
                    AppendLog,
                    _activeOperationCts.Token,
                    _allDiscoveredEndpoints,
                    _settings.NginxPort);
                if (string.IsNullOrEmpty(tunnelUrl))
                {
                    ServerStatusTitle.Text = _nginxService.IsRunning ? $"Server: Hosting (Port {_nginxService.ActivePort})" : "Server: Idle";
                    UpdateTopRightStatusBadges("Steam P2P Failed");
                    UpdateProgressUI("Steam P2P Connection Failed", 0);
                    return;
                }

                baseHttpUrl = tunnelUrl;
                _connectedClientBaseHttpUrl = baseHttpUrl;
                _isClientConnectedToRemoteHost = true;
                _connectedHostIpPort = !string.IsNullOrWhiteSpace(_steamService.PunchedHostEndpoint)
                    ? _steamService.PunchedHostEndpoint
                    : $"{targetInput}:{_steamService.LastHandshakedHostEndpoints?.NginxPort ?? _settings.NginxPort}";
                _connectedHostMeta = _steamService.LastHandshakedHostEndpoints;
                if (!string.IsNullOrWhiteSpace(_steamService.ObservedClientSelfIpPort))
                {
                    _observedClientSelfIpPort = _steamService.ObservedClientSelfIpPort;
                }
                _currentClientToHostPingMs = _steamService.LastClientRttPingMs >= 0 ? _steamService.LastClientRttPingMs : 1;

                ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(108, 203, 95));
                ServerStatusTitle.Text = "Client: Handshaked (Steam P2P)";
                keepClientConnectionAlive = true;
            }
            else
            {
                if (useSteamMode && !isSteamTarget)
                {
                    AppendLog($"[WARN] '{targetInput}' is not a valid Steam Room Code (BG3-...) or 17-digit SteamID64. Connecting via Direct IP...");
                }

                RelayWarningBanner.Visibility = Visibility.Collapsed;
                baseHttpUrl = HttpSyncClientService.NormalizeBaseHttpUrl(targetInput, _settings.NginxPort);
                _connectedClientBaseHttpUrl = baseHttpUrl;
                string routeDesc = NetworkDiscoveryService.ClassifyRemoteOrTargetIp(targetInput, _allDiscoveredEndpoints);

                UpdateProgressUI($"Step 1/3: Connecting & handshaking with Host at {baseHttpUrl}...", 3);

                string hostIpPort = baseHttpUrl.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                                               .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                                               .TrimEnd('/');

                using (var handshakeHttp = NetworkDiscoveryService.CreateOptimizedHttpClient(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3)))
                {
                    int initPing = await SteamP2PRelayService.MeasureEndpointPingMsAsync(hostIpPort, baseHttpUrl, handshakeHttp, _activeOperationCts.Token);
                    _currentClientToHostPingMs = initPing >= 0 ? initPing : 1;

                    try
                    {
                        using var infoResp = await handshakeHttp.GetAsync($"{baseHttpUrl.TrimEnd('/')}/host_info.json", _activeOperationCts.Token);
                        if (infoResp.Headers.TryGetValues("X-Client-Endpoint", out var epVals))
                        {
                            string obs = epVals.FirstOrDefault()?.Trim() ?? "";
                            if (!string.IsNullOrWhiteSpace(obs) && !obs.StartsWith("127.0.0.1", StringComparison.Ordinal))
                            {
                                _observedClientSelfIpPort = obs;
                            }
                        }
                        if (infoResp.IsSuccessStatusCode)
                        {
                            string hostInfoJson = await infoResp.Content.ReadAsStringAsync(_activeOperationCts.Token);
                            var parsedHostMeta = JsonSerializer.Deserialize<HostDirectEndpointsMetadata>(hostInfoJson);
                            if (parsedHostMeta != null)
                            {
                                _connectedHostMeta = parsedHostMeta;
                            }
                        }
                    }
                    catch
                    {
                        // Host info optional on older endpoints
                    }

                    if (string.IsNullOrWhiteSpace(_observedClientSelfIpPort))
                    {
                        try
                        {
                            using var healthResp = await handshakeHttp.GetAsync($"{baseHttpUrl.TrimEnd('/')}/health", _activeOperationCts.Token);
                            if (healthResp.Headers.TryGetValues("X-Client-Endpoint", out var epVals))
                            {
                                string obs = epVals.FirstOrDefault()?.Trim() ?? "";
                                if (!string.IsNullOrWhiteSpace(obs) && !obs.StartsWith("127.0.0.1", StringComparison.Ordinal))
                                {
                                    _observedClientSelfIpPort = obs;
                                }
                            }
                        }
                        catch { }
                    }
                }

                _isClientConnectedToRemoteHost = true;
                _connectedHostIpPort = hostIpPort;
                ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(108, 203, 95));
                ServerStatusTitle.Text = "Client: Handshaked (Direct IP)";
                StartClientDirectHttpPingLoop(baseHttpUrl, routeDesc);
                AppendLog($"[CLIENT] Connected & handshaked via {routeDesc} -> {baseHttpUrl} (Ping: {_currentClientToHostPingMs}ms)");
                keepClientConnectionAlive = true;
            }

            // Populate Lobby Host Card & Client Card immediately after handshake before scanning files!
            UpdateHostLobbyCardUI();
            DispatchLiveClientTelemetry(
                baseHttpUrl,
                "Handshaked — Preparing local file scan...",
                2,
                0,
                useSteamMode && isSteamTarget,
                forceImmediate: true);

            // =====================================================================
            // STEP 2: SCAN & HASH LOCAL FILES (WHILE STREAMING PROGRESS TO HOST)
            // =====================================================================
            UpdateProgressUI("Step 2/3: Scanning local BG3 files before transfer...", 5);
            var (_, localEntries) = await _hashService.GenerateModHashAsync(
                _settings,
                AppendLog,
                (scanStatus, scanPct) =>
                {
                    double overallScanPct = 2 + (scanPct * 0.23); // 2% -> 25% overall
                    UpdateProgressUI(scanStatus, overallScanPct);
                    DispatchLiveClientTelemetry(
                        baseHttpUrl,
                        $"Scanning Files ({scanPct:0}%): {scanStatus}",
                        overallScanPct,
                        0,
                        useSteamMode && isSteamTarget,
                        forceImmediate: scanPct >= 99.5);
                },
                _activeOperationCts.Token);

            // =====================================================================
            // STEP 3: COMPARE & TRANSFER FILES (WHILE STREAMING LIVE SPEED & PROGRESS)
            // =====================================================================
            UpdateProgressUI("Step 3/3: Comparing with Host ModHash.txt & transferring files...", 25);
            DispatchLiveClientTelemetry(
                baseHttpUrl,
                $"Scanned {localEntries.Count} local files — Comparing with Host...",
                25,
                0,
                useSteamMode && isSteamTarget,
                forceImmediate: true);

            bool syncOk = await _httpClientService.SyncFromHostNginxAsync(
                baseHttpUrl,
                _settings,
                localEntries,
                AppendLog,
                UpdateProgressUI,
                _activeOperationCts.Token,
                (syncStage, syncPct, speedBytesPerSec) =>
                {
                    DispatchLiveClientTelemetry(
                        baseHttpUrl,
                        syncStage,
                        syncPct,
                        speedBytesPerSec,
                        useSteamMode && isSteamTarget,
                        forceImmediate: syncPct >= 99.9 || speedBytesPerSec == 0);
                });

            DispatchLiveClientTelemetry(
                baseHttpUrl,
                syncOk ? "✓ Sync Complete — All Mods Up to Date (100%)" : "⚠ Sync Finished with Warnings",
                syncOk ? 100 : 0,
                0,
                useSteamMode && isSteamTarget,
                forceImmediate: true);

            ServerStatusTitle.Text = _nginxService.IsRunning
                ? $"Server: Hosting (Port {_nginxService.ActivePort})"
                : (syncOk ? "Client: Connected & Synced" : "Client: Sync Failed");
        }
        catch (OperationCanceledException)
        {
            keepClientConnectionAlive = false;
            AppendLog("[CLIENT] Client sync cancelled by user.");
            UpdateProgressUI("Cancelled", 0);
        }
        catch (Exception ex)
        {
            keepClientConnectionAlive = false;
            AppendLog($"[ERROR] Client sync failed: {ex.Message}");
            UpdateProgressUI("Sync Error", 0);
        }
        finally
        {
            if (!keepClientConnectionAlive)
            {
                StopClientDirectHttpPingLoop();
                _steamService.StopClientRelayTunnel();
                if (!_isHostSessionActive && !_nginxService.IsRunning && _steamService.IsSteamInitialized)
                {
                    _steamService.ShutdownCurrentSession(AppendLog);
                }
            }
            _isBusy = false;
            HostBigButton.IsEnabled = true;
            ClientBigButton.IsEnabled = true;
            StopServerFeatureBtn.IsEnabled = _nginxService.IsRunning || _steamService.IsClientTunnelActive || _clientDirectPingCts != null;
            UpdateTopRightStatusBadges();
            UpdateHostLobbyCardUI();
        }
    }

    private async Task StopHostingAndOperationsAsync()
    {
        try
        {
            _portDebounceCts?.Cancel();
            _activeOperationCts?.Cancel();
            StopClientDirectHttpPingLoop();
            _vpnDiscoveryService.StopHostBeacon();
            _nginxService.StopServer(AppendLog);
            _steamService.StopHostRelay();
            _steamService.StopClientRelayTunnel();
            if (_steamService.IsSteamInitialized)
            {
                _steamService.ShutdownCurrentSession(AppendLog);
            }
            await _upnpService.RemovePortForwardAsync(AppendLog);

            _activeLiveRouteOverride = null;
            _lastConnectedHttpClientIp = null;
            if (_isHostSessionActive && ModeSteamP2PRadio.IsChecked == true)
            {
                _settings.LastSteamTarget = "";
                SetTargetBoxProgrammatically("");
                _settings.Save();
            }
            _isHostSessionActive = false;
            _hostCustomStatusText = "";
            _isClientConnectedToRemoteHost = false;
            _connectedHostIpPort = "";
            _connectedClientBaseHttpUrl = "";
            _observedClientSelfIpPort = "";
            _connectedHostMeta = null;
            _latestLocalClientTelemetry = null;
            _currentLobbyTransferSpeedBytesPerSec = 0;
            _lastHttpTelemetryTicksByKey.Clear();
            _lobbyClients.Clear();
            RelayWarningBanner.Visibility = Visibility.Collapsed;
            ServerStatusDot.Fill = new SolidColorBrush(Color.FromRgb(138, 138, 138));
            ServerStatusTitle.Text = "Server: Idle";
            HostBtnTitle.Text = "HOST";
            HostBtnTitle.Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255));
            HostBtnSubtitle.Text = "Start Mod Server";
            ClientBigButton.IsEnabled = true;
            StopServerFeatureBtn.IsEnabled = false;
            UpdateTopRightStatusBadges("");
            UpdateHostLobbyCardUI();
            UpdateProgressUI("Stopped", 0);
            PathResolverService.CompactAndTrimWorkingSet();
        }
        catch (Exception ex)
        {
            AppendLog($"[WARN] Error stopping services: {ex.Message}");
        }
    }

    // =========================================================================
    // CONNECTION & LOG BAR ACTIONS
    // =========================================================================
    private void CopyHostInfoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ModeSteamP2PRadio.IsChecked == true)
        {
            if (_steamService.IsSteamInitialized)
            {
                string code = _steamService.LocalRoomCode;
                if (App.TrySetClipboardText(code))
                {
                    AppendLog($"[INFO] Copied Steam Room Code '{code}' to clipboard!");
                }
            }
            else
            {
                AppendLog("[WARN] Steam Client is not running.");
            }
            return;
        }

        if (InterfacesComboBox.SelectedItem is NetworkEndpointInfo selectedEp)
        {
            if (App.TrySetClipboardText(selectedEp.IpAddress))
            {
                AppendLog($"[INFO] Copied {selectedEp.CategoryBadge} IP '{selectedEp.IpAddress}' ({selectedEp.AdapterName}) to clipboard!");
            }
            return;
        }

        var fallback = _allDiscoveredEndpoints.FirstOrDefault();
        string text = fallback != null ? fallback.IpAddress : "127.0.0.1";
        if (App.TrySetClipboardText(text))
        {
            AppendLog($"[INFO] Copied IP '{text}' to clipboard.");
        }
    }

    private void CopySteamIdBtn_Click(object sender, RoutedEventArgs e)
    {
        ulong sid = _steamService.IsSteamInitialized
            ? _steamService.LocalSteamId
            : SteamP2PRelayService.TryGetActiveSteamUserIdFromRegistry();

        if (sid > 0)
        {
            string steamIdStr = sid.ToString();
            if (App.TrySetClipboardText(steamIdStr))
            {
                AppendLog($"[INFO] Copied permanent 17-digit SteamID64 '{steamIdStr}' to clipboard!");
            }
        }
        else
        {
            AppendLog("[WARN] Steam Client is not running; cannot retrieve SteamID64.");
        }
    }

    public static NetworkEndpointInfo? FindMatchingLocalVpnEndpoint(List<NetworkEndpointInfo> localVpns, VpnDiscoveredHostInfo host)
    {
        if (localVpns.Count == 0) return null;

        if (System.Net.IPAddress.TryParse(host.VpnIp, out var hostIp))
        {
            if (NetworkDiscoveryService.IsRfc6598CgnatIp(hostIp))
            {
                var rfcMatch = localVpns.FirstOrDefault(e =>
                    System.Net.IPAddress.TryParse(e.IpAddress, out var lip) &&
                    NetworkDiscoveryService.IsRfc6598CgnatIp(lip) &&
                    (string.IsNullOrWhiteSpace(host.VpnProvider) ||
                     string.Equals(e.VpnProvider, host.VpnProvider, StringComparison.OrdinalIgnoreCase)));
                if (rfcMatch != null) return rfcMatch;
            }

            if (host.VpnIp.StartsWith("26.", StringComparison.Ordinal))
            {
                var radmin = localVpns.FirstOrDefault(e => e.IpAddress.StartsWith("26.", StringComparison.Ordinal));
                if (radmin != null) return radmin;
            }

            if (host.VpnIp.StartsWith("25.", StringComparison.Ordinal))
            {
                var hamachi = localVpns.FirstOrDefault(e => e.IpAddress.StartsWith("25.", StringComparison.Ordinal));
                if (hamachi != null) return hamachi;
            }

            byte[] hb = hostIp.GetAddressBytes();
            var prefixMatch = localVpns.FirstOrDefault(e =>
                System.Net.IPAddress.TryParse(e.IpAddress, out var lip) &&
                lip.GetAddressBytes() is byte[] lb &&
                lb[0] == hb[0] && lb[1] == hb[1]);
            if (prefixMatch != null) return prefixMatch;
        }

        if (!string.IsNullOrWhiteSpace(host.VpnProvider))
        {
            var providerMatch = localVpns.FirstOrDefault(e =>
                string.Equals(e.VpnProvider, host.VpnProvider, StringComparison.OrdinalIgnoreCase));
            if (providerMatch != null) return providerMatch;
        }

        return localVpns.FirstOrDefault();
    }

    private async void SelectHostBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ModeVpnIpRadio.IsChecked == true)
        {
            SaveCurrentTargetSettings();
            int portToScan = int.TryParse(NginxPortBox.Text.Trim(), out int p) && p > 0 && p <= 65535
                ? p
                : (_settings.NginxPort > 0 ? _settings.NginxPort : 27000);

            var vpnWin = new VpnHostBrowserWindow(_vpnDiscoveryService, portToScan, AppendLog)
            {
                Owner = this
            };
            if (vpnWin.ShowDialog() == true && vpnWin.SelectedHost != null)
            {
                var selectedHost = vpnWin.SelectedHost;
                int resolvedPort = selectedHost.NginxPort > 0 ? selectedHost.NginxPort : portToScan;

                int ifacePort = (!_isHostSessionActive && !_nginxService.IsRunning && resolvedPort > 0)
                    ? resolvedPort
                    : _settings.NginxPort;
                var existingWan = _allDiscoveredEndpoints.Where(ep => ep.Category == NetworkCategory.WAN).ToList();
                var freshLocal = NetworkDiscoveryService.GetLocalAndVpnInterfaces(ifacePort);
                var updatedEndpoints = new List<NetworkEndpointInfo>(existingWan.Count + freshLocal.Count);
                updatedEndpoints.AddRange(existingWan);
                updatedEndpoints.AddRange(freshLocal);
                _allDiscoveredEndpoints = updatedEndpoints;
                PopulateInterfacesComboForCurrentMode();

                if (InterfacesComboBox.ItemsSource is IEnumerable<NetworkEndpointInfo> vpnItems)
                {
                    var matchEp = FindMatchingLocalVpnEndpoint(vpnItems.ToList(), selectedHost);
                    if (matchEp != null)
                    {
                        _isPopulatingCombo = true;
                        try { InterfacesComboBox.SelectedItem = matchEp; }
                        finally { _isPopulatingCombo = false; }
                    }
                }

                _settings.LastVpnIp = selectedHost.VpnIp;
                _settings.LastHostTarget = selectedHost.VpnIp;
                SetTargetBoxProgrammatically(selectedHost.VpnIp);

                if (resolvedPort > 0)
                {
                    _portDebounceCts?.Cancel();
                    _isUiInitializing = true;
                    try { NginxPortBox.Text = resolvedPort.ToString(); }
                    finally { _isUiInitializing = false; }

                    if (!_isHostSessionActive && !_nginxService.IsRunning)
                    {
                        _settings.NginxPort = resolvedPort;
                        foreach (var ep in _allDiscoveredEndpoints)
                        {
                            ep.Port = resolvedPort;
                        }
                    }
                }

                _settings.Save();
                UpdateTopRightStatusBadges();
                UpdateHostLobbyCardUI();
                UpdateProgressUI($"Selected VPN Host: {selectedHost.HostName} ({selectedHost.VpnIp}:{resolvedPort})", 0);
                AppendLog($"[VPN] Selected Host '{selectedHost.HostName}' ({selectedHost.VpnProvider}) -> {selectedHost.VpnIp}:{resolvedPort} (Ping: {selectedHost.PingDisplay})");
            }
            return;
        }

        _steamService.SetConfiguredAppId(_settings.SteamAppId);
        bool wasAlreadyHooked = _steamService.IsSteamInitialized;
        if (!wasAlreadyHooked)
        {
            await PromptToLaunchSteamIfNotRunningAsync();
        }

        var win = new SteamFriendsWindow(_steamService, AppendLog)
        {
            Owner = this
        };
        if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.SelectedHostTarget))
        {
            ModeSteamP2PRadio.IsChecked = true;
            _settings.LastSteamTarget = win.SelectedHostTarget;
            SetTargetBoxProgrammatically(win.SelectedHostTarget);
        }

        if (!wasAlreadyHooked &&
            !_isHostSessionActive &&
            !_nginxService.IsRunning &&
            !_isClientConnectedToRemoteHost &&
            _steamService.IsSteamInitialized)
        {
            _steamService.ShutdownCurrentSession(AppendLog);
            UpdateTopRightStatusBadges();
            UpdateHostLobbyCardUI();
        }
    }

    private void ActivityLogFeatureBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow != null && _logWindow.IsLoaded)
        {
            _logWindow.Activate();
            _logWindow.Focus();
            return;
        }

        _logWindow = new ActivityLogWindow(
            () => _logBuffer.ToString(),
            () =>
            {
                _logBuffer.Clear();
                AppendLog("[INFO] Log cleared.");
            })
        {
            Owner = this
        };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    // =========================================================================
    // BOTTOM SMALLER FEATURE BUTTONS
    // =========================================================================
    private void BackupRestoreFeatureBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new BackupRestoreWindow(_settings, _backupService, AppendLog)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void LocationsSettingsFeatureBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings, AppendLog)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private void ExcludeFeatureBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new ExcludeWindow(_settings, AppendLog)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private async void SteamAppIdFeatureBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new SteamAppIdWindow(_settings, AppendLog)
        {
            Owner = this
        };

        if (win.ShowDialog() == true)
        {
            UpdateSteamAppIdButtonText();
            _steamService.SetConfiguredAppId(_settings.SteamAppId);

            if (_steamService.IsSteamInitialized || _nginxService.IsRunning || _isHostSessionActive || _isClientConnectedToRemoteHost)
            {
                bool ok = _steamService.TryInitialize(_settings.SteamAppId, AppendLog, forceReinit: true);
                if (!ok && !SteamP2PRelayService.IsSteamProcessRunning())
                {
                    ok = await PromptToLaunchSteamIfNotRunningAsync();
                }

                if (ok && (_nginxService.IsRunning || _isHostSessionActive))
                {
                    await _steamService.StartHostRelayAsync(_nginxService.ActivePort, _allDiscoveredEndpoints, AppendLog);
                    if (ModeSteamP2PRadio.IsChecked == true)
                    {
                        SetTargetBoxProgrammatically(_steamService.LocalRoomCode);
                    }
                }
                else if (ModeSteamP2PRadio.IsChecked == true)
                {
                    SetTargetBoxProgrammatically("");
                }
            }
            else
            {
                AppendLog($"[STEAM] Active Steam AppID set to {SteamP2PRelayService.GetAppIdDisplayName(_settings.SteamAppId)} (Standby — will hook when HOST or CLIENT starts).");
            }

            UpdateTopRightStatusBadges();
            UpdateHostLobbyCardUI();
        }
    }

    private void UpdateSteamAppIdButtonText()
    {
        uint id = _settings.SteamAppId > 0 ? _settings.SteamAppId : SteamP2PRelayService.DefaultBg3AppId;
        SteamAppIdFeatureBtn.Content = $"🆔 AppID: {id}";
    }

    private void AuthorLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch { }
    }

    private async Task<bool> PromptToLaunchSteamIfNotRunningAsync()
    {
        if (_steamService.TryInitialize(_settings.SteamAppId, AppendLog))
        {
            UpdateTopRightStatusBadges();
            return true;
        }

        var result = MessageBox.Show(
            this,
            $"Steam Client is not currently running or connected (Target AppID: {SteamP2PRelayService.GetAppIdDisplayName(_settings.SteamAppId)}).\n\n" +
            "Would you like to automatically launch Steam now (steam://open/main)?\n\n" +
            "• Click YES to start Steam automatically and wait for it to connect.\n" +
            "• Click NO if you want to start Steam manually (or use Direct IP / VPN mode).",
            "Steam Client Not Running — Launch Steam?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            AppendLog("[STEAM] Auto-launch declined. You can start Steam manually anytime or use Direct IP / VPN mode.");
            return false;
        }

        AppendLog("[STEAM] Launching Steam via 'steam://open/main' — waiting for Steam Client to start and sign in...");
        SteamP2PRelayService.LaunchSteamClientViaProtocol();

        for (int attempt = 1; attempt <= 15; attempt++)
        {
            UpdateProgressUI($"Waiting for Steam Client to start and connect ({attempt * 2}s / 30s)...", Math.Min(90, attempt * 6));
            await Task.Delay(2000);

            if (_steamService.TryInitialize(_settings.SteamAppId, AppendLog))
            {
                AppendLog($"[STEAM] Steam Client is now online and connected ({SteamP2PRelayService.GetAppIdDisplayName(_settings.SteamAppId)})!");
                UpdateProgressUI("Steam Client Connected!", 100);
                UpdateModeUI();
                UpdateTopRightStatusBadges();
                return true;
            }
        }

        AppendLog($"[WARN] Steam Client is still starting up or logging in (or AppID {_settings.SteamAppId} is not owned on this account; you can switch to AppID 480 via 'Steam AppID' button at bottom).");
        UpdateProgressUI("Steam still starting — try again once logged in", 0);
        return false;
    }

    private void OpenModsFolderFeatureBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string modsDir = Path.Combine(PathResolverService.GetLarianBasePath(_settings), "Mods");
            Directory.CreateDirectory(modsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = modsDir,
                UseShellExecute = true
            });
            AppendLog($"[INFO] Opened Mods folder: {modsDir}");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Could not open Mods folder: {ex.Message}");
        }
    }

    private async void StopServerFeatureBtn_Click(object sender, RoutedEventArgs e)
    {
        await StopHostingAndOperationsAsync();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.LastSteamTarget = "";
        _settings.Save();
        _portDebounceCts?.Cancel();
        _activeOperationCts?.Cancel();
        _hostRealTimePingCts?.Cancel();
        _clientDirectPingCts?.Cancel();
        _ = _upnpService.RemovePortForwardAsync();
        try { _sharedTelemetryHttpClient.Dispose(); } catch { }
        _vpnDiscoveryService.Dispose();
        _nginxService.Dispose();
        _steamService.Dispose();
        RuntimeTempCleanupService.CleanupAllOnExit();
    }
}
