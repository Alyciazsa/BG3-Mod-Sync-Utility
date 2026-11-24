using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BG3ModSync.Models;
using Microsoft.Win32;
using Steamworks;
using Steamworks.Data;

namespace BG3ModSync.Services;

public class SteamFriendInfo
{
    public ulong SteamId { get; set; }
    public ulong LobbyId { get; set; }
    public string Name { get; set; } = "";
    public string StatusText { get; set; } = "";
}

public class HostDirectEndpointsMetadata
{
    public ulong HostSteamId { get; set; }
    public string HostName { get; set; } = "";
    public int NginxPort { get; set; } = 27000;
    public string WanIp { get; set; } = "";
    public List<string> LanIps { get; set; } = new();
    public List<string> VpnIps { get; set; } = new();
    public int FileCount { get; set; }
    public long TotalModBytes { get; set; }
    public string HostingState { get; set; } = "";
    public List<string> VpnProviders { get; set; } = new();
}

public class SteamP2PRelayService : IDisposable
{
    public const uint DefaultBg3AppId = 1086940;
    public const uint SpacewarAppId = 480;
    private const byte CmdOpenStream = 0x01;
    private const byte CmdDataChunk = 0x02;
    private const byte CmdCloseStream = 0x03;
    private const byte CmdHandshakeEndpoints = 0x04;
    private const byte CmdPing = 0x05;
    private const byte CmdPong = 0x06;
    private const byte CmdClientTelemetry = 0x07;
    private const int ChunkSize = 256 * 1024; // 256 KB per reliable P2P message for multi-gigabit throughput

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    private static readonly object _webRtcLoadLock = new();
    private static IntPtr _preloadedSteamWebRtcHandle;
    private static string _preloadedSteamWebRtcPath = "";

    private static readonly ConcurrentDictionary<ulong, ImageSource> _avatarCache = new();
    private readonly object _steamLifecycleLock = new();
    private CancellationTokenSource? _callbackLoopCts;
    private Task? _callbackLoopTask;
    private HostP2PSocketManager? _hostSocketManager;
    private ClientP2PConnectionManager? _clientConnectionManager;
    private TcpListener? _clientLocalListener;
    private CancellationTokenSource? _clientTunnelCts;
    private CancellationTokenSource? _clientPingLoopCts;
    private HostDirectEndpointsMetadata _localHostEndpoints = new();
    private Action<string>? _activeLogCallback;
    private bool _eventsSubscribed;
    private bool _debugOutputSubscribed;
    private long _lastLocalIcePrivacyHintTick;

    public static bool IsSteamWebRtcPreloaded => _preloadedSteamWebRtcHandle != IntPtr.Zero;
    public static string PreloadedSteamWebRtcPath => _preloadedSteamWebRtcPath;

    /// <summary>
    /// Pre-loads Valve's steamwebrtc64.dll from the local Steam client install directory via Win32 LoadLibraryW
    /// BEFORE SteamClient.Init so SteamNetworkingSockets Direct UDP ICE does not fail with
    /// "ICE failed 3999 Failed to load steamwebrtc64.dll" (without mutating global DLL search paths via SetDllDirectory).
    /// </summary>
    public static bool TryPreloadSteamWebRtc(Action<string>? logCallback = null)
    {
        lock (_webRtcLoadLock)
        {
            if (_preloadedSteamWebRtcHandle != IntPtr.Zero)
            {
                return true;
            }

            var candidateDirs = new List<string>();
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidateDir(string? rawDir)
            {
                if (string.IsNullOrWhiteSpace(rawDir)) return;
                string normalized = rawDir.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
                if (!string.IsNullOrWhiteSpace(normalized) && seenDirs.Add(normalized))
                {
                    candidateDirs.Add(normalized);
                }
            }

            try
            {
                using var hkcuSteam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                AddCandidateDir(hkcuSteam?.GetValue("SteamPath") as string);
            }
            catch { }

            try
            {
                using var activeProc = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                string? clientDll64 = activeProc?.GetValue("SteamClientDll64") as string;
                if (!string.IsNullOrWhiteSpace(clientDll64))
                {
                    AddCandidateDir(Path.GetDirectoryName(clientDll64.Replace('/', '\\')));
                }
            }
            catch { }

            try
            {
                using var hklmWow = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                AddCandidateDir(hklmWow?.GetValue("InstallPath") as string);
            }
            catch { }

            try
            {
                using var hklmSteam = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                AddCandidateDir(hklmSteam?.GetValue("InstallPath") as string);
            }
            catch { }

            try
            {
                var steamProcs = System.Diagnostics.Process.GetProcessesByName("steam");
                foreach (var proc in steamProcs)
                {
                    try
                    {
                        string? exePath = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(exePath))
                        {
                            AddCandidateDir(Path.GetDirectoryName(exePath));
                        }
                    }
                    catch { }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            foreach (string dir in candidateDirs)
            {
                string dllPath = Path.Combine(dir, "steamwebrtc64.dll");
                if (!File.Exists(dllPath)) continue;

                IntPtr handle = LoadLibraryW(dllPath);
                if (handle != IntPtr.Zero)
                {
                    _preloadedSteamWebRtcHandle = handle;
                    _preloadedSteamWebRtcPath = dllPath;
                    logCallback?.Invoke($"[STEAM-ICE] Preloaded Steam WebRTC ICE module: {dllPath}");
                    return true;
                }

                int win32Err = Marshal.GetLastWin32Error();
                logCallback?.Invoke($"[WARN] [STEAM-ICE] LoadLibraryW failed for '{dllPath}' (Win32 error {win32Err}).");
            }

            logCallback?.Invoke("[WARN] [STEAM-ICE] Could not locate or preload steamwebrtc64.dll from Steam install directory — Direct UDP ICE hole-punching may fall back to Valve SDR Relay.");
            return false;
        }
    }

    public uint ActiveAppId { get; private set; } = DefaultBg3AppId;
    public bool IsSteamInitialized { get; private set; }
    public ulong LocalSteamId { get; private set; }
    public string LocalPersonaName { get; private set; } = "Offline";
    public bool IsClientTunnelActive => (_clientConnectionManager != null && _clientConnectionManager.IsConnected) || _clientPingLoopCts != null;
    public bool HasActiveP2PDataConnection => _clientConnectionManager != null && _clientConnectionManager.IsConnected;
    public bool HasConnectedP2PClients => _hostSocketManager != null && _hostSocketManager.HasConnectedClients;
    public string PunchedHostEndpoint { get; private set; } = "";
    public string ObservedClientSelfIpPort { get; private set; } = "";
    public bool LastConnectionObservedRelay { get; private set; }
    public HostDirectEndpointsMetadata? LastHandshakedHostEndpoints { get; private set; }
    public int LastClientRttPingMs { get; private set; } = 1;

    public static string GetAppIdDisplayName(uint appId) => appId switch
    {
        1086940 => "Baldur's Gate 3 (1086940)",
        480 => "Spacewar Experimental/Test (480)",
        _ => $"Custom AppID ({appId})"
    };

    public void SetConfiguredAppId(uint appId)
    {
        if (appId == 0) appId = DefaultBg3AppId;
        if (!IsSteamInitialized)
        {
            ActiveAppId = appId;
        }
    }

    public static ulong TryGetActiveSteamUserIdFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            object? val = key?.GetValue("ActiveUser");
            uint accountId = val switch
            {
                int i when i > 0 => (uint)i,
                uint u when u > 0 => u,
                _ => 0
            };
            return accountId != 0 ? 76561197960265728UL + accountId : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsSteamProcessRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void LaunchSteamClientViaProtocol()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "steam://open/main",
                UseShellExecute = true
            });
        }
        catch { }
    }
    public Lobby? ActiveLobby { get; private set; }
    public string LocalRoomCode => ActiveLobby.HasValue
        ? EncodeRoomCode(ActiveLobby.Value.Id.Value)
        : EncodeRoomCode(LocalSteamId);
    public int ClientLocalTunnelPort { get; private set; }
    public string NearestRelaySummary { get; private set; } = "";

    public event Action<string>? OnLog;
    public event Action<ulong>? OnLobbyHostJoinRequested;
    /// <summary>
    /// Parameters: (string routeLabel, bool isRelayWarning, string relayServerAndPing)
    /// </summary>
    public event Action<string, bool, string>? OnRouteStatusChanged;
    public event Action<ClientLobbyTelemetry>? OnClientTelemetryReceived;
    public event Action<string>? OnClientDisconnected;

    /// <summary>
    /// Initiates a Facepunch Steamworks async CallResult inside _steamLifecycleLock (preventing concurrent CClientPipe IPC access)
    /// and resumes continuations asynchronously on the ThreadPool (never inside Dispatch.Frame while m_bOutstandingCallback is true!).
    /// </summary>
    private Task<T> StartOffCallbackSteamCall<T>(Func<Task<T>> steamCallFactory)
    {
        Task<T> rawTask;
        lock (_steamLifecycleLock)
        {
            rawTask = steamCallFactory();
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = rawTask.ContinueWith(static (t, state) =>
        {
            var dst = (TaskCompletionSource<T>)state!;
            if (t.IsCanceled) dst.TrySetCanceled();
            else if (t.IsFaulted) dst.TrySetException(t.Exception!.InnerExceptions);
            else dst.TrySetResult(t.Result);
        }, tcs, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return tcs.Task;
    }

    public async Task<ImageSource?> GetSteamAvatarBitmapAsync(ulong steamId)
    {
        if (steamId == 0 || !IsSteamInitialized || !SteamClient.IsValid) return null;
        if (_avatarCache.TryGetValue(steamId, out var cached)) return cached;

        try
        {
            lock (_steamLifecycleLock)
            {
                if (!IsSteamInitialized || !SteamClient.IsValid) return null;
                SteamFriends.RequestUserInformation(steamId, false);
            }
            var img = await StartOffCallbackSteamCall(() => SteamFriends.GetMediumAvatarAsync(steamId));
            if (!img.HasValue || img.Value.Data == null || img.Value.Width == 0 || img.Value.Height == 0)
            {
                await Task.Delay(350);
                if (!IsSteamInitialized || !SteamClient.IsValid) return null;
                img = await StartOffCallbackSteamCall(() => SteamFriends.GetMediumAvatarAsync(steamId));
            }

            if (img.HasValue && img.Value.Data != null && img.Value.Width > 0 && img.Value.Height > 0)
            {
                byte[] rgba = img.Value.Data;
                int w = (int)img.Value.Width;
                int h = (int)img.Value.Height;
                byte[] bgra = new byte[rgba.Length];
                for (int i = 0; i + 3 < rgba.Length; i += 4)
                {
                    bgra[i + 0] = rgba[i + 2];
                    bgra[i + 1] = rgba[i + 1];
                    bgra[i + 2] = rgba[i + 0];
                    bgra[i + 3] = rgba[i + 3];
                }
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                bmp.Freeze();
                _avatarCache[steamId] = bmp;
                return bmp;
            }
        }
        catch { }

        return null;
    }

    public void SendClientTelemetry(ClientLobbyTelemetry telemetry)
    {
        try
        {
            if (_clientConnectionManager != null && _clientConnectionManager.IsConnected)
            {
                _clientConnectionManager.SendTelemetry(telemetry);
            }
        }
        catch { }
    }

    private static readonly Dictionary<string, string> ValvePopCityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sgp"] = "Singapore",
        ["hkg"] = "Hong Kong",
        ["tyo"] = "Tokyo",
        ["tyo1"] = "Tokyo",
        ["seo"] = "Seoul",
        ["maa"] = "Chennai",
        ["maa2"] = "Chennai",
        ["bom"] = "Mumbai",
        ["bom2"] = "Mumbai",
        ["dfjr"] = "Fujairah",
        ["dxb"] = "Dubai",
        ["gum"] = "Guam",
        ["syd"] = "Sydney",
        ["fra"] = "Frankfurt",
        ["ams"] = "Amsterdam",
        ["lhr"] = "London",
        ["par"] = "Paris",
        ["mad"] = "Madrid",
        ["vie"] = "Vienna",
        ["waw"] = "Warsaw",
        ["sto"] = "Stockholm",
        ["sto2"] = "Stockholm",
        ["iad"] = "Virginia",
        ["ord"] = "Chicago",
        ["dfw"] = "Dallas",
        ["lax"] = "Los Angeles",
        ["sea"] = "Seattle",
        ["atl"] = "Atlanta",
        ["den"] = "Denver",
        ["gru"] = "Sao Paulo",
        ["scl"] = "Santiago",
        ["lim"] = "Lima",
        ["eze"] = "Buenos Aires",
        ["jnb"] = "Johannesburg",
        ["pek"] = "Beijing",
        ["sha"] = "Shanghai",
        ["ctu"] = "Chengdu",
        ["tsn"] = "Tianjin",
        ["can"] = "Guangzhou"
    };

    public static string ParseBestRelayFromPingLocation(string? rawPingLocation)
    {
        if (string.IsNullOrWhiteSpace(rawPingLocation)) return "";

        string bestCity = "";
        int bestPing = int.MaxValue;

        foreach (var entry in rawPingLocation.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eqIdx = entry.IndexOf('=');
            if (eqIdx <= 0) continue;

            string popCode = entry.Substring(0, eqIdx).Trim();
            string valPart = entry.Substring(eqIdx + 1).Trim();

            int endIdx = 0;
            while (endIdx < valPart.Length && char.IsDigit(valPart[endIdx]))
                endIdx++;

            if (endIdx > 0 && int.TryParse(valPart.Substring(0, endIdx), out int pingMs))
            {
                if (pingMs < bestPing)
                {
                    bestPing = pingMs;
                    bestCity = ValvePopCityNames.TryGetValue(popCode, out var mapped) ? mapped : popCode.ToUpperInvariant();
                }
            }
        }

        if (!string.IsNullOrEmpty(bestCity) && bestPing < int.MaxValue)
        {
            return $"{bestCity} ({bestPing}ms)";
        }
        return "";
    }

    private static System.Reflection.PropertyInfo? _snsInternalProp;
    private static System.Reflection.MethodInfo? _initAuthMethod;
    private static System.Reflection.MethodInfo? _getConnInfoMethod;
    private static System.Reflection.MethodInfo? _getQuickStatusMethod;
    private static System.Reflection.MethodInfo? _recvMessagesOnPollGroupMethod;
    private static System.Reflection.MethodInfo? _recvMessagesOnConnMethod;
    private static System.Reflection.FieldInfo? _socketPollGroupField;
    private static Action<SocketManager, IntPtr>? _sockReceiveMessageDelegate;
    private static Action<ConnectionManager, IntPtr>? _connReceiveMessageDelegate;
    private static System.Reflection.FieldInfo? _quickPingField;
    private static System.Reflection.FieldInfo? _connInfoPopRelayField;
    private static System.Reflection.FieldInfo? _popIdValueField;
    private static System.Reflection.FieldInfo? _connInfoIdentityField;
    private static System.Reflection.FieldInfo? _connInfoEndDebugField;
    private static System.Reflection.FieldInfo? _connInfoEndReasonField;
    private static System.Reflection.MethodInfo? _utilsSetConfigIntMethod;
    private static System.Reflection.MethodInfo? _utilsGetConfigIntMethod;
    private static Type? _netConfigEnumType;
    private static bool _reflectionInitialized;

    private static void EnsureReflectionInitialized()
    {
        if (_reflectionInitialized) return;
        _reflectionInitialized = true;
        try
        {
            const System.Reflection.BindingFlags allInstance =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            const System.Reflection.BindingFlags allStatic =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

            var snsType = typeof(SteamNetworkingSockets);
            _snsInternalProp = snsType.GetProperty("Internal", allStatic);
            var isnsType = snsType.Assembly.GetType("Steamworks.ISteamNetworkingSockets");
            if (isnsType != null)
            {
                _initAuthMethod = isnsType.GetMethod("InitAuthentication", allInstance, null, Type.EmptyTypes, null);
                _getConnInfoMethod = isnsType.GetMethod("GetConnectionInfo", allInstance);
                _getQuickStatusMethod = isnsType.GetMethod("GetQuickConnectionStatus", allInstance);
                _recvMessagesOnPollGroupMethod = isnsType.GetMethod("ReceiveMessagesOnPollGroup", allInstance);
                _recvMessagesOnConnMethod = isnsType.GetMethod("ReceiveMessagesOnConnection", allInstance);
            }
            _socketPollGroupField = typeof(SocketManager).GetField("pollGroup", allInstance);
            var sockRecvMsg = typeof(SocketManager).GetMethod("ReceiveMessage", allInstance);
            if (sockRecvMsg != null)
            {
                _sockReceiveMessageDelegate = (Action<SocketManager, IntPtr>)Delegate.CreateDelegate(typeof(Action<SocketManager, IntPtr>), sockRecvMsg);
            }
            var connRecvMsg = typeof(ConnectionManager).GetMethod("ReceiveMessage", allInstance);
            if (connRecvMsg != null)
            {
                _connReceiveMessageDelegate = (Action<ConnectionManager, IntPtr>)Delegate.CreateDelegate(typeof(Action<ConnectionManager, IntPtr>), connRecvMsg);
            }
            var qType = snsType.Assembly.GetType("Steamworks.Data.SteamNetworkingQuickConnectionStatus");
            if (qType != null)
            {
                _quickPingField = qType.GetField("Ping", allInstance);
            }
            _connInfoPopRelayField = typeof(ConnectionInfo).GetField("popRelay", allInstance);
            if (_connInfoPopRelayField != null)
            {
                _popIdValueField = _connInfoPopRelayField.FieldType.GetField("Value", allInstance);
            }
            _connInfoIdentityField = typeof(ConnectionInfo).GetField("identity", allInstance);
            _connInfoEndDebugField = typeof(ConnectionInfo).GetField("endDebug", allInstance);
            _connInfoEndReasonField = typeof(ConnectionInfo).GetField("endReason", allInstance);

            var utilsType = typeof(SteamNetworkingUtils);
            _utilsSetConfigIntMethod = utilsType.GetMethod("SetConfigInt", allStatic);
            _utilsGetConfigIntMethod = utilsType.GetMethod("GetConfigInt", allStatic);
            _netConfigEnumType = utilsType.Assembly.GetType("Steamworks.NetConfig");
        }
        catch { }
    }

    /// <summary>
    /// Workaround for Facepunch.Steamworks 2.3.3 bug where ConnectionInfo.Identity reads 'this.address'
    /// instead of private field 'this.identity', causing info.Identity.SteamId to always return 0.
    /// </summary>
    public static ulong GetRemoteSteamId(ConnectionInfo info)
    {
        try
        {
            EnsureReflectionInitialized();
            if (_connInfoIdentityField != null)
            {
                object? idObj = _connInfoIdentityField.GetValue(info);
                if (idObj is NetIdentity netId)
                {
                    ulong sid = netId.SteamId.Value;
                    if (sid != 0) return sid;
                }
            }
        }
        catch { }

        try
        {
            return info.Identity.SteamId.Value;
        }
        catch
        {
            return 0;
        }
    }

    public static string GetConnectionEndDebug(ConnectionInfo info)
    {
        try
        {
            EnsureReflectionInitialized();
            if (_connInfoEndDebugField != null && _connInfoEndDebugField.GetValue(info) is string s)
            {
                return s.Trim();
            }
        }
        catch { }
        return "";
    }

    public static int GetConnectionEndReasonCode(ConnectionInfo info)
    {
        try
        {
            EnsureReflectionInitialized();
            if (_connInfoEndReasonField != null && _connInfoEndReasonField.GetValue(info) is int code)
            {
                return code;
            }
        }
        catch { }
        return (int)info.EndReason;
    }

    public static string FormatDisconnectDiagnostics(ConnectionInfo info)
    {
        int reasonCode = GetConnectionEndReasonCode(info);
        NetConnectionEnd reasonEnum = info.EndReason;
        string reasonDisplay = Enum.IsDefined(typeof(NetConnectionEnd), reasonEnum)
            ? $"{reasonEnum} ({reasonCode})"
            : reasonCode.ToString();
        string endDebug = GetConnectionEndDebug(info);
        string debugDisplay = string.IsNullOrEmpty(endDebug) ? "none" : endDebug;
        return $"State={info.State}, EndReason={reasonDisplay}, EndDebug=\"{debugDisplay}\"";
    }

    public static SteamNetworkingAvailability? TryInitSteamAuthentication(Action<string>? logCallback = null)
    {
        try
        {
            EnsureReflectionInitialized();
            var internalObj = _snsInternalProp?.GetValue(null);
            if (internalObj != null && _initAuthMethod != null)
            {
                object? res = _initAuthMethod.Invoke(internalObj, null);
                if (res is SteamNetworkingAvailability avail)
                {
                    if (avail == SteamNetworkingAvailability.Failed || avail == SteamNetworkingAvailability.CannotTry)
                    {
                        logCallback?.Invoke($"[WARN] [STEAM-AUTH] SteamNetworkingSockets.InitAuthentication() returned {avail}.");
                    }
                    else
                    {
                        logCallback?.Invoke($"[STEAM-AUTH] Pre-initialized SteamNetworkingSockets authentication cert (Status: {avail}).");
                    }
                    return avail;
                }
            }
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[WARN] [STEAM-AUTH] InitAuthentication() invocation failed: {ex.Message}");
        }
        return null;
    }

    public static int? TryGetNetworkingConfigInt(int configId)
    {
        try
        {
            EnsureReflectionInitialized();
            if (_utilsGetConfigIntMethod != null && _netConfigEnumType != null)
            {
                object? res = _utilsGetConfigIntMethod.Invoke(null, new object[] { Enum.ToObject(_netConfigEnumType, configId) });
                if (res is int v) return v;
            }
        }
        catch { }
        return null;
    }

    public static uint? TryGetPopRelayId(ConnectionInfo info)
    {
        try
        {
            EnsureReflectionInitialized();
            if (_connInfoPopRelayField != null && _popIdValueField != null)
            {
                object? popObj = _connInfoPopRelayField.GetValue(info);
                if (popObj != null)
                {
                    object? valObj = _popIdValueField.GetValue(popObj);
                    if (valObj is uint u) return u;
                }
            }
        }
        catch { }
        return null;
    }

    public static string DecodePopId(uint popId)
    {
        if (popId == 0) return "";
        char[] chars = new char[4];
        int count = 0;
        for (int shift = 24; shift >= 0; shift -= 8)
        {
            byte b = (byte)((popId >> shift) & 0xFF);
            if (b != 0) chars[count++] = (char)b;
        }
        string code = new string(chars, 0, count).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code)) return "Valve SDR";
        return ValvePopCityNames.TryGetValue(code, out var city) ? city : code.ToUpperInvariant();
    }

    public static bool TryGetLiveConnectionInfo(Connection connection, ref ConnectionInfo info)
    {
        try
        {
            EnsureReflectionInitialized();
            var internalObj = _snsInternalProp?.GetValue(null);
            if (internalObj != null && _getConnInfoMethod != null)
            {
                object[] args = new object[] { connection, info };
                bool ok = (bool)(_getConnInfoMethod.Invoke(internalObj, args) ?? false);
                if (ok && args[1] is ConnectionInfo updated)
                {
                    info = updated;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static int GetQuickConnectionPingMs(Connection connection)
    {
        try
        {
            EnsureReflectionInitialized();
            var internalObj = _snsInternalProp?.GetValue(null);
            if (internalObj != null && _getQuickStatusMethod != null && _quickPingField != null)
            {
                var qType = _quickPingField.DeclaringType;
                if (qType != null)
                {
                    object statusStruct = Activator.CreateInstance(qType)!;
                    object[] args = new object[] { connection, statusStruct };
                    bool ok = (bool)(_getQuickStatusMethod.Invoke(internalObj, args) ?? false);
                    if (ok && args[1] != null)
                    {
                        int ping = (int)(_quickPingField.GetValue(args[1]) ?? -1);
                        if (ping >= 0) return ping;
                    }
                }
            }
        }
        catch { }
        return -1;
    }

    public static string ResolvePunchedIpAndPort(
        Connection connection,
        ConnectionInfo info,
        HostDirectEndpointsMetadata? remoteEndpoints = null,
        HostDirectEndpointsMetadata? localEndpoints = null)
    {
        TryGetLiveConnectionInfo(connection, ref info);

        try
        {
            var netAddr = info.Address;
            var ipAddr = netAddr.Address;
            if (ipAddr != null && netAddr.Port > 0)
            {
                if (ipAddr.IsIPv4MappedToIPv6)
                    ipAddr = ipAddr.MapToIPv4();
                string ipStr = ipAddr.ToString();
                if (!string.IsNullOrWhiteSpace(ipStr) && ipStr != "0.0.0.0" && ipStr != "::" && ipStr != "::1")
                {
                    return $"{ipStr}:{netAddr.Port}";
                }
            }
        }
        catch { }

        // Check DetailedStatus for any active ICE candidate IPv4:Port
        try
        {
            string detailed = connection.DetailedStatus() ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(
                detailed,
                @"\b(?<ip>(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}):(?<port>\d{2,5})\b");
            while (match.Success)
            {
                string ip = match.Groups["ip"].Value;
                string port = match.Groups["port"].Value;
                if (ip != "0.0.0.0" && ip != "127.0.0.1" && port != "0")
                {
                    return $"{ip}:{port}";
                }
                match = match.NextMatch();
            }
        }
        catch { }

        // Fallback to exchanged endpoint metadata (WAN / LAN / VPN + Port)
        if (remoteEndpoints != null)
        {
            int port = remoteEndpoints.NginxPort > 0 ? remoteEndpoints.NginxPort : 27000;

            if (localEndpoints != null)
            {
                // 1. Check matching VPN subnet (including /10 RFC 6598 Tailscale/CGNAT range 100.64.0.0/10)
                foreach (var remVpn in remoteEndpoints.VpnIps.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    if (IPAddress.TryParse(remVpn, out var rIp))
                    {
                        byte[] rb = rIp.GetAddressBytes();
                        foreach (var locVpn in localEndpoints.VpnIps.Where(x => !string.IsNullOrWhiteSpace(x)))
                        {
                            if (IPAddress.TryParse(locVpn, out var lIp))
                            {
                                byte[] lb = lIp.GetAddressBytes();
                                bool sameVpnScope = (rb[0] == lb[0] && rb[1] == lb[1]) ||
                                                    (NetworkDiscoveryService.IsRfc6598CgnatIp(rIp) && NetworkDiscoveryService.IsRfc6598CgnatIp(lIp));
                                if (sameVpnScope)
                                    return $"{remVpn}:{port}";
                            }
                        }
                    }
                }

                // 2. Check matching LAN subnet
                foreach (var remLan in remoteEndpoints.LanIps.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    if (IPAddress.TryParse(remLan, out var rIp))
                    {
                        byte[] rb = rIp.GetAddressBytes();
                        foreach (var locLan in localEndpoints.LanIps.Where(x => !string.IsNullOrWhiteSpace(x)))
                        {
                            if (IPAddress.TryParse(locLan, out var lIp))
                            {
                                byte[] lb = lIp.GetAddressBytes();
                                if (rb[0] == lb[0] && rb[1] == lb[1])
                                    return $"{remLan}:{port}";
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(remoteEndpoints.WanIp))
                return $"{remoteEndpoints.WanIp}:{port}";
            if (remoteEndpoints.LanIps.Count > 0 && !string.IsNullOrWhiteSpace(remoteEndpoints.LanIps[0]))
                return $"{remoteEndpoints.LanIps[0]}:{port}";
            if (remoteEndpoints.VpnIps.Count > 0 && !string.IsNullOrWhiteSpace(remoteEndpoints.VpnIps[0]))
                return $"{remoteEndpoints.VpnIps[0]}:{port}";

            return $"127.0.0.1:{port}";
        }

        return "127.0.0.1:27000";
    }

    public string RefreshNearestRelaySummary()
    {
        if (!IsSteamInitialized || !SteamClient.IsValid) return NearestRelaySummary;
        try
        {
            string? pingLocStr = SteamNetworkingUtils.LocalPingLocation?.ToString();
            string bestRelay = ParseBestRelayFromPingLocation(pingLocStr);
            if (!string.IsNullOrEmpty(bestRelay))
            {
                NearestRelaySummary = bestRelay;
            }
        }
        catch { }
        return NearestRelaySummary;
    }

    public static (string RouteLabel, bool IsRelayedThroughValve, string RelayWithPing) InspectSteamConnectionRoute(
        Connection connection,
        ConnectionInfo info,
        string fallbackRelaySummary,
        HostDirectEndpointsMetadata? remoteEndpoints = null,
        HostDirectEndpointsMetadata? localEndpoints = null,
        int overridePingMs = -1,
        ulong localSteamId = 0)
    {
        try
        {
            TryGetLiveConnectionInfo(connection, ref info);
            string detailed = "";
            try { detailed = connection.DetailedStatus() ?? ""; } catch { }

            int pingMs = overridePingMs >= 0 ? overridePingMs : GetQuickConnectionPingMs(connection);

            if (pingMs < 0)
            {
                int pingIdx = detailed.IndexOf("Ping:", StringComparison.OrdinalIgnoreCase);
                if (pingIdx >= 0)
                {
                    int start = pingIdx + 5;
                    int end = detailed.IndexOf("ms", start, StringComparison.OrdinalIgnoreCase);
                    if (end > start)
                    {
                        string numStr = detailed.Substring(start, end - start).Trim();
                        int.TryParse(numStr, out pingMs);
                    }
                }
            }

            string relayDisplay = fallbackRelaySummary;
            if (!string.IsNullOrEmpty(relayDisplay) && pingMs >= 0)
            {
                int parenIdx = relayDisplay.IndexOf('(');
                string cityOnly = parenIdx > 0 ? relayDisplay.Substring(0, parenIdx).Trim() : relayDisplay;
                relayDisplay = $"{cityOnly} ({pingMs}ms)";
            }
            else if (string.IsNullOrEmpty(relayDisplay))
            {
                relayDisplay = pingMs >= 0 ? $"SDR ({pingMs}ms)" : "Valve SDR";
            }

            string punchedEndpoint = ResolvePunchedIpAndPort(connection, info, remoteEndpoints, localEndpoints);

            // Authoritative Valve SDK check: SteamNetConnectionInfo_t.m_idPOPRelay == 0 means Direct ICE UDP (no SDR relay!)
            uint? popRelayId = TryGetPopRelayId(info);
            ulong peerSteamId = GetRemoteSteamId(info);
            ulong effectiveLocalSteamId = localSteamId != 0 ? localSteamId : (localEndpoints?.HostSteamId ?? 0);
            if (effectiveLocalSteamId == 0)
            {
                try
                {
                    if (SteamClient.IsValid) effectiveLocalSteamId = SteamClient.SteamId.Value;
                }
                catch { }
            }

            // Same-PC/same-account loopback ONLY when remote identity actually matches LocalSteamId AND popRelay == 0
            // (Never treat peerSteamId == 0 as loopback — Facepunch 2.3.3's info.Identity bug caused false 0s!)
            bool isLoopbackSelf = effectiveLocalSteamId != 0
                               && peerSteamId == effectiveLocalSteamId
                               && (!popRelayId.HasValue || popRelayId.Value == 0);
            if (isLoopbackSelf)
            {
                int dispPing = pingMs >= 0 ? pingMs : 1;
                return ($"P2P Hole-Punch -> {punchedEndpoint} • Ping: {dispPing}ms", false, relayDisplay);
            }

            if (popRelayId.HasValue)
            {
                if (popRelayId.Value == 0)
                {
                    string directPing = pingMs >= 0 ? $"{pingMs}ms" : "Measuring...";
                    return ($"P2P Hole-Punch -> {punchedEndpoint} • Ping: {directPing}", false, relayDisplay);
                }

                string popCity = DecodePopId(popRelayId.Value);
                string sdrDisplay = pingMs >= 0 ? $"{popCity} ({pingMs}ms)" : popCity;
                return ($"Steam Relay: {sdrDisplay}", true, sdrDisplay);
            }

            // Fallback heuristic if popRelay reflection was unavailable
            bool hasSdrRouter = detailed.Contains("Primary router:", StringComparison.OrdinalIgnoreCase)
                             || detailed.Contains("Backup router:", StringComparison.OrdinalIgnoreCase)
                             || detailed.Contains("Relay:", StringComparison.OrdinalIgnoreCase);

            bool hasDirectIce = detailed.Contains("ICE", StringComparison.OrdinalIgnoreCase)
                             || detailed.Contains("Direct", StringComparison.OrdinalIgnoreCase)
                             || (!hasSdrRouter && info.Address.Port > 0);

            if (hasDirectIce && !hasSdrRouter)
            {
                string directPing = pingMs >= 0 ? $"{pingMs}ms" : "Measuring...";
                return ($"P2P Hole-Punch -> {punchedEndpoint} • Ping: {directPing}", false, relayDisplay);
            }

            return ($"Steam Relay: {relayDisplay}", true, relayDisplay);
        }
        catch
        {
            string rel = !string.IsNullOrEmpty(fallbackRelaySummary) ? fallbackRelaySummary : "Valve SDR";
            return ($"Steam Relay: {rel}", true, rel);
        }
    }

    public bool TryInitialize(Action<string>? logCallback = null)
    {
        return TryInitialize(ActiveAppId > 0 ? ActiveAppId : DefaultBg3AppId, logCallback, forceReinit: false);
    }

    public bool TryInitialize(uint appId, Action<string>? logCallback = null, bool forceReinit = false)
    {
        if (appId == 0) appId = DefaultBg3AppId;
        if (logCallback != null)
        {
            _activeLogCallback = logCallback;
        }

        if (IsSteamInitialized && SteamClient.IsValid)
        {
            if (!forceReinit && ActiveAppId == appId)
                return true;

            ShutdownCurrentSession();
        }

        ActiveAppId = appId;

        try
        {
            // Critical: Pre-load Valve's steamwebrtc64.dll from the Steam install path BEFORE SteamClient.Init
            // so Direct UDP ICE does not fail with "ICE failed 3999 Failed to load steamwebrtc64.dll".
            TryPreloadSteamWebRtc(logCallback);

            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());

            string appIdFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt");
            bool createdTempAppId = false;
            try
            {
                File.WriteAllText(appIdFile, appId.ToString());
                RuntimeTempCleanupService.TrackTempFile(appIdFile);
                createdTempAppId = true;
            }
            catch { }

            SteamClient.Init(appId, asyncCallbacks: false);

            // Immediately remove temporary steam_appid.txt after SteamAPI_Init has read it
            if (createdTempAppId)
            {
                try { File.Delete(appIdFile); } catch { }
            }

            ConfigureSteamNetworkingDebugOutput();
            ConfigureDirectHolePunchAndHighThroughput(logCallback);
            SteamNetworkingUtils.InitRelayNetworkAccess();
            TryInitSteamAuthentication(logCallback);
            _ = Task.Run(EnsureAppP2PFirewallRule);

            IsSteamInitialized = SteamClient.IsValid;
            if (IsSteamInitialized)
            {
                LocalSteamId = SteamClient.SteamId.Value;
                LocalPersonaName = SteamClient.Name;

                if (!_eventsSubscribed)
                {
                    SteamFriends.OnGameLobbyJoinRequested += HandleGameLobbyJoinRequested;
                    SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
                    _eventsSubscribed = true;
                }

                StartCallbackPump();
                var pingWaitToken = _callbackLoopCts?.Token ?? CancellationToken.None;
                logCallback?.Invoke($"[STEAM] Connected to Steam ({GetAppIdDisplayName(appId)}) as '{LocalPersonaName}' | SteamID: {LocalSteamId} | Room Code: {LocalRoomCode}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (!pingWaitToken.IsCancellationRequested && sw.Elapsed.TotalSeconds < 15)
                        {
                            string? pingLocStr = null;
                            bool ready = false;
                            lock (_steamLifecycleLock)
                            {
                                if (pingWaitToken.IsCancellationRequested || !IsSteamInitialized || !SteamClient.IsValid)
                                    return;
                                if (SteamNetworkingUtils.Status == SteamNetworkingAvailability.Current)
                                {
                                    ready = true;
                                    pingLocStr = SteamNetworkingUtils.LocalPingLocation?.ToString();
                                }
                            }

                            if (ready)
                            {
                                string bestRelay = ParseBestRelayFromPingLocation(pingLocStr);
                                if (!string.IsNullOrEmpty(bestRelay))
                                {
                                    NearestRelaySummary = bestRelay;
                                    logCallback?.Invoke($"[STEAM-SDR] Nearest Valve Signaling/Relay POP: {bestRelay} (Direct Hole-Punch & Handoff Enabled)");
                                    OnRouteStatusChanged?.Invoke("", false, bestRelay);
                                }
                                return;
                            }

                            await Task.Delay(100, pingWaitToken).ConfigureAwait(false);
                        }
                    }
                    catch { }
                }, pingWaitToken);

                return true;
            }
        }
        catch (Exception ex)
        {
            IsSteamInitialized = false;
            logCallback?.Invoke($"[STEAM] Steam client not detected or AppID {appId} initialization skipped ({ex.Message}).");
        }

        return false;
    }

    public void ShutdownCurrentSession(Action<string>? logCallback = null)
    {
        _callbackLoopCts?.Cancel();
        try { _callbackLoopTask?.Wait(2000); } catch { }
        _callbackLoopTask = null;

        StopClientRelayTunnel();
        StopHostRelay();

        lock (_steamLifecycleLock)
        {
            try
            {
                if (SteamClient.IsValid)
                {
                    try
                    {
                        SteamFriends.ClearRichPresence();
                    }
                    catch { }
                    SteamClient.Shutdown();
                }
            }
            catch { }
        }

        try
        {
            Environment.SetEnvironmentVariable("SteamAppId", null);
            Environment.SetEnvironmentVariable("SteamGameId", null);
            Environment.SetEnvironmentVariable("SteamOverlayGameId", null);

            string appIdFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt");
            if (File.Exists(appIdFile))
            {
                File.Delete(appIdFile);
            }
        }
        catch { }

        IsSteamInitialized = false;
        logCallback?.Invoke("[STEAM] Unhooked from Steam API — Steam status returned to Online (not playing any game).");
    }

    private void ConfigureSteamNetworkingDebugOutput()
    {
        try
        {
            if (!_debugOutputSubscribed)
            {
                SteamNetworkingUtils.OnDebugOutput += HandleSteamNetworkingDebugOutput;
                _debugOutputSubscribed = true;
            }
            SteamNetworkingUtils.DebugLevel = NetDebugOutput.Msg;
        }
        catch { }
    }

    internal static bool IsRelevantSteamNetworkingDebugLine(NetDebugOutput type, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        if (type >= NetDebugOutput.Bug && type <= NetDebugOutput.Warning) return true; // Bug, Error, Important, Warning
        if (type == NetDebugOutput.Msg)
        {
            // Suppress noisy per-POP ECN/L4S probe chatter
            if (message.Contains("L4S detection", StringComparison.OrdinalIgnoreCase)
                || message.Contains("We will send ECN=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return message.Contains("ICE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("P2P", StringComparison.OrdinalIgnoreCase)
                || message.Contains("candidate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cert", StringComparison.OrdinalIgnoreCase)
                || message.Contains("STUN", StringComparison.OrdinalIgnoreCase)
                || message.Contains("steamwebrtc", StringComparison.OrdinalIgnoreCase)
                || message.Contains("SDR", StringComparison.OrdinalIgnoreCase)
                || message.Contains("4999", StringComparison.Ordinal)
                || message.Contains("3999", StringComparison.Ordinal)
                || message.Contains("AuthStatus", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    internal static bool IsLocalUserIceDisabledMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("ICE not enabled by local user options", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ICE failed 4999", StringComparison.OrdinalIgnoreCase);
    }

    private void HandleSteamNetworkingDebugOutput(NetDebugOutput type, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string clean = message.Trim().Replace("\r", " ").Replace("\n", " | ");
        if (!IsRelevantSteamNetworkingDebugLine(type, clean)) return;

        EmitServiceLog($"[STEAM-NET:{type}] {clean}");

        if (IsLocalUserIceDisabledMessage(clean))
        {
            long now = Environment.TickCount64;
            if (now - _lastLocalIcePrivacyHintTick >= 10_000)
            {
                _lastLocalIcePrivacyHintTick = now;
                EmitServiceLog("[WARN] [STEAM-ICE] Steam's local Networking privacy option is blocking Direct UDP ICE! Check BOTH Host & Client: Steam -> Settings -> In Game -> Steam Networking -> set to 'Friends only' or 'Always' (NOT 'Never').");
            }
        }
    }

    private static int _appFirewallRuleChecked;

    private static void EnsureAppP2PFirewallRule()
    {
        if (Interlocked.Exchange(ref _appFirewallRuleChecked, 1) == 1)
            return;

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) ||
                !File.Exists(exePath) ||
                Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            static int RunNetsh(string args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return -1;
                proc.WaitForExit(2000);
                return proc.HasExited ? proc.ExitCode : -1;
            }

            if (RunNetsh("advfirewall firewall show rule name=\"BG3ModSync Steam P2P (UDP)\"") != 0)
            {
                RunNetsh($"advfirewall firewall add rule name=\"BG3ModSync Steam P2P (UDP)\" dir=in action=allow protocol=UDP program=\"{exePath}\" enable=yes profile=any");
            }

            if (RunNetsh("advfirewall firewall show rule name=\"BG3ModSync Steam P2P (TCP)\"") != 0)
            {
                RunNetsh($"advfirewall firewall add rule name=\"BG3ModSync Steam P2P (TCP)\" dir=in action=allow protocol=TCP program=\"{exePath}\" enable=yes profile=any");
            }
        }
        catch
        {
            // Non-fatal when running without Administrator privileges
        }
    }

    private void InvokeDedupedLog(Action<string>? directCallback, string message)
    {
        try { directCallback?.Invoke(message); } catch { }
        var onLog = OnLog;
        if (onLog == null) return;
        foreach (var del in onLog.GetInvocationList())
        {
            if (directCallback != null && Equals(del, directCallback))
                continue;
            try { ((Action<string>)del).Invoke(message); } catch { }
        }
    }

    private void EmitServiceLog(string message)
    {
        InvokeDedupedLog(_activeLogCallback, message);
    }

    private static void ConfigureDirectHolePunchAndHighThroughput(Action<string>? logCallback)
    {
        try
        {
            EnsureReflectionInitialized();
            var setConfigInt = _utilsSetConfigIntMethod;
            var netConfigEnumType = _netConfigEnumType;
            if (setConfigInt != null && netConfigEnumType != null)
            {
                bool TrySetInt(int id, int value)
                {
                    try
                    {
                        object? res = setConfigInt.Invoke(null, new object[] { Enum.ToObject(netConfigEnumType, id), value });
                        return res is bool b && b;
                    }
                    catch
                    {
                        return false;
                    }
                }

                // Real Valve steamnetworkingtypes.h ESteamNetworkingConfigValue IDs:
                // Do NOT override 103 (P2P_STUN_ServerList) — let Valve auto-select nearest STUN servers
                // and avoid Facepunch 2.3.3's SetConfigString missing NUL-terminator bug.
                // 104 = k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable (Int32: 6 = Private(2) | Public(4), avoids unconfigured TURN)
                // 105 = k_ESteamNetworkingConfig_P2P_Transport_ICE_Penalty (Int32: 0 = prefer ICE)
                // 106 = k_ESteamNetworkingConfig_P2P_Transport_SDR_Penalty (Int32: 1000 = heavily penalize Valve SDR Relay)
                // 17  = k_ESteamNetworkingConfig_LogLevel_P2PRendezvous (5 = Msg, logs 'ICE failed <code_reason>')
                // 39  = k_ESteamNetworkingConfig_DualWifi_Enable (0 = off, avoids APIPA 169.254.x.x secondary socket 0x2743 spam)
                // 9 = SendBufferSize (64 MB), 10 = SendRateMin (256 KB/s for WAN/CGNAT congestion backoff),
                // 11 = SendRateMax (64 MB/s), 12 = NagleTime (0),
                // 47 = RecvBufferSize (4 MB), 48 = RecvBufferMessages (4096)
                bool okIce = TrySetInt(104, 6);
                bool okIcePen = TrySetInt(105, 0);
                bool okSdrPen = TrySetInt(106, 1000);
                bool okRendezvousLog = TrySetInt(17, 5);
                bool okDualWifiOff = TrySetInt(39, 0);

                bool okSendBuf = TrySetInt(9, 64 * 1024 * 1024);
                bool okRateMin = TrySetInt(10, 256 * 1024);
                bool okRateMax = TrySetInt(11, 64 * 1024 * 1024);
                bool okNagle = TrySetInt(12, 0);
                bool okRecvBuf = TrySetInt(47, 4 * 1024 * 1024);
                bool okRecvMsgs = TrySetInt(48, 4096);

                logCallback?.Invoke(
                    $"[STEAM-ICE] Configured Direct UDP ICE & Transport Buffers (ICE=Private|Public(6):{okIce}, ICE Penalty=0:{okIcePen} vs SDR Penalty=1000:{okSdrPen}, DualWifi=Off:{okDualWifiOff}, RendezvousLog=Msg:{okRendezvousLog}, Valve Auto-STUN, SendBuf=64MB:{okSendBuf}, SendRate=256KB/s..64MB/s:{okRateMin}/{okRateMax}, RecvBuf=4MB/4096:{okRecvBuf}/{okRecvMsgs}, Nagle=0:{okNagle}).");
            }
            else
            {
                SteamNetworkingUtils.SendBufferSize = 64 * 1024 * 1024;
            }
        }
        catch
        {
            // Ignore if unsupported by runtime version
        }
    }

    private void StartCallbackPump()
    {
        _callbackLoopCts?.Cancel();
        try { _callbackLoopTask?.Wait(200); } catch { }
        _callbackLoopCts = new CancellationTokenSource();
        var token = _callbackLoopCts.Token;

        _callbackLoopTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    lock (_steamLifecycleLock)
                    {
                        if (!token.IsCancellationRequested && SteamClient.IsValid)
                        {
                            SteamClient.RunCallbacks();
                            if (_hostSocketManager != null && !_hostSocketManager.IsWriteBackpressured)
                            {
                                _hostSocketManager.ReceiveBounded();
                            }
                            if (_clientConnectionManager != null && !_clientConnectionManager.IsWriteBackpressured)
                            {
                                _clientConnectionManager.ReceiveBounded();
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore transient callback errors
                }
                await Task.Delay(2, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, token);
    }

    private void HandleGameLobbyJoinRequested(Lobby lobby, SteamId friendId)
    {
        EmitServiceLog($"[STEAM] Joined via Steam Invite from Friend {friendId}. Joining Lobby {lobby.Id}...");
        _ = Task.Run(async () =>
        {
            try { await StartOffCallbackSteamCall(() => lobby.Join()); } catch { }
        });
    }

    private void HandleLobbyEntered(Lobby lobby)
    {
        string hostIdStr = lobby.GetData("bg3modsync_host");
        if (ulong.TryParse(hostIdStr, out ulong hostSteamId) && hostSteamId != 0 && hostSteamId != LocalSteamId)
        {
            string roomCode = EncodeRoomCode(lobby.Id.Value);
            EmitServiceLog($"[STEAM] Entered Lobby {lobby.Id}. Host SteamID: {hostSteamId} (Room Code: {roomCode})");
            OnLobbyHostJoinRequested?.Invoke(lobby.Id.Value);
        }
    }

    public async Task<bool> StartHostRelayAsync(
        int localNginxPort,
        IReadOnlyList<NetworkEndpointInfo> discoveredEndpoints,
        Action<string> logCallback)
    {
        if (!TryInitialize(logCallback))
        {
            logCallback("[STEAM] Hosting in Manual IP mode only (Steam client not active).");
            return false;
        }

        TryInitSteamAuthentication();
        StopHostRelay();

        _localHostEndpoints = new HostDirectEndpointsMetadata
        {
            HostSteamId = LocalSteamId,
            HostName = LocalPersonaName,
            NginxPort = localNginxPort,
            WanIp = discoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.WAN)?.IpAddress ?? "",
            LanIps = discoveredEndpoints.Where(e => e.Category == NetworkCategory.LAN).Select(e => e.IpAddress).Distinct().ToList(),
            VpnIps = discoveredEndpoints.Where(e => e.Category == NetworkCategory.VPN).Select(e => e.IpAddress).Distinct().ToList()
        };

        try
        {
            string endpointsJson = JsonSerializer.Serialize(_localHostEndpoints);

            HostP2PSocketManager hostMgr;
            lock (_steamLifecycleLock)
            {
                hostMgr = SteamNetworkingSockets.CreateRelaySocket<HostP2PSocketManager>(0);
                hostMgr.Configure(
                    localNginxPort,
                    NearestRelaySummary,
                    endpointsJson,
                    _localHostEndpoints,
                    msg => InvokeDedupedLog(logCallback, msg),
                    (route, isRelay, relayPing) => OnRouteStatusChanged?.Invoke(route, isRelay, relayPing),
                    telemetry => OnClientTelemetryReceived?.Invoke(telemetry),
                    clientKey => OnClientDisconnected?.Invoke(clientKey));
                _hostSocketManager = hostMgr;
            }

            var lobbyResult = await StartOffCallbackSteamCall(() => SteamMatchmaking.CreateLobbyAsync(16));
            if (lobbyResult.HasValue)
            {
                var lobby = lobbyResult.Value;
                string sessionRoomCode = EncodeRoomCode(lobby.Id.Value);
                lock (_steamLifecycleLock)
                {
                    ActiveLobby = lobby;
                    lobby.SetPublic();
                    lobby.SetJoinable(true);
                    lobby.SetData("bg3modsync_app", "BG3ModSync");
                    lobby.SetData("bg3modsync_host", LocalSteamId.ToString());
                    lobby.SetData("bg3modsync_room", sessionRoomCode);
                    lobby.SetData("bg3modsync_endpoints", endpointsJson);
                    lobby.SetData("name", $"BG3ModSync - {LocalPersonaName}");
                    try
                    {
                        SteamFriends.SetRichPresence("connect", $"+connect_lobby {lobby.Id}");
                        SteamFriends.SetRichPresence("steam_player_group", lobby.Id.ToString());
                        SteamFriends.SetRichPresence("bg3modsync_lobby", lobby.Id.ToString());
                        SteamFriends.SetRichPresence("bg3modsync_room", sessionRoomCode);
                    }
                    catch { }
                }
                logCallback($"[STEAM-P2P] Steam Lobby Created ({GetAppIdDisplayName(ActiveAppId)}, Lobby ID: {lobby.Id}) | Session Room Code: {sessionRoomCode} | Host SteamID: {LocalSteamId}");
                logCallback("[STEAM-P2P] Published direct WAN/LAN/VPN endpoints to Lobby for zero-relay Direct HTTP Handoff.");
            }
            else
            {
                logCallback($"[STEAM-P2P] Hole-Punch Socket listening on Virtual Port 0 | Room Code: {LocalRoomCode} | SteamID: {LocalSteamId}");
            }

            return true;
        }
        catch (Exception ex)
        {
            logCallback($"[STEAM-P2P] Failed to create Steam P2P listener: {ex.Message}");
            return false;
        }
    }

    public HostDirectEndpointsMetadata LocalHostEndpoints => _localHostEndpoints;

    public void UpdateHostTargetNginxPort(int newNginxPort, IReadOnlyList<NetworkEndpointInfo>? discoveredEndpoints = null)
    {
        _localHostEndpoints.HostSteamId = LocalSteamId;
        _localHostEndpoints.HostName = LocalPersonaName;
        _localHostEndpoints.NginxPort = newNginxPort;
        if (discoveredEndpoints != null)
        {
            _localHostEndpoints.WanIp = discoveredEndpoints.FirstOrDefault(e => e.Category == NetworkCategory.WAN)?.IpAddress ?? "";
            _localHostEndpoints.LanIps = discoveredEndpoints.Where(e => e.Category == NetworkCategory.LAN).Select(e => e.IpAddress).Distinct().ToList();
            _localHostEndpoints.VpnIps = discoveredEndpoints.Where(e => e.Category == NetworkCategory.VPN).Select(e => e.IpAddress).Distinct().ToList();
        }
        string endpointsJson = JsonSerializer.Serialize(_localHostEndpoints);
        _hostSocketManager?.UpdateNginxPort(newNginxPort, endpointsJson, _localHostEndpoints);
        lock (_steamLifecycleLock)
        {
            if (ActiveLobby.HasValue && SteamClient.IsValid)
            {
                ActiveLobby.Value.SetData("bg3modsync_endpoints", endpointsJson);
            }
        }
    }

    public void StopHostRelay()
    {
        try
        {
            _hostSocketManager?.CancelPendingOperations();
            lock (_steamLifecycleLock)
            {
                if (ActiveLobby.HasValue)
                {
                    try { ActiveLobby.Value.Leave(); } catch { }
                    ActiveLobby = null;
                }
                if (SteamClient.IsValid)
                {
                    try { SteamFriends.ClearRichPresence(); } catch { }
                }
                var toClose = _hostSocketManager;
                _hostSocketManager = null;
                toClose?.CloseAll();
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Connects to the Host via Steam Room Code or SteamID64 using Direct-Only Handoff &amp; NAT Hole-Punching:
    /// 1. Resolves the Lobby / Host SteamID and retrieves the Host's Direct HTTP endpoints (VPN, LAN, WAN UPnP).
    /// 2. Establishes P2P hole-punch signaling with Host, exchanges IP/Port endpoints, displays punched Host IP:Port on the green line, and starts continuous 1s ping.
    /// 3. Uses Direct HTTP if directly reachable (closing temporary P2P signaling socket immediately) or Direct UDP ICE Hole-Punch tunnel (blocking Valve SDR Relay bulk transfers).
    /// </summary>
    public async Task<string?> StartClientRelayTunnelAsync(
        ulong targetSteamOrLobbyId,
        Action<string> logCallback,
        CancellationToken cancellationToken,
        IReadOnlyList<NetworkEndpointInfo>? clientDiscoveredEndpoints = null,
        int defaultNginxPort = 27000)
    {
        if (!TryInitialize(logCallback))
        {
            logCallback("[ERROR] Cannot connect via SteamID/Room Code because Steam is not running.");
            return null;
        }

        StopClientRelayTunnel();

        var clientLocalMeta = new HostDirectEndpointsMetadata
        {
            HostSteamId = LocalSteamId,
            HostName = LocalPersonaName,
            NginxPort = defaultNginxPort > 0 ? defaultNginxPort : 27000,
            WanIp = clientDiscoveredEndpoints?.FirstOrDefault(e => e.Category == NetworkCategory.WAN)?.IpAddress ?? _localHostEndpoints.WanIp,
            LanIps = clientDiscoveredEndpoints?.Where(e => e.Category == NetworkCategory.LAN).Select(e => e.IpAddress).Distinct().ToList() ?? _localHostEndpoints.LanIps,
            VpnIps = clientDiscoveredEndpoints?.Where(e => e.Category == NetworkCategory.VPN).Select(e => e.IpAddress).Distinct().ToList() ?? _localHostEndpoints.VpnIps
        };

        var (resolvedHostId, lobbyEndpoints) = await ResolveToHostSteamIdAndEndpointsAsync(targetSteamOrLobbyId, logCallback);
        if (resolvedHostId == 0)
        {
            StopClientRelayTunnel();
            return null;
        }
        LastHandshakedHostEndpoints = lobbyEndpoints ?? new HostDirectEndpointsMetadata { HostSteamId = resolvedHostId, NginxPort = defaultNginxPort };

        logCallback($"[STEAM-P2P] Initiating Steam NAT Hole-Punching handshake with Host SteamID {resolvedHostId}...");

        try
        {
            RefreshNearestRelaySummary();

            ClientP2PConnectionManager clientMgr;
            lock (_steamLifecycleLock)
            {
                clientMgr = SteamNetworkingSockets.ConnectRelay<ClientP2PConnectionManager>(resolvedHostId, 0);
                clientMgr.Configure(NearestRelaySummary, clientLocalMeta, msg => InvokeDedupedLog(logCallback, msg), (route, isRelay, relayPing) =>
                {
                    // Do not flash RelayWarningBanner during initial signaling handshake;
                    // final route policy is enforced after Stage 1 Direct HTTP Handoff & Stage 2 ICE 5s upgrade window.
                    if (isRelay)
                    {
                        LastConnectionObservedRelay = true;
                    }
                    else
                    {
                        OnRouteStatusChanged?.Invoke(route, false, relayPing);
                    }
                });
                _clientConnectionManager = clientMgr;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!clientMgr.IsConnected && !clientMgr.IsFailed && sw.Elapsed.TotalSeconds < 15)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }

            if (!clientMgr.IsConnected)
            {
                // Fallback if P2P handshake failed/timed out but Lobby endpoints are directly reachable via HTTP
                if (lobbyEndpoints != null)
                {
                    var directFallback = await TryProbeDirectHttpEndpointsAsync(lobbyEndpoints, logCallback, cancellationToken);
                    if (directFallback.DirectUrl != null)
                    {
                        clientMgr.CancelPendingOperations();
                        lock (_steamLifecycleLock)
                        {
                            if (ReferenceEquals(_clientConnectionManager, clientMgr))
                                _clientConnectionManager = null;
                            clientMgr.CloseAll();
                        }
                        PunchedHostEndpoint = directFallback.PunchedIpPort;
                        StartClientContinuousPingLoop(PunchedHostEndpoint, directFallback.DirectUrl, lobbyEndpoints, clientLocalMeta);
                        return directFallback.DirectUrl;
                    }
                }

                if (clientMgr.IsFailed && !string.IsNullOrEmpty(clientMgr.LastDisconnectSummary))
                {
                    logCallback($"[ERROR] Steam P2P handshake with Host failed after {sw.Elapsed.TotalSeconds:F1}s: {clientMgr.LastDisconnectSummary}");
                }
                else
                {
                    logCallback($"[ERROR] Timed out waiting for Steam P2P handshake with Host after {sw.Elapsed.TotalSeconds:F1}s (State={clientMgr.ConnectionInfo.State}).");
                }
                StopClientRelayTunnel();
                return null;
            }

            // Always exchange endpoints with Host so Host learns Client's IP:Port and Client gets Host's live IP:Port
            var handshakeEndpoints = await clientMgr.RequestHostEndpointsAsync(clientLocalMeta, TimeSpan.FromSeconds(2.5), cancellationToken);
            var effectiveHostEndpoints = handshakeEndpoints ?? lobbyEndpoints ?? new HostDirectEndpointsMetadata { HostSteamId = resolvedHostId, NginxPort = defaultNginxPort };
            if (effectiveHostEndpoints.HostSteamId == 0) effectiveHostEndpoints.HostSteamId = resolvedHostId;
            if (string.IsNullOrWhiteSpace(effectiveHostEndpoints.HostName) && resolvedHostId != 0)
            {
                try { effectiveHostEndpoints.HostName = new Friend(resolvedHostId).Name; } catch { }
            }
            LastHandshakedHostEndpoints = effectiveHostEndpoints;
            clientMgr.SetRemoteHostEndpoints(effectiveHostEndpoints);

            // Stage 1: Check if Host's direct Nginx HTTP endpoint is reachable for zero-copy full-speed direct TCP download
            if (effectiveHostEndpoints != null)
            {
                var (directUrl, punchedIpPort) = await TryProbeDirectHttpEndpointsAsync(effectiveHostEndpoints, logCallback, cancellationToken);
                if (!string.IsNullOrEmpty(directUrl))
                {
                    // Check if the signaling socket needed Valve SDR relay (useful CGNAT telemetry hint),
                    // then close the temporary P2P signaling socket immediately so zero traffic/warnings linger on Steam Relay!
                    try
                    {
                        var preCloseRoute = InspectSteamConnectionRoute(
                            clientMgr.Connection,
                            clientMgr.ConnectionInfo,
                            NearestRelaySummary,
                            effectiveHostEndpoints,
                            clientLocalMeta,
                            clientMgr.LastRttPingMs,
                            LocalSteamId);
                        if (preCloseRoute.IsRelayedThroughValve)
                        {
                            LastConnectionObservedRelay = true;
                        }
                    }
                    catch { }

                    clientMgr.CancelPendingOperations();
                    lock (_steamLifecycleLock)
                    {
                        if (ReferenceEquals(_clientConnectionManager, clientMgr))
                            _clientConnectionManager = null;
                        clientMgr.CloseAll();
                    }

                    PunchedHostEndpoint = punchedIpPort;
                    logCallback($"[STEAM-P2P] Stage 1 Direct HTTP Handoff active -> {PunchedHostEndpoint} (Closed temporary signaling socket; continuous 1s ping active).");
                    StartClientContinuousPingLoop(PunchedHostEndpoint, directUrl, effectiveHostEndpoints, clientLocalMeta);
                    return directUrl;
                }
            }

            // Stage 2: Check if the Steam P2P connection upgrades to Direct UDP ICE Hole-Punched vs Valve SDR Relay.
            // Give ICE STUN candidate exchange & UDP hole-punching up to 8.0s (32 x 250ms) to complete!
            logCallback("[STEAM-ICE] Waiting up to 8.0s for Direct UDP ICE NAT Hole-Punching to complete...");
            for (int i = 0; i < 32; i++)
            {
                if (clientMgr.IsFailed || !clientMgr.IsConnected)
                {
                    string reason = !string.IsNullOrEmpty(clientMgr.LastDisconnectSummary)
                        ? clientMgr.LastDisconnectSummary
                        : $"State={clientMgr.ConnectionInfo.State}";
                    logCallback($"[ERROR] Steam P2P connection dropped during Direct UDP ICE negotiation: {reason}");
                    StopClientRelayTunnel();
                    return null;
                }

                var (routeLabel, isRelayed, relayPing) = InspectSteamConnectionRoute(
                    clientMgr.Connection,
                    clientMgr.ConnectionInfo,
                    NearestRelaySummary,
                    effectiveHostEndpoints,
                    clientLocalMeta,
                    clientMgr.LastRttPingMs,
                    LocalSteamId);

                if (!isRelayed)
                {
                    LastConnectionObservedRelay = false;
                    OnRouteStatusChanged?.Invoke(routeLabel, false, relayPing);
                    break;
                }
                await Task.Delay(250, cancellationToken);
            }

            if (clientMgr.IsFailed || !clientMgr.IsConnected)
            {
                string reason = !string.IsNullOrEmpty(clientMgr.LastDisconnectSummary)
                    ? clientMgr.LastDisconnectSummary
                    : $"State={clientMgr.ConnectionInfo.State}";
                logCallback($"[ERROR] Steam P2P connection dropped before Direct UDP ICE tunnel started: {reason}");
                StopClientRelayTunnel();
                return null;
            }

            var finalCheck = InspectSteamConnectionRoute(
                clientMgr.Connection,
                clientMgr.ConnectionInfo,
                NearestRelaySummary,
                effectiveHostEndpoints,
                clientLocalMeta,
                clientMgr.LastRttPingMs,
                LocalSteamId);

            if (finalCheck.IsRelayedThroughValve)
            {
                LastConnectionObservedRelay = true;
                // Enforce Direct-Only Policy: Do NOT stream gigabytes of .pak files through Valve's SDR Relay!
                OnRouteStatusChanged?.Invoke($"Blocked SDR Relay [{finalCheck.RelayWithPing}] (Direct-Only Policy)", true, finalCheck.RelayWithPing);
                logCallback($"[WARN] Direct UDP ICE Hole-Punch & Direct HTTP Handoff were blocked by strict symmetric NAT/CGNAT (Connection remained on Valve SDR Relay: {finalCheck.RelayWithPing}).");
                logCallback("[WARN] To avoid abusing Steam's SDR Relay network with multi-GB .pak files, bulk relay transfer is disabled. Enable UPnP on Host router, port-forward, or use VPN mode (Tailscale/ZeroTier/Radmin).");
                StopClientRelayTunnel();
                return null;
            }

            PunchedHostEndpoint = ResolvePunchedIpAndPort(
                clientMgr.Connection,
                clientMgr.ConnectionInfo,
                effectiveHostEndpoints,
                clientLocalMeta);

            // Direct UDP Hole-Punch succeeded! Start local loopback TCP listener bridged to the Direct P2P UDP socket
            _clientLocalListener = new TcpListener(IPAddress.Loopback, 0);
            _clientLocalListener.Start();
            ClientLocalTunnelPort = ((IPEndPoint)_clientLocalListener.LocalEndpoint).Port;

            _clientTunnelCts = new CancellationTokenSource();
            var tunnelToken = _clientTunnelCts.Token;

            _ = Task.Run(async () =>
            {
                uint nextStreamId = 1;
                while (!tunnelToken.IsCancellationRequested)
                {
                    try
                    {
                        var tcpClient = await _clientLocalListener.AcceptTcpClientAsync(tunnelToken);
                        tcpClient.NoDelay = true;
                        uint streamId = Interlocked.Increment(ref nextStreamId);
                        clientMgr.AttachLocalSocket(streamId, tcpClient);
                    }
                    catch
                    {
                        if (tunnelToken.IsCancellationRequested) break;
                    }
                }
            }, tunnelToken);

            string baseUrl = $"http://127.0.0.1:{ClientLocalTunnelPort}";
            logCallback($"[STEAM-P2P] Direct P2P Hole-Punch Tunnel Established to Host {PunchedHostEndpoint}! Local Endpoint {baseUrl} -> Direct Peer-to-Peer -> Host Nginx (Continuous 1s ping active).");

            StartClientContinuousPingLoop(PunchedHostEndpoint, baseUrl, effectiveHostEndpoints, clientLocalMeta);
            return baseUrl;
        }
        catch (Exception ex)
        {
            logCallback($"[ERROR] Steam P2P Hole-Punch connection error: {ex.Message}");
            StopClientRelayTunnel();
            return null;
        }
    }

    private void StartClientContinuousPingLoop(
        string initialPunchedIpPort,
        string? httpHealthBaseUrl,
        HostDirectEndpointsMetadata? hostEndpoints,
        HostDirectEndpointsMetadata? clientLocalMeta)
    {
        _clientPingLoopCts?.Cancel();
        _clientPingLoopCts = new CancellationTokenSource();
        var token = _clientPingLoopCts.Token;

        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            int consecutiveRelayTicks = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int pingMs = -1;
                    string punchedIpPort = initialPunchedIpPort;

                    var activeMgr = _clientConnectionManager;
                    if (activeMgr != null && activeMgr.IsConnected && !activeMgr.IsFailed)
                    {
                        activeMgr.SendPing();
                        await Task.Delay(60, token);

                        if (activeMgr.TrySnapshotLiveRouteAndPing(
                            hostEndpoints,
                            clientLocalMeta,
                            NearestRelaySummary,
                            LocalSteamId,
                            out string snapEndpoint,
                            out int snapPing,
                            out bool isRelayedMidSession,
                            out string relayWithPing))
                        {
                            if (!string.IsNullOrWhiteSpace(snapEndpoint))
                            {
                                punchedIpPort = snapEndpoint;
                                PunchedHostEndpoint = snapEndpoint;
                            }
                            if (snapPing >= 0)
                            {
                                pingMs = snapPing;
                            }

                            if (isRelayedMidSession)
                            {
                                consecutiveRelayTicks++;
                                if (consecutiveRelayTicks >= 3)
                                {
                                    LastConnectionObservedRelay = true;
                                    OnRouteStatusChanged?.Invoke($"Blocked SDR Relay [{relayWithPing}] (Direct-Only Policy)", true, relayWithPing);
                                    EmitServiceLog($"[WARN] Direct UDP ICE path dropped mid-session and fell back to Valve SDR Relay ({relayWithPing}). Closing tunnel per Direct-Only Policy.");
                                    StopClientRelayTunnel();
                                    break;
                                }
                            }
                            else
                            {
                                consecutiveRelayTicks = 0;
                            }
                        }
                    }

                    // Also measure via ICMP or HTTP /health if P2P ping hasn't updated yet or using Direct HTTP handoff
                    if (pingMs < 0)
                    {
                        pingMs = await MeasureEndpointPingMsAsync(punchedIpPort, httpHealthBaseUrl, http, token);
                    }

                    if (pingMs < 0) pingMs = 1;
                    LastClientRttPingMs = pingMs;

                    RefreshNearestRelaySummary();
                    OnRouteStatusChanged?.Invoke(
                        $"P2P Hole-Punch -> {punchedIpPort} • Ping: {pingMs}ms",
                        false,
                        NearestRelaySummary);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient ping error
                }

                await Task.Delay(940, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, token);
    }

    public static async Task<int> MeasureEndpointPingMsAsync(
        string hostIpAndPort,
        string? httpBaseUrl,
        HttpClient? sharedHttp = null,
        CancellationToken cancellationToken = default,
        bool allowTcpFallback = true)
    {
        string cleanIp = hostIpAndPort.Trim();
        int port = 27000;
        int colonIdx = cleanIp.LastIndexOf(':');
        if (colonIdx > 0)
        {
            int.TryParse(cleanIp.Substring(colonIdx + 1), out port);
            cleanIp = cleanIp.Substring(0, colonIdx);
        }

        // 1. Try fast ICMP Ping first
        if (IPAddress.TryParse(cleanIp, out var parsedIp))
        {
            try
            {
                using var pinger = new System.Net.NetworkInformation.Ping();
                var reply = await pinger.SendPingAsync(parsedIp, 750);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                {
                    return (int)Math.Max(0, reply.RoundtripTime);
                }
            }
            catch { }
        }

        // 2. Fallback to TCP connect / HTTP /health RTT measurement
        if (!string.IsNullOrWhiteSpace(httpBaseUrl))
        {
            try
            {
                using var ownedClient = sharedHttp == null ? NetworkDiscoveryService.CreateOptimizedHttpClient(TimeSpan.FromMilliseconds(850), TimeSpan.FromMilliseconds(750)) : null;
                var client = sharedHttp ?? ownedClient!;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await client.GetAsync($"{httpBaseUrl.TrimEnd('/')}/health", cancellationToken);
                sw.Stop();
                if (resp.IsSuccessStatusCode)
                {
                    return (int)Math.Max(1, Math.Round(sw.Elapsed.TotalMilliseconds));
                }
            }
            catch { }
        }

        if (allowTcpFallback && IPAddress.TryParse(cleanIp, out var tcpIp) && port > 0 && port <= 65535)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(tcpIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                NetworkDiscoveryService.TryBindSocketToPhysicalLanIfApplicable(socket, tcpIp);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(750);
                await socket.ConnectAsync(new IPEndPoint(tcpIp, port), cts.Token);
                sw.Stop();
                return (int)Math.Max(1, Math.Round(sw.Elapsed.TotalMilliseconds));
            }
            catch { }
        }

        return -1;
    }

    private async Task<(string? DirectUrl, string PunchedIpPort)> TryProbeDirectHttpEndpointsAsync(
        HostDirectEndpointsMetadata endpoints,
        Action<string> logCallback,
        CancellationToken cancellationToken)
    {
        int port = endpoints.NginxPort > 0 ? endpoints.NginxPort : 27000;
        var candidates = new List<(string Category, string Ip)>();

        if (endpoints.HostSteamId == LocalSteamId)
        {
            // Note: For same-PC local testing, we also test the P2P Direct Hole-Punch tunnel below so both paths are verified!
            logCallback($"[STEAM-HANDOFF] Host endpoints received from Lobby (Port {port}, WAN: {endpoints.WanIp}, LAN: {string.Join(",", endpoints.LanIps)}, VPN: {string.Join(",", endpoints.VpnIps)}).");
            return (null, "");
        }

        foreach (var vpnIp in endpoints.VpnIps.Where(ip => !string.IsNullOrWhiteSpace(ip)))
            candidates.Add(("VPN", vpnIp));
        foreach (var lanIp in endpoints.LanIps.Where(ip => !string.IsNullOrWhiteSpace(ip)))
            candidates.Add(("LAN", lanIp));
        if (!string.IsNullOrWhiteSpace(endpoints.WanIp))
            candidates.Add(("WAN (UPnP)", endpoints.WanIp));

        if (candidates.Count == 0) return (null, "");

        logCallback($"[STEAM-HANDOFF] Probing Host's direct Nginx HTTP endpoints ({string.Join(", ", candidates.Select(c => $"{c.Category}:{c.Ip}:{port}"))})...");

        using var http = NetworkDiscoveryService.CreateOptimizedHttpClient(TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(1.8));
        foreach (var (category, ip) in candidates)
        {
            try
            {
                string testUrl = $"http://{ip}:{port}/health";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var httpResp = await http.GetAsync(testUrl, cancellationToken);
                string resp = await httpResp.Content.ReadAsStringAsync(cancellationToken);
                sw.Stop();
                if (httpResp.IsSuccessStatusCode && resp.Contains("OK", StringComparison.OrdinalIgnoreCase))
                {
                    if (httpResp.Headers.TryGetValues("X-Client-Endpoint", out var epValues))
                    {
                        string observed = epValues.FirstOrDefault()?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(observed) && !observed.StartsWith("127.0.0.1", StringComparison.Ordinal))
                        {
                            ObservedClientSelfIpPort = observed;
                        }
                    }

                    string baseUrl = $"http://{ip}:{port}";
                    string ipPort = $"{ip}:{port}";
                    int initPing = (int)Math.Max(1, Math.Round(sw.Elapsed.TotalMilliseconds));
                    string routeDesc = $"P2P Hole-Punch -> {ipPort} • Ping: {initPing}ms";
                    OnRouteStatusChanged?.Invoke(routeDesc, false, "");
                    logCallback($"[STEAM-HANDOFF] Direct HTTP Handoff Succeeded via {category} ({baseUrl}, Ping: {initPing}ms)! 0 bytes of mod data will touch Steam.");
                    return (baseUrl, ipPort);
                }
            }
            catch
            {
                // Try next direct candidate
            }
        }

        logCallback("[STEAM-HANDOFF] Direct TCP endpoints not directly reachable; proceeding with UDP NAT Hole-Punching...");
        return (null, "");
    }

    public void StopClientRelayTunnel()
    {
        try
        {
            _clientPingLoopCts?.Cancel();
            _clientPingLoopCts = null;
            _clientTunnelCts?.Cancel();
            _clientTunnelCts = null;
            _clientLocalListener?.Stop();
            _clientLocalListener = null;
            _clientConnectionManager?.CancelPendingOperations();
            lock (_steamLifecycleLock)
            {
                var toClose = _clientConnectionManager;
                _clientConnectionManager = null;
                toClose?.CloseAll();
            }
            PunchedHostEndpoint = "";
            ObservedClientSelfIpPort = "";
            LastConnectionObservedRelay = false;
        }
        catch
        {
            // Ignore
        }
    }

    private static System.Reflection.PropertyInfo? _sfInternalProp;
    private static System.Reflection.MethodInfo? _reqFriendRpMethod;

    private static void TryRequestFriendRichPresence(SteamId friendId)
    {
        try
        {
            if (_sfInternalProp == null)
            {
                var sfType = typeof(SteamFriends);
                _sfInternalProp = sfType.GetProperty("Internal", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var isfType = sfType.Assembly.GetType("Steamworks.ISteamFriends");
                _reqFriendRpMethod = isfType?.GetMethod("RequestFriendRichPresence", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            var internalObj = _sfInternalProp?.GetValue(null);
            if (internalObj != null && _reqFriendRpMethod != null)
            {
                _reqFriendRpMethod.Invoke(internalObj, new object[] { friendId });
            }
        }
        catch { }
    }

    private static ulong ParseLobbyIdFromRichPresence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        foreach (var tok in raw.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ulong.TryParse(tok, out ulong v) && ((v >> 52) & 0xF) == 8)
                return v;
        }
        return 0;
    }

    /// <summary>
    /// Queries Valve's Steam Matchmaking Lobby List directly to find active public BG3ModSync host lobbies
    /// (mapping HostSteamId -> LobbyId). Works reliably even when FriendGameInfo.Lobby is null or Rich Presence is delayed.
    /// </summary>
    public async Task<Dictionary<ulong, ulong>> QueryActiveModSyncLobbiesAsync(ulong? specificHostSteamId = null, int timeoutMs = 1500)
    {
        var map = new Dictionary<ulong, ulong>();
        if (!IsSteamInitialized || !SteamClient.IsValid) return map;

        try
        {
            var reqTask = StartOffCallbackSteamCall(() =>
            {
                var query = SteamMatchmaking.LobbyList
                    .FilterDistanceWorldwide()
                    .WithMaxResults(specificHostSteamId.HasValue ? 5 : 50);

                query = specificHostSteamId.HasValue
                    ? query.WithKeyValue("bg3modsync_host", specificHostSteamId.Value.ToString())
                    : query.WithKeyValue("bg3modsync_app", "BG3ModSync");

                return query.RequestAsync();
            });
            var completed = await Task.WhenAny(reqTask, Task.Delay(timeoutMs));
            if (completed == reqTask)
            {
                var lobbies = await reqTask;
                if (lobbies != null)
                {
                    lock (_steamLifecycleLock)
                    {
                        if (!IsSteamInitialized || !SteamClient.IsValid) return map;
                        foreach (var lobby in lobbies)
                        {
                            ulong lid = lobby.Id.Value;
                            if (lid == 0) continue;

                            ulong hostSid = 0;
                            string hostStr = lobby.GetData("bg3modsync_host");
                            if (!string.IsNullOrWhiteSpace(hostStr))
                            {
                                ulong.TryParse(hostStr.Trim(), out hostSid);
                            }
                            if (hostSid == 0)
                            {
                                hostSid = lobby.Owner.Id.Value;
                            }
                            if (hostSid != 0)
                            {
                                map[hostSid] = lid;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return map;
    }

    public List<SteamFriendInfo> GetOnlineFriends()
    {
        return GetOnlineFriendsCore(null);
    }

    public async Task<List<SteamFriendInfo>> GetOnlineFriendsAsync()
    {
        if (!TryInitialize()) return new List<SteamFriendInfo>();

        var activeLobbies = await QueryActiveModSyncLobbiesAsync(null, 1400);
        var list = GetOnlineFriendsCore(activeLobbies);

        // Backwards-compatibility fallback: if any friend active in the same AppID is running an older build
        // (setting bg3modsync_host without bg3modsync_app), look up their lobby directly by bg3modsync_host!
        int fallbackLookups = 0;
        foreach (var item in list)
        {
            if (item.LobbyId == 0 && fallbackLookups < 4)
            {
                fallbackLookups++;
                var found = await QueryActiveModSyncLobbiesAsync(item.SteamId, 900);
                if (found.TryGetValue(item.SteamId, out ulong liveLid) && liveLid != 0)
                {
                    item.LobbyId = liveLid;
                    item.StatusText = $"Hosting Lobby ({EncodeRoomCode(liveLid)})";
                }
            }
        }

        return list.OrderByDescending(f => f.LobbyId != 0).ThenBy(f => f.Name).ToList();
    }

    private List<SteamFriendInfo> GetOnlineFriendsCore(Dictionary<ulong, ulong>? activeLobbies)
    {
        var list = new List<SteamFriendInfo>();
        if (!TryInitialize()) return list;

        try
        {
            string appLabel = GetAppIdDisplayName(ActiveAppId);
            lock (_steamLifecycleLock)
            {
                if (!IsSteamInitialized || !SteamClient.IsValid) return list;
                foreach (var friend in SteamFriends.GetFriends())
                {
                    ulong fid = friend.Id.Value;
                    ulong friendLobbyId = 0;

                    // Priority 1: Authoritative Steam Matchmaking LobbyList match
                    if (activeLobbies != null && activeLobbies.TryGetValue(fid, out ulong matchedLid) && matchedLid != 0)
                    {
                        friendLobbyId = matchedLid;
                    }

                    // Priority 2: Friend.GameInfo.Lobby
                    if (friendLobbyId == 0 && friend.IsPlayingThisGame && friend.GameInfo.HasValue && friend.GameInfo.Value.Lobby.HasValue)
                    {
                        friendLobbyId = friend.GameInfo.Value.Lobby.Value.Id.Value;
                    }

                    // Priority 3: Request & read Rich Presence (bg3modsync_lobby or connect +connect_lobby)
                    if (friend.IsPlayingThisGame || friend.State != FriendState.Offline)
                    {
                        TryRequestFriendRichPresence(friend.Id);
                        if (friendLobbyId == 0)
                        {
                            try
                            {
                                friendLobbyId = ParseLobbyIdFromRichPresence(friend.GetRichPresence("bg3modsync_lobby"));
                                if (friendLobbyId == 0)
                                {
                                    friendLobbyId = ParseLobbyIdFromRichPresence(friend.GetRichPresence("connect"));
                                }
                            }
                            catch { }
                        }
                    }

                    // Show friends actively in the same AppID OR hosting a BG3ModSync lobby (even if Steam privacy/Away hides Playing status)
                    if (!friend.IsPlayingThisGame && friendLobbyId == 0) continue;

                    string status = friendLobbyId != 0
                        ? $"Hosting Lobby ({EncodeRoomCode(friendLobbyId)})"
                        : $"Active in {appLabel}";

                    list.Add(new SteamFriendInfo
                    {
                        SteamId = fid,
                        LobbyId = friendLobbyId,
                        Name = friend.Name,
                        StatusText = status
                    });
                }
            }
        }
        catch
        {
            // Ignore
        }

        return list.OrderByDescending(f => f.LobbyId != 0).ThenBy(f => f.Name).ToList();
    }

    public bool InviteFriend(ulong friendSteamId, Action<string>? logCallback = null)
    {
        if (!IsSteamInitialized) return false;
        try
        {
            if (ActiveLobby.HasValue)
            {
                bool sent = ActiveLobby.Value.InviteFriend(friendSteamId);
                logCallback?.Invoke(sent
                    ? $"[STEAM] Sent Spacewar Lobby Invite to SteamID {friendSteamId}."
                    : $"[WARN] Could not send Lobby Invite to {friendSteamId}.");
                return sent;
            }
            else
            {
                SteamFriends.OpenUserOverlay(friendSteamId, "steamid");
                return true;
            }
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[WARN] Invite failed: {ex.Message}");
            return false;
        }
    }

    public void OpenOverlayInviteDialog(Action<string>? logCallback = null)
    {
        if (!IsSteamInitialized)
        {
            logCallback?.Invoke("[WARN] Steam is not initialized.");
            return;
        }
        try
        {
            if (ActiveLobby.HasValue)
            {
                SteamFriends.OpenGameInviteOverlay(ActiveLobby.Value.Id);
                logCallback?.Invoke("[STEAM] Opened Steam Overlay Invite Dialog.");
            }
            else
            {
                logCallback?.Invoke("[INFO] Start Hosting first to create a Steam Lobby for invites, or share your Room Code / SteamID.");
            }
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"[WARN] Could not open Steam Overlay: {ex.Message}");
        }
    }

    public static string EncodeRoomCode(ulong steamId)
    {
        if (steamId == 0) return "N/A";
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        ulong val = steamId;
        char[] buffer = new char[16];
        int pos = buffer.Length;
        while (val > 0)
        {
            buffer[--pos] = chars[(int)(val % 36)];
            val /= 36;
        }
        string base36 = new string(buffer, pos, buffer.Length - pos);
        return $"BG3-{base36}";
    }

    public static bool TryParseSteamTarget(string input, out ulong targetSteamOrLobbyId)
    {
        targetSteamOrLobbyId = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string trimmed = input.Trim();

        if (trimmed.StartsWith("BG3-", StringComparison.OrdinalIgnoreCase))
        {
            string code = trimmed.Substring(4).Replace("-", "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code)) return false;

            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            ulong result = 0;
            foreach (char c in code)
            {
                int idx = chars.IndexOf(c);
                if (idx < 0) return false;
                result = result * 36 + (ulong)idx;
            }
            targetSteamOrLobbyId = result;
            return targetSteamOrLobbyId >= 76561197960265728UL;
        }

        if (ulong.TryParse(trimmed, out ulong parsed) && parsed >= 76561197960265728UL)
        {
            targetSteamOrLobbyId = parsed;
            return true;
        }

        return false;
    }

    public async Task<(ulong HostSteamId, HostDirectEndpointsMetadata? Endpoints)> ResolveToHostSteamIdAndEndpointsAsync(
        ulong steamOrLobbyId,
        Action<string> logCallback)
    {
        if (ActiveLobby.HasValue && ActiveLobby.Value.Id.Value == steamOrLobbyId)
        {
            logCallback($"[STEAM-P2P] Same-PC Loopback detected for Session Room Code {EncodeRoomCode(steamOrLobbyId)} -> Host SteamID: {LocalSteamId}");
            return (LocalSteamId, _localHostEndpoints);
        }

        uint accountType = (uint)((steamOrLobbyId >> 52) & 0xF);
        if (accountType == 8) // Steam Lobby ID
        {
            logCallback($"[STEAM-P2P] Joining Steam Lobby {steamOrLobbyId} ({EncodeRoomCode(steamOrLobbyId)}) to resolve Host & Direct Endpoints...");
            var joinTask = StartOffCallbackSteamCall(() => SteamMatchmaking.JoinLobbyAsync(steamOrLobbyId));
            var completed = await Task.WhenAny(joinTask, Task.Delay(5000));
            if (completed == joinTask)
            {
                var joinedLobby = await joinTask;
                if (joinedLobby.HasValue)
                {
                    HostDirectEndpointsMetadata? meta = null;
                    string hostData;
                    ulong ownerSid;
                    lock (_steamLifecycleLock)
                    {
                        string endpointsJson = joinedLobby.Value.GetData("bg3modsync_endpoints");
                        if (!string.IsNullOrWhiteSpace(endpointsJson))
                        {
                            try { meta = JsonSerializer.Deserialize<HostDirectEndpointsMetadata>(endpointsJson); } catch { }
                        }
                        hostData = joinedLobby.Value.GetData("bg3modsync_host");
                        ownerSid = joinedLobby.Value.Owner.Id.Value;
                    }

                    if (ulong.TryParse(hostData, out ulong resolvedHost) && resolvedHost != 0)
                    {
                        logCallback($"[STEAM-P2P] Resolved Lobby {EncodeRoomCode(steamOrLobbyId)} -> Host SteamID: {resolvedHost}");
                        return (resolvedHost, meta);
                    }
                    if (ownerSid != 0)
                    {
                        return (ownerSid, meta);
                    }
                }
            }
            if (_hostSocketManager != null)
            {
                logCallback("[WARN] Lobby join timed out; falling back to LocalSteamId for local Host loopback test.");
                return (LocalSteamId, _localHostEndpoints);
            }
            logCallback($"[ERROR] Could not join or resolve Steam Lobby {steamOrLobbyId} ({EncodeRoomCode(steamOrLobbyId)}) — Host may have stopped hosting or is using a different Steam AppID.");
            return (0, null);
        }

        // If a User SteamID64 (accountType == 1) was provided instead of a Lobby ID,
        // discover their active BG3ModSync Lobby via Friend.GameInfo, Rich Presence, or Steam Matchmaking LobbyList!
        if (accountType == 1 && steamOrLobbyId != 0)
        {
            ulong discoveredLobbyId = 0;
            try
            {
                lock (_steamLifecycleLock)
                {
                    if (IsSteamInitialized && SteamClient.IsValid)
                    {
                        var f = new Friend(steamOrLobbyId);
                        if (f.GameInfo.HasValue && f.GameInfo.Value.Lobby.HasValue)
                        {
                            discoveredLobbyId = f.GameInfo.Value.Lobby.Value.Id.Value;
                        }
                        if (discoveredLobbyId == 0)
                        {
                            TryRequestFriendRichPresence(f.Id);
                            discoveredLobbyId = ParseLobbyIdFromRichPresence(f.GetRichPresence("bg3modsync_lobby"));
                            if (discoveredLobbyId == 0)
                            {
                                discoveredLobbyId = ParseLobbyIdFromRichPresence(f.GetRichPresence("connect"));
                            }
                        }
                    }
                }
            }
            catch { }

            if (discoveredLobbyId == 0)
            {
                var found = await QueryActiveModSyncLobbiesAsync(steamOrLobbyId, 2500);
                if (found.TryGetValue(steamOrLobbyId, out ulong liveLid) && liveLid != 0)
                {
                    discoveredLobbyId = liveLid;
                }
            }

            if (discoveredLobbyId != 0)
            {
                logCallback($"[STEAM-P2P] Discovered active Host Lobby {discoveredLobbyId} ({EncodeRoomCode(discoveredLobbyId)}) for Friend SteamID {steamOrLobbyId}!");
                return await ResolveToHostSteamIdAndEndpointsAsync(discoveredLobbyId, logCallback);
            }
        }

        return (steamOrLobbyId, null);
    }

    private static readonly TimeSpan DefaultBackpressureTimeout = TimeSpan.FromSeconds(30);

    internal static Result SendFramedMessage(Connection conn, byte cmd, uint streamId, ReadOnlySpan<byte> payload, SendType sendType = SendType.Reliable)
    {
        byte[] packet = new byte[5 + payload.Length];
        packet[0] = cmd;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), streamId);
        if (!payload.IsEmpty)
        {
            payload.CopyTo(packet.AsSpan(5));
        }
        return conn.SendMessage(packet, 0, packet.Length, sendType);
    }

    /// <summary>
    /// Executes a SteamNetworkingSockets send delegate with bounded backpressure retry when
    /// Valve returns Result.LimitExceeded (64 MB send buffer temporarily full under WAN/ICE congestion)
    /// or Result.Busy, aborting immediately on cancellation, disconnect, or fatal send errors.
    /// </summary>
    internal static async Task<Result> SendWithBackpressureAsync(
        Func<Result> sendFunc,
        Func<bool>? isStillAlive,
        Action<string>? log,
        string context,
        CancellationToken cancellationToken,
        TimeSpan? maxBackpressureWait = null)
    {
        TimeSpan waitBudget = maxBackpressureWait ?? DefaultBackpressureTimeout;
        System.Diagnostics.Stopwatch? backpressureSw = null;
        int delayMs = 2;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result.Cancelled;
            if (isStillAlive != null && !isStillAlive())
                return Result.NoConnection;

            Result res;
            try
            {
                res = sendFunc();
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WARN] [STEAM-P2P] Send exception ({context}): {ex.Message}");
                return Result.Fail;
            }

            if (res == Result.OK)
                return Result.OK;

            if (res == Result.LimitExceeded || res == Result.Busy)
            {
                backpressureSw ??= System.Diagnostics.Stopwatch.StartNew();
                if (backpressureSw.Elapsed >= waitBudget)
                {
                    log?.Invoke($"[WARN] [STEAM-P2P] Send backpressure timeout after {backpressureSw.Elapsed.TotalSeconds:F1}s ({context}, last={res}).");
                    return res;
                }

                try
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                if (delayMs < 15)
                {
                    delayMs = Math.Min(15, delayMs * 2);
                }
                continue;
            }

            // Fatal transport error (NoConnection, InvalidState, InvalidParam, Fail, etc.)
            if (isStillAlive == null || isStillAlive())
            {
                log?.Invoke($"[WARN] [STEAM-P2P] Send failed ({context}, result={res}).");
            }
            return res;
        }
    }

    internal static Task<Result> SendFramedPacketAsync(
        Connection conn,
        byte[] packetBuffer,
        int totalLength,
        Func<bool>? isStillAlive,
        Action<string>? log,
        string context,
        CancellationToken cancellationToken,
        SendType sendType = SendType.Reliable,
        TimeSpan? maxBackpressureWait = null)
    {
        return SendWithBackpressureAsync(
            () => conn.SendMessage(packetBuffer, 0, totalLength, sendType),
            isStillAlive,
            log,
            context,
            cancellationToken,
            maxBackpressureWait);
    }

    internal static Task<Result> SendFramedMessageAsync(
        Connection conn,
        byte cmd,
        uint streamId,
        ReadOnlyMemory<byte> payload,
        Func<bool>? isStillAlive,
        Action<string>? log,
        string context,
        CancellationToken cancellationToken,
        SendType sendType = SendType.Reliable,
        TimeSpan? maxBackpressureWait = null)
    {
        byte[] packet = new byte[5 + payload.Length];
        packet[0] = cmd;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), streamId);
        if (!payload.IsEmpty)
        {
            payload.Span.CopyTo(packet.AsSpan(5));
        }
        return SendFramedPacketAsync(conn, packet, packet.Length, isStillAlive, log, context, cancellationToken, sendType, maxBackpressureWait);
    }

    internal static void SendPingPacket(Connection conn)
    {
        try
        {
            Span<byte> tsBytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(tsBytes, System.Diagnostics.Stopwatch.GetTimestamp());
            SendFramedMessage(conn, CmdPing, 0, tsBytes, SendType.Reliable);
        }
        catch { }
    }

    public void Dispose()
    {
        _callbackLoopCts?.Cancel();
        try { _callbackLoopTask?.Wait(2000); } catch { }
        _callbackLoopTask = null;

        StopClientRelayTunnel();
        StopHostRelay();
        lock (_steamLifecycleLock)
        {
            if (_debugOutputSubscribed)
            {
                try { SteamNetworkingUtils.OnDebugOutput -= HandleSteamNetworkingDebugOutput; } catch { }
                _debugOutputSubscribed = false;
            }
            if (_eventsSubscribed)
            {
                try { SteamFriends.OnGameLobbyJoinRequested -= HandleGameLobbyJoinRequested; } catch { }
                try { SteamMatchmaking.OnLobbyEntered -= HandleLobbyEntered; } catch { }
                _eventsSubscribed = false;
            }
            try
            {
                if (IsSteamInitialized && SteamClient.IsValid)
                {
                    SteamClient.Shutdown();
                }
            }
            catch { }
            IsSteamInitialized = false;
        }
    }

    // =========================================================================
    // HOST SOCKET MANAGER: Bridges Direct Hole-Punched P2P -> 127.0.0.1:NginxPort
    // =========================================================================
    private sealed class HostP2PSocketManager : SocketManager
    {
        private const long MaxPendingWriteBytes = 16L * 1024 * 1024;
        private const int MaxPendingWriteChunks = 512;
        private const int MaxChannelItemsPerStream = 4096;

        private sealed class HostStreamState
        {
            public Channel<byte[]> InboundChannel { get; } = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(MaxChannelItemsPerStream)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });

            public CancellationTokenSource Cts { get; }
            public CancellationToken Token { get; }
            public volatile TcpClient? Tcp;
            private int _pumpStarted;

            public HostStreamState(CancellationToken managerToken)
            {
                Cts = CancellationTokenSource.CreateLinkedTokenSource(managerToken);
                Token = Cts.Token;
            }

            public bool TryStartPump() => Interlocked.CompareExchange(ref _pumpStarted, 1, 0) == 0;
            public void CompleteInbound() => InboundChannel.Writer.TryComplete();

            public void Abort()
            {
                try { InboundChannel.Writer.TryComplete(); } catch { }
                try { Cts.Cancel(); } catch { }
                try { Tcp?.Dispose(); } catch { }
            }
        }

        private int _nginxPort = 27000;
        private string _nearestRelay = "";
        private string _endpointsJson = "";
        private HostDirectEndpointsMetadata _localHostMeta = new();
        private Action<string>? _log;
        private Action<string, bool, string>? _routeCallback;
        private Action<ClientLobbyTelemetry>? _telemetryCallback;
        private Action<string>? _disconnectCallback;
        private CancellationTokenSource? _hostPingLoopCts;
        private readonly CancellationTokenSource _managerCts = new();
        private readonly ReaderWriterLockSlim _nativeLock = new();
        private volatile bool _closingIntentionally;
        private readonly ConcurrentDictionary<uint, (Connection Conn, ConnectionInfo Info)> _activePeers = new();
        private readonly ConcurrentDictionary<uint, HostDirectEndpointsMetadata> _peerEndpoints = new();
        private readonly ConcurrentDictionary<uint, ClientLobbyTelemetry> _peerTelemetries = new();
        private readonly ConcurrentDictionary<uint, int> _peerRttPingMs = new();
        private readonly ConcurrentDictionary<(uint ConnId, uint StreamId), HostStreamState> _streams = new();
        private long _pendingWriteBytes;
        private int _pendingWriteChunks;

        public bool HasConnectedClients => !_activePeers.IsEmpty;
        public bool IsWriteBackpressured =>
            Interlocked.Read(ref _pendingWriteBytes) >= MaxPendingWriteBytes ||
            Volatile.Read(ref _pendingWriteChunks) >= MaxPendingWriteChunks;

        public void CancelPendingOperations()
        {
            try { _managerCts.Cancel(); } catch { }
        }

        public void ReceiveBounded(int batchSize = 16, int maxBatches = 64)
        {
            if (_closingIntentionally || IsWriteBackpressured) return;

            EnsureReflectionInitialized();
            var internalObj = _snsInternalProp?.GetValue(null);
            var recvPollMethod = _recvMessagesOnPollGroupMethod;
            var pollGroupField = _socketPollGroupField;
            var recvMsgDel = _sockReceiveMessageDelegate;

            if (internalObj == null || recvPollMethod == null || pollGroupField == null || recvMsgDel == null)
            {
                Receive(batchSize);
                return;
            }

            object? pollGroupVal = pollGroupField.GetValue(this);
            if (pollGroupVal == null) return;

            IntPtr msgBuf = Marshal.AllocHGlobal(IntPtr.Size * batchSize);
            try
            {
                object[] args = new object[] { pollGroupVal, msgBuf, batchSize };
                for (int b = 0; b < maxBatches; b++)
                {
                    if (_closingIntentionally || IsWriteBackpressured) break;

                    int processed = (int)(recvPollMethod.Invoke(internalObj, args) ?? 0);
                    if (processed <= 0) break;

                    for (int i = 0; i < processed; i++)
                    {
                        IntPtr msgPtr = Marshal.ReadIntPtr(msgBuf, i * IntPtr.Size);
                        if (msgPtr != IntPtr.Zero)
                        {
                            try { recvMsgDel(this, msgPtr); } catch { }
                        }
                    }

                    if (processed < batchSize) break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(msgBuf);
            }
        }

        private Result SafeSendPeerPacket(uint connId, Connection conn, byte[] packetBuffer, int totalLength, SendType sendType)
        {
            _nativeLock.EnterReadLock();
            try
            {
                if (_closingIntentionally || !_activePeers.ContainsKey(connId))
                    return Result.NoConnection;
                return conn.SendMessage(packetBuffer, 0, totalLength, sendType);
            }
            finally
            {
                _nativeLock.ExitReadLock();
            }
        }

        private void IncrementPendingWrite(int bytes)
        {
            Interlocked.Add(ref _pendingWriteBytes, bytes);
            Interlocked.Increment(ref _pendingWriteChunks);
        }

        private void DecrementPendingWrite(int bytes)
        {
            Interlocked.Add(ref _pendingWriteBytes, -bytes);
            Interlocked.Decrement(ref _pendingWriteChunks);
        }

        private bool EnqueueChunk(Channel<byte[]> channel, byte[] payload, CancellationToken ct)
        {
            IncrementPendingWrite(payload.Length);
            if (channel.Writer.TryWrite(payload))
            {
                return true;
            }

            try
            {
                while (channel.Writer.WaitToWriteAsync(ct).AsTask().GetAwaiter().GetResult())
                {
                    if (channel.Writer.TryWrite(payload))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Stream or manager cancelled
            }

            DecrementPendingWrite(payload.Length);
            return false;
        }

        public void Configure(
            int nginxPort,
            string nearestRelay,
            string endpointsJson,
            HostDirectEndpointsMetadata localHostMeta,
            Action<string> log,
            Action<string, bool, string>? routeCallback = null,
            Action<ClientLobbyTelemetry>? telemetryCallback = null,
            Action<string>? disconnectCallback = null)
        {
            _nginxPort = nginxPort;
            _nearestRelay = nearestRelay;
            _endpointsJson = endpointsJson;
            _localHostMeta = localHostMeta;
            _log = log;
            _routeCallback = routeCallback;
            _telemetryCallback = telemetryCallback;
            _disconnectCallback = disconnectCallback;
            StartHostRealTimePingLoop();
        }

        public void UpdateNginxPort(int newPort, string endpointsJson, HostDirectEndpointsMetadata localHostMeta)
        {
            _nginxPort = newPort;
            _endpointsJson = endpointsJson;
            _localHostMeta = localHostMeta;
        }

        private void StartHostRealTimePingLoop()
        {
            _hostPingLoopCts?.Cancel();
            _hostPingLoopCts = new CancellationTokenSource();
            var token = _hostPingLoopCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var kvp in _activePeers.ToArray())
                        {
                            uint connId = kvp.Key;
                            var (conn, info) = kvp.Value;

                            _nativeLock.EnterReadLock();
                            try
                            {
                                if (_closingIntentionally || !_activePeers.ContainsKey(connId)) continue;
                                SendPingPacket(conn);
                            }
                            finally
                            {
                                _nativeLock.ExitReadLock();
                            }

                            await Task.Delay(50, token);

                            _peerEndpoints.TryGetValue(connId, out var peerMeta);
                            int rtt = _peerRttPingMs.TryGetValue(connId, out int measured) ? measured : -1;

                            int quick;
                            (string RouteLabel, bool IsRelayedThroughValve, string RelayWithPing) route;
                            string clientIpPort;
                            _nativeLock.EnterReadLock();
                            try
                            {
                                if (_closingIntentionally || !_activePeers.ContainsKey(connId)) continue;
                                quick = GetQuickConnectionPingMs(conn);
                                int bestPingInner = -1;
                                if (rtt >= 0 && quick > 0) bestPingInner = Math.Min(rtt, quick);
                                else if (rtt >= 0) bestPingInner = rtt;
                                else if (quick >= 0) bestPingInner = quick;

                                route = InspectSteamConnectionRoute(
                                    conn,
                                    info,
                                    _nearestRelay,
                                    peerMeta,
                                    _localHostMeta,
                                    bestPingInner,
                                    _localHostMeta.HostSteamId);

                                clientIpPort = ResolvePunchedIpAndPort(conn, info, peerMeta, _localHostMeta);
                            }
                            finally
                            {
                                _nativeLock.ExitReadLock();
                            }

                            int bestPing = -1;
                            if (rtt >= 0 && quick > 0) bestPing = Math.Min(rtt, quick);
                            else if (rtt >= 0) bestPing = rtt;
                            else if (quick >= 0) bestPing = quick;

                            var (routeLabel, isRelay, relayPing) = route;

                            // Host should never trigger the yellow "Blocked SDR Relay" warning banner during signaling;
                            // relay status is surfaced as a per-client CGNAT badge instead.
                            _routeCallback?.Invoke(routeLabel, false, relayPing);

                            ulong peerSteamId = (peerMeta != null && peerMeta.HostSteamId > 0)
                                ? peerMeta.HostSteamId
                                : GetRemoteSteamId(info);
                            string clientKey = peerSteamId > 0 ? $"steam_{peerSteamId}" : $"conn_{connId}";

                            if (_peerTelemetries.TryGetValue(connId, out var telemetry))
                            {
                                if (peerSteamId > 0)
                                {
                                    telemetry.SteamId = peerSteamId;
                                    telemetry.ClientKey = clientKey;
                                }
                                if (bestPing >= 0) telemetry.PingMs = bestPing;
                                if (!string.IsNullOrWhiteSpace(clientIpPort)) telemetry.IpAndPort = clientIpPort;
                                if (isRelay) telemetry.IsCgnat = true;
                                if (peerMeta != null && !string.IsNullOrWhiteSpace(peerMeta.HostName) &&
                                    (string.IsNullOrWhiteSpace(telemetry.Name) || telemetry.Name.StartsWith("Client") || telemetry.Name.StartsWith("Steam User")))
                                {
                                    telemetry.Name = peerMeta.HostName;
                                }
                                _telemetryCallback?.Invoke(telemetry);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Ignore transient error
                    }

                    await Task.Delay(950, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }, token);
        }

        public override void OnConnecting(Connection connection, ConnectionInfo info)
        {
            // base.OnConnecting(connection, info) already calls connection.Accept() in Facepunch.Steamworks 2.3.3.
            base.OnConnecting(connection, info);
            ulong peerSteamId = GetRemoteSteamId(info);
            _log?.Invoke($"[STEAM-P2P] Incoming P2P handshake from SteamID {peerSteamId}...");
        }

        public override void OnConnected(Connection connection, ConnectionInfo info)
        {
            base.OnConnected(connection, info);
            _activePeers[connection.Id] = (connection, info);
            SendPingPacket(connection);
            var (routeLabel, isRelay, relayPing) = InspectSteamConnectionRoute(
                connection,
                info,
                _nearestRelay,
                null,
                _localHostMeta,
                -1,
                _localHostMeta.HostSteamId);
            _routeCallback?.Invoke(routeLabel, false, relayPing);
            ulong peerSteamId = GetRemoteSteamId(info);
            _log?.Invoke($"[STEAM-P2P] Client connected ({routeLabel}) — SteamID {peerSteamId}");

            if (peerSteamId > 0)
            {
                string peerName = "";
                try { peerName = new Friend(peerSteamId).Name; } catch { }
                if (string.IsNullOrWhiteSpace(peerName)) peerName = $"Steam User ({peerSteamId})";

                var initialTelemetry = new ClientLobbyTelemetry
                {
                    ClientKey = $"steam_{peerSteamId}",
                    SteamId = peerSteamId,
                    Name = peerName,
                    IpAndPort = ResolvePunchedIpAndPort(connection, info, null, _localHostMeta),
                    PingMs = Math.Max(1, GetQuickConnectionPingMs(connection)),
                    StageText = "Handshaked — Ready",
                    ProgressPercent = 0,
                    SpeedBytesPerSec = 0,
                    IsSteamMode = true,
                    IsCgnat = isRelay
                };
                _peerTelemetries[connection.Id] = initialTelemetry;
                _telemetryCallback?.Invoke(initialTelemetry);
            }
        }

        public override void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            _activePeers.TryRemove(connection.Id, out _);
            _peerEndpoints.TryRemove(connection.Id, out _);
            _peerRttPingMs.TryRemove(connection.Id, out _);

            _nativeLock.EnterWriteLock();
            try
            {
                base.OnDisconnected(connection, info);
            }
            finally
            {
                _nativeLock.ExitWriteLock();
            }

            ulong peerSteamId = GetRemoteSteamId(info);
            string diag = FormatDisconnectDiagnostics(info);

            if (_peerTelemetries.TryRemove(connection.Id, out var removedTelemetry))
            {
                _disconnectCallback?.Invoke(removedTelemetry.ClientKey);
            }
            else if (peerSteamId > 0)
            {
                _disconnectCallback?.Invoke($"steam_{peerSteamId}");
            }
            if (!_closingIntentionally)
            {
                _log?.Invoke($"[STEAM-P2P] Client disconnected: SteamID {peerSteamId} ({diag})");
            }

            foreach (var kvp in _streams.Where(kv => kv.Key.ConnId == connection.Id).ToList())
            {
                AbortStream(kvp.Key);
            }
            if (_activePeers.IsEmpty)
            {
                _routeCallback?.Invoke("", false, _nearestRelay);
            }
        }

        private void AbortStream((uint ConnId, uint StreamId) key)
        {
            if (_streams.TryRemove(key, out var state))
            {
                state.Abort();
            }
        }

        private HostStreamState GetOrCreateStream(Connection connection, (uint ConnId, uint StreamId) key)
        {
            var state = _streams.GetOrAdd(key, _ => new HostStreamState(_managerCts.Token));
            if (state.TryStartPump())
            {
                _ = Task.Run(() => RunHostStreamPumpAsync(connection, key, state));
            }
            return state;
        }

        public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            try
            {
                if (size < 5) return;
                byte[] buf = new byte[size];
                Marshal.Copy(data, buf, 0, size);

                byte cmd = buf[0];
                uint streamId = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(1, 4));
                var key = (connection.Id, streamId);

                if (cmd == CmdPing)
                {
                    SendFramedMessage(connection, CmdPong, streamId, buf.AsSpan(5, size - 5), SendType.Reliable);
                    return;
                }

                if (cmd == CmdPong && size >= 13)
                {
                    long sentTicks = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(5, 8));
                    double rttMs = System.Diagnostics.Stopwatch.GetElapsedTime(sentTicks).TotalMilliseconds;
                    _peerRttPingMs[connection.Id] = Math.Max(1, (int)Math.Round(rttMs));
                    return;
                }

                if (cmd == CmdHandshakeEndpoints)
                {
                    if (size > 5)
                    {
                        try
                        {
                            string clientJson = Encoding.UTF8.GetString(buf, 5, size - 5);
                            var clientMeta = JsonSerializer.Deserialize<HostDirectEndpointsMetadata>(clientJson);
                            if (clientMeta != null)
                            {
                                _peerEndpoints[connection.Id] = clientMeta;
                                if (_activePeers.TryGetValue(connection.Id, out var peerEntry))
                                {
                                    string punched = ResolvePunchedIpAndPort(peerEntry.Conn, peerEntry.Info, clientMeta, _localHostMeta);
                                    ulong effectiveSteamId = clientMeta.HostSteamId > 0 ? clientMeta.HostSteamId : GetRemoteSteamId(peerEntry.Info);
                                    string effectiveKey = effectiveSteamId > 0 ? $"steam_{effectiveSteamId}" : $"conn_{connection.Id}";

                                    if (!_peerTelemetries.TryGetValue(connection.Id, out var existingTel))
                                    {
                                        existingTel = new ClientLobbyTelemetry
                                        {
                                            ClientKey = effectiveKey,
                                            SteamId = effectiveSteamId,
                                            Name = !string.IsNullOrWhiteSpace(clientMeta.HostName) ? clientMeta.HostName : $"Steam User ({effectiveSteamId})",
                                            IpAndPort = punched,
                                            PingMs = Math.Max(1, GetQuickConnectionPingMs(peerEntry.Conn)),
                                            StageText = "Handshaked — Ready",
                                            ProgressPercent = 0,
                                            SpeedBytesPerSec = 0,
                                            IsSteamMode = true
                                        };
                                        _peerTelemetries[connection.Id] = existingTel;
                                    }
                                    else
                                    {
                                        if (effectiveSteamId > 0)
                                        {
                                            existingTel.SteamId = effectiveSteamId;
                                            existingTel.ClientKey = effectiveKey;
                                        }
                                        if (!string.IsNullOrWhiteSpace(clientMeta.HostName)) existingTel.Name = clientMeta.HostName;
                                        if (!string.IsNullOrWhiteSpace(punched)) existingTel.IpAndPort = punched;
                                        if (existingTel.ProgressPercent <= 0 && existingTel.StageText.Contains("Handshak"))
                                            existingTel.StageText = "Handshaked — Ready";
                                    }
                                    _telemetryCallback?.Invoke(existingTel);
                                }
                            }
                        }
                        catch { }
                    }

                    byte[] jsonBytes = Encoding.UTF8.GetBytes(_endpointsJson);
                    SendFramedMessage(connection, CmdHandshakeEndpoints, streamId, jsonBytes);
                    SendPingPacket(connection);
                    return;
                }

                if (cmd == CmdClientTelemetry && size > 5)
                {
                    try
                    {
                        string telJson = Encoding.UTF8.GetString(buf, 5, size - 5);
                        var incoming = JsonSerializer.Deserialize<ClientLobbyTelemetry>(telJson);
                        if (incoming != null)
                        {
                            ulong peerSteamId = identity.SteamId.Value;
                            if (peerSteamId == 0 && _activePeers.TryGetValue(connection.Id, out var pEntry))
                                peerSteamId = GetRemoteSteamId(pEntry.Info);
                            if (peerSteamId > 0 && incoming.SteamId == 0) incoming.SteamId = peerSteamId;
                            if (string.IsNullOrWhiteSpace(incoming.ClientKey))
                                incoming.ClientKey = incoming.SteamId > 0 ? $"steam_{incoming.SteamId}" : $"conn_{connection.Id}";

                            if (_peerRttPingMs.TryGetValue(connection.Id, out int hostMeasuredRtt) && hostMeasuredRtt > 0)
                            {
                                incoming.PingMs = hostMeasuredRtt;
                            }
                            if (_activePeers.TryGetValue(connection.Id, out var peerEntry))
                            {
                                _peerEndpoints.TryGetValue(connection.Id, out var cMeta);
                                string resolvedIpPort = ResolvePunchedIpAndPort(peerEntry.Conn, peerEntry.Info, cMeta, _localHostMeta);
                                if (!string.IsNullOrWhiteSpace(resolvedIpPort))
                                {
                                    incoming.IpAndPort = resolvedIpPort;
                                }
                            }

                            _peerTelemetries[connection.Id] = incoming;
                            _telemetryCallback?.Invoke(incoming);
                        }
                    }
                    catch { }
                    return;
                }

                if (cmd == CmdOpenStream)
                {
                    GetOrCreateStream(connection, key);
                }
                else if (cmd == CmdDataChunk)
                {
                    int payloadLen = size - 5;
                    if (payloadLen <= 0) return;
                    if (_streams.TryGetValue(key, out var state))
                    {
                        byte[] payload = new byte[payloadLen];
                        Buffer.BlockCopy(buf, 5, payload, 0, payloadLen);
                        EnqueueChunk(state.InboundChannel, payload, state.Token);
                    }
                }
                else if (cmd == CmdCloseStream)
                {
                    bool isAbort = size > 5 && buf[5] == 1;
                    if (isAbort)
                    {
                        AbortStream(key);
                    }
                    else if (_streams.TryGetValue(key, out var state))
                    {
                        // Complete inbound FIFO channel so all queued request bytes flush to Nginx before half-closing.
                        state.CompleteInbound();
                    }
                }
            }
            catch { }
        }

        private async Task RunHostStreamPumpAsync(Connection connection, (uint ConnId, uint StreamId) key, HostStreamState state)
        {
            TcpClient? tcp = null;
            bool inboundReaderStarted = false;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                state.Tcp = tcp;
                await tcp.ConnectAsync(IPAddress.Loopback, _nginxPort, state.Token).ConfigureAwait(false);
                var netStream = tcp.GetStream();

                inboundReaderStarted = true;
                Task inboundWriterTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (byte[] chunk in state.InboundChannel.Reader.ReadAllAsync(state.Token).ConfigureAwait(false))
                        {
                            DecrementPendingWrite(chunk.Length);
                            await netStream.WriteAsync(chunk, 0, chunk.Length, state.Token).ConfigureAwait(false);
                        }
                        await netStream.FlushAsync(state.Token).ConfigureAwait(false);
                        try { tcp.Client.Shutdown(SocketShutdown.Send); } catch { }
                    }
                    catch
                    {
                        try { state.Cts.Cancel(); } catch { }
                    }
                    finally
                    {
                        state.CompleteInbound();
                        while (state.InboundChannel.Reader.TryRead(out var leftover))
                        {
                            DecrementPendingWrite(leftover.Length);
                        }
                    }
                });

                Task outboundReaderTask = Task.Run(async () =>
                {
                    byte[] packetBuf = new byte[5 + ChunkSize];
                    packetBuf[0] = CmdDataChunk;
                    BinaryPrimitives.WriteUInt32LittleEndian(packetBuf.AsSpan(1, 4), key.StreamId);
                    try
                    {
                        int read;
                        while ((read = await netStream.ReadAsync(packetBuf.AsMemory(5, ChunkSize), state.Token).ConfigureAwait(false)) > 0)
                        {
                            int packetLen = 5 + read;
                            var sendRes = await SendWithBackpressureAsync(
                                () => SafeSendPeerPacket(key.ConnId, connection, packetBuf, packetLen, SendType.Reliable),
                                () => !_closingIntentionally && _activePeers.ContainsKey(key.ConnId),
                                _log,
                                $"Host->Client stream {key.StreamId}",
                                state.Token).ConfigureAwait(false);
                            if (sendRes != Result.OK)
                            {
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Socket closed or cancelled
                    }
                    finally
                    {
                        // Nginx finished sending response (or errored); stop waiting on inbound request data
                        state.CompleteInbound();
                        try { state.Cts.Cancel(); } catch { }
                    }
                });

                await Task.WhenAll(inboundWriterTask, outboundReaderTask).ConfigureAwait(false);
            }
            catch
            {
                // Failed to connect to local Nginx or cancelled
            }
            finally
            {
                state.CompleteInbound();
                if (!inboundReaderStarted)
                {
                    while (state.InboundChannel.Reader.TryRead(out var leftover))
                    {
                        DecrementPendingWrite(leftover.Length);
                    }
                }
                _streams.TryRemove(new KeyValuePair<(uint ConnId, uint StreamId), HostStreamState>(key, state));
                try { tcp?.Dispose(); } catch { }
                try { state.Cts.Dispose(); } catch { }

                if (!_closingIntentionally && _activePeers.ContainsKey(key.ConnId) && !_managerCts.IsCancellationRequested)
                {
                    try
                    {
                        byte[] closePacket = new byte[5];
                        closePacket[0] = CmdCloseStream;
                        BinaryPrimitives.WriteUInt32LittleEndian(closePacket.AsSpan(1, 4), key.StreamId);
                        await SendWithBackpressureAsync(
                            () => SafeSendPeerPacket(key.ConnId, connection, closePacket, closePacket.Length, SendType.Reliable),
                            () => !_closingIntentionally && _activePeers.ContainsKey(key.ConnId),
                            _log,
                            $"Host->Client CloseStream {key.StreamId}",
                            _managerCts.Token,
                            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        public void CloseAll()
        {
            _closingIntentionally = true;
            CancelPendingOperations();
            _hostPingLoopCts?.Cancel();
            _hostPingLoopCts = null;

            var peersToClose = _activePeers.Values.ToArray();
            _activePeers.Clear();
            _peerEndpoints.Clear();
            _peerRttPingMs.Clear();
            foreach (var key in _streams.Keys.ToList())
            {
                AbortStream(key);
            }

            _nativeLock.EnterWriteLock();
            try
            {
                // Explicitly close all accepted peer connections BEFORE destroying the listen socket / poll group
                // so remote peers get a clean ClosedByPeer notification and Valve's loopback partner stays consistent.
                foreach (var (conn, _) in peersToClose)
                {
                    try { conn.Close(linger: false, reasonCode: 1000, debugString: "Host stopped"); } catch { }
                }
                try { Close(); } catch { }
            }
            finally
            {
                _nativeLock.ExitWriteLock();
            }
        }
    }

    // =========================================================================
    // CLIENT CONNECTION MANAGER: Bridges Local Loopback Socket -> Direct P2P Host
    // =========================================================================
    private sealed class ClientP2PConnectionManager : ConnectionManager
    {
        private const long MaxPendingWriteBytes = 16L * 1024 * 1024;
        private const int MaxPendingWriteChunks = 512;
        private const int MaxChannelItemsPerStream = 4096;

        private sealed class ClientStreamState
        {
            public TcpClient Tcp { get; }
            public Channel<byte[]> WriteChannel { get; } = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(MaxChannelItemsPerStream)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });
            public CancellationTokenSource Cts { get; }
            public CancellationToken Token { get; }
            public volatile bool RemoteClosed;
            public volatile bool LocalWriteFailed;

            public ClientStreamState(TcpClient tcp, CancellationToken managerToken)
            {
                Tcp = tcp;
                Cts = CancellationTokenSource.CreateLinkedTokenSource(managerToken);
                Token = Cts.Token;
            }

            public void CompleteWrites() => WriteChannel.Writer.TryComplete();

            public void Abort()
            {
                try { WriteChannel.Writer.TryComplete(); } catch { }
                try { Cts.Cancel(); } catch { }
                try { Tcp.Dispose(); } catch { }
            }
        }

        private string _nearestRelay = "";
        private HostDirectEndpointsMetadata _clientLocalMeta = new();
        private HostDirectEndpointsMetadata? _remoteHostMeta;
        private Action<string>? _log;
        private Action<string, bool, string>? _routeCallback;
        private readonly CancellationTokenSource _managerCts = new();
        private readonly ReaderWriterLockSlim _nativeLock = new();
        private readonly ConcurrentDictionary<uint, ClientStreamState> _streams = new();
        private TaskCompletionSource<HostDirectEndpointsMetadata?>? _endpointsTcs;
        private volatile bool _closingIntentionally;
        private volatile bool _nativeClosed;
        private long _pendingWriteBytes;
        private int _pendingWriteChunks;

        public bool IsConnected { get; private set; }
        public bool IsFailed { get; private set; }
        public int LastRttPingMs { get; private set; } = -1;
        public string LastDisconnectSummary { get; private set; } = "";
        public bool IsWriteBackpressured =>
            Interlocked.Read(ref _pendingWriteBytes) >= MaxPendingWriteBytes ||
            Volatile.Read(ref _pendingWriteChunks) >= MaxPendingWriteChunks;

        public void CancelPendingOperations()
        {
            try { _managerCts.Cancel(); } catch { }
        }

        public void ReceiveBounded(int batchSize = 16, int maxBatches = 64)
        {
            if (_closingIntentionally || _nativeClosed || IsWriteBackpressured) return;

            EnsureReflectionInitialized();
            var internalObj = _snsInternalProp?.GetValue(null);
            var recvConnMethod = _recvMessagesOnConnMethod;
            var recvMsgDel = _connReceiveMessageDelegate;

            if (internalObj == null || recvConnMethod == null || recvMsgDel == null)
            {
                Receive(batchSize);
                return;
            }

            IntPtr msgBuf = Marshal.AllocHGlobal(IntPtr.Size * batchSize);
            try
            {
                object[] args = new object[] { Connection, msgBuf, batchSize };
                for (int b = 0; b < maxBatches; b++)
                {
                    if (_closingIntentionally || _nativeClosed || IsWriteBackpressured) break;

                    int processed = (int)(recvConnMethod.Invoke(internalObj, args) ?? 0);
                    if (processed <= 0) break;

                    for (int i = 0; i < processed; i++)
                    {
                        IntPtr msgPtr = Marshal.ReadIntPtr(msgBuf, i * IntPtr.Size);
                        if (msgPtr != IntPtr.Zero)
                        {
                            try { recvMsgDel(this, msgPtr); } catch { }
                        }
                    }

                    if (processed < batchSize) break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(msgBuf);
            }
        }

        private Result SafeSendPacket(byte[] packetBuffer, int totalLength, SendType sendType)
        {
            _nativeLock.EnterReadLock();
            try
            {
                if (_nativeClosed || !IsConnected)
                    return Result.NoConnection;
                return Connection.SendMessage(packetBuffer, 0, totalLength, sendType);
            }
            finally
            {
                _nativeLock.ExitReadLock();
            }
        }

        public bool TrySnapshotLiveRouteAndPing(
            HostDirectEndpointsMetadata? hostEndpoints,
            HostDirectEndpointsMetadata? clientLocalMeta,
            string fallbackRelaySummary,
            ulong localSteamId,
            out string endpoint,
            out int pingMs,
            out bool isRelayedThroughValve,
            out string relayWithPing)
        {
            endpoint = "";
            pingMs = -1;
            isRelayedThroughValve = false;
            relayWithPing = "";
            _nativeLock.EnterReadLock();
            try
            {
                if (_nativeClosed || !IsConnected)
                    return false;

                endpoint = ResolvePunchedIpAndPort(Connection, ConnectionInfo, hostEndpoints, clientLocalMeta);
                int rtt = LastRttPingMs;
                int quick = GetQuickConnectionPingMs(Connection);
                if (rtt >= 0 && quick >= 0)
                    pingMs = Math.Min(rtt, quick > 0 ? quick : rtt);
                else if (rtt >= 0)
                    pingMs = rtt;
                else if (quick >= 0)
                    pingMs = quick;

                var routeCheck = InspectSteamConnectionRoute(
                    Connection,
                    ConnectionInfo,
                    fallbackRelaySummary,
                    hostEndpoints,
                    clientLocalMeta,
                    pingMs,
                    localSteamId);
                isRelayedThroughValve = routeCheck.IsRelayedThroughValve;
                relayWithPing = routeCheck.RelayWithPing;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _nativeLock.ExitReadLock();
            }
        }

        private void IncrementPendingWrite(int bytes)
        {
            Interlocked.Add(ref _pendingWriteBytes, bytes);
            Interlocked.Increment(ref _pendingWriteChunks);
        }

        private void DecrementPendingWrite(int bytes)
        {
            Interlocked.Add(ref _pendingWriteBytes, -bytes);
            Interlocked.Decrement(ref _pendingWriteChunks);
        }

        private bool EnqueueChunk(Channel<byte[]> channel, byte[] payload, CancellationToken ct)
        {
            IncrementPendingWrite(payload.Length);
            if (channel.Writer.TryWrite(payload))
            {
                return true;
            }

            try
            {
                while (channel.Writer.WaitToWriteAsync(ct).AsTask().GetAwaiter().GetResult())
                {
                    if (channel.Writer.TryWrite(payload))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Stream or manager cancelled
            }

            DecrementPendingWrite(payload.Length);
            return false;
        }

        public void Configure(
            string nearestRelay,
            HostDirectEndpointsMetadata clientLocalMeta,
            Action<string> log,
            Action<string, bool, string>? routeCallback = null)
        {
            _nearestRelay = nearestRelay;
            _clientLocalMeta = clientLocalMeta;
            _log = log;
            _routeCallback = routeCallback;
        }

        public void SetRemoteHostEndpoints(HostDirectEndpointsMetadata? remoteHostMeta)
        {
            _remoteHostMeta = remoteHostMeta;
        }

        public void SendPing()
        {
            _nativeLock.EnterReadLock();
            try
            {
                if (!_nativeClosed && IsConnected)
                {
                    SendPingPacket(Connection);
                }
            }
            finally
            {
                _nativeLock.ExitReadLock();
            }
        }

        public void SendTelemetry(ClientLobbyTelemetry telemetry)
        {
            _nativeLock.EnterReadLock();
            try
            {
                if (_nativeClosed || !IsConnected) return;
                byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(telemetry));
                SendFramedMessage(Connection, CmdClientTelemetry, 0, jsonBytes, SendType.Reliable);
            }
            catch { }
            finally
            {
                _nativeLock.ExitReadLock();
            }
        }

        public async Task<HostDirectEndpointsMetadata?> RequestHostEndpointsAsync(
            HostDirectEndpointsMetadata clientLocalMeta,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _endpointsTcs = new TaskCompletionSource<HostDirectEndpointsMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] clientJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(clientLocalMeta));
            _nativeLock.EnterReadLock();
            try
            {
                if (_nativeClosed || !IsConnected) return null;
                SendFramedMessage(Connection, CmdHandshakeEndpoints, 0, clientJsonBytes);
                SendPingPacket(Connection);
            }
            finally
            {
                _nativeLock.ExitReadLock();
            }
            var completed = await Task.WhenAny(_endpointsTcs.Task, Task.Delay(timeout, cancellationToken));
            if (completed == _endpointsTcs.Task)
            {
                return await _endpointsTcs.Task;
            }
            return null;
        }

        public override void OnConnected(ConnectionInfo info)
        {
            base.OnConnected(info);
            IsConnected = true;
            SendPingPacket(Connection);
            var (routeLabel, isRelay, relayPing) = InspectSteamConnectionRoute(
                Connection,
                info,
                _nearestRelay,
                _remoteHostMeta,
                _clientLocalMeta,
                LastRttPingMs,
                _clientLocalMeta.HostSteamId);
            _routeCallback?.Invoke(routeLabel, isRelay, relayPing);
            ulong hostSteamId = GetRemoteSteamId(info);
            _log?.Invoke($"[STEAM-P2P] Connected to Host ({routeLabel}) — Host SteamID: {hostSteamId}");
        }

        public override void OnDisconnected(ConnectionInfo info)
        {
            IsConnected = false;
            IsFailed = true;

            bool alreadyClosed;
            _nativeLock.EnterWriteLock();
            try
            {
                alreadyClosed = _nativeClosed;
                _nativeClosed = true;
                base.OnDisconnected(info);
                if (!alreadyClosed)
                {
                    try { Connection.Close(linger: false, reasonCode: 0, debugString: "Peer disconnected"); } catch { }
                }
            }
            finally
            {
                _nativeLock.ExitWriteLock();
            }

            // Preserve the primary disconnect reason (ClosedByPeer / ProblemDetectedLocally) instead of
            // overwriting or double-logging the secondary State=None callback emitted by Connection.Close().
            if (!alreadyClosed || string.IsNullOrEmpty(LastDisconnectSummary))
            {
                string diag = FormatDisconnectDiagnostics(info);
                LastDisconnectSummary = diag;
                ulong hostSteamId = GetRemoteSteamId(info);
                if (!_closingIntentionally)
                {
                    _log?.Invoke($"[STEAM-P2P] Disconnected from Host (SteamID {hostSteamId}): {diag}");
                }
            }

            _endpointsTcs?.TrySetResult(null);
            AbortAllStreams();
        }

        private void AbortAllStreams()
        {
            foreach (var key in _streams.Keys.ToList())
            {
                if (_streams.TryRemove(key, out var state))
                {
                    state.Abort();
                }
            }
        }

        public void AttachLocalSocket(uint streamId, TcpClient client)
        {
            var state = new ClientStreamState(client, _managerCts.Token);
            _streams[streamId] = state;

            _ = Task.Run(async () =>
            {
                NetworkStream netStream;
                try
                {
                    netStream = client.GetStream();
                }
                catch
                {
                    _streams.TryRemove(new KeyValuePair<uint, ClientStreamState>(streamId, state));
                    state.Abort();
                    try { state.Cts.Dispose(); } catch { }
                    return;
                }

                // Dedicated async writer draining the per-stream bounded Channel<byte[]> in strict FIFO order
                // so OnMessage never blocks StartCallbackPump / _steamLifecycleLock with synchronous socket writes.
                Task writeTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (byte[] chunk in state.WriteChannel.Reader.ReadAllAsync(state.Token).ConfigureAwait(false))
                        {
                            DecrementPendingWrite(chunk.Length);
                            await netStream.WriteAsync(chunk, 0, chunk.Length, state.Token).ConfigureAwait(false);
                        }
                        await netStream.FlushAsync(state.Token).ConfigureAwait(false);
                        // Send clean TCP FIN to local HttpClient after all queued chunks have flushed
                        try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                    }
                    catch
                    {
                        state.LocalWriteFailed = true;
                        try { state.Cts.Cancel(); } catch { }
                    }
                    finally
                    {
                        state.CompleteWrites();
                        while (state.WriteChannel.Reader.TryRead(out var leftover))
                        {
                            DecrementPendingWrite(leftover.Length);
                        }
                    }
                });

                bool readTaskFailed = false;
                Task readTask = Task.Run(async () =>
                {
                    try
                    {
                        byte[] openPacket = new byte[5];
                        openPacket[0] = CmdOpenStream;
                        BinaryPrimitives.WriteUInt32LittleEndian(openPacket.AsSpan(1, 4), streamId);
                        var openRes = await SendWithBackpressureAsync(
                            () => SafeSendPacket(openPacket, openPacket.Length, SendType.Reliable),
                            () => !_nativeClosed && IsConnected,
                            _log,
                            $"Client->Host OpenStream {streamId}",
                            state.Token).ConfigureAwait(false);
                        if (openRes != Result.OK)
                        {
                            readTaskFailed = true;
                            try { state.Cts.Cancel(); } catch { }
                            return;
                        }

                        byte[] packetBuf = new byte[5 + ChunkSize];
                        packetBuf[0] = CmdDataChunk;
                        BinaryPrimitives.WriteUInt32LittleEndian(packetBuf.AsSpan(1, 4), streamId);

                        int read;
                        while ((read = await netStream.ReadAsync(packetBuf.AsMemory(5, ChunkSize), state.Token).ConfigureAwait(false)) > 0)
                        {
                            int packetLen = 5 + read;
                            var sendRes = await SendWithBackpressureAsync(
                                () => SafeSendPacket(packetBuf, packetLen, SendType.Reliable),
                                () => !_nativeClosed && IsConnected,
                                _log,
                                $"Client->Host stream {streamId}",
                                state.Token).ConfigureAwait(false);
                            if (sendRes != Result.OK)
                            {
                                readTaskFailed = true;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        if (!state.RemoteClosed)
                        {
                            readTaskFailed = true;
                        }
                    }
                    finally
                    {
                        if (!state.RemoteClosed && !_nativeClosed && IsConnected && !_managerCts.IsCancellationRequested)
                        {
                            try
                            {
                                bool isAbort = state.LocalWriteFailed || readTaskFailed;
                                byte[] closePacket = new byte[isAbort ? 6 : 5];
                                closePacket[0] = CmdCloseStream;
                                BinaryPrimitives.WriteUInt32LittleEndian(closePacket.AsSpan(1, 4), streamId);
                                if (isAbort) closePacket[5] = 1;
                                await SendWithBackpressureAsync(
                                    () => SafeSendPacket(closePacket, closePacket.Length, SendType.Reliable),
                                    () => !_nativeClosed && IsConnected,
                                    _log,
                                    $"Client->Host CloseStream {streamId}",
                                    _managerCts.Token,
                                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                            }
                            catch { }
                        }
                    }
                });

                try
                {
                    Task firstDone = await Task.WhenAny(writeTask, readTask).ConfigureAwait(false);
                    if (firstDone == writeTask)
                    {
                        if (state.RemoteClosed && !state.LocalWriteFailed)
                        {
                            // Wait briefly for local HttpClient to observe EOF & close cleanly (prevents Windows loopback TCP RST)
                            await Task.WhenAny(readTask, Task.Delay(5000, _managerCts.Token)).ConfigureAwait(false);
                        }
                        try { state.Cts.Cancel(); } catch { }
                    }
                    else
                    {
                        if (state.LocalWriteFailed || readTaskFailed || !IsConnected)
                        {
                            try { state.Cts.Cancel(); } catch { }
                        }
                    }

                    await Task.WhenAll(writeTask, readTask).ConfigureAwait(false);
                }
                catch { }
                finally
                {
                    _streams.TryRemove(new KeyValuePair<uint, ClientStreamState>(streamId, state));
                    state.CompleteWrites();
                    try { client.Dispose(); } catch { }
                    try { state.Cts.Dispose(); } catch { }
                }
            });
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            try
            {
                if (size < 5) return;
                byte[] buf = new byte[size];
                Marshal.Copy(data, buf, 0, size);

                byte cmd = buf[0];
                uint streamId = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(1, 4));

                if (cmd == CmdPing)
                {
                    SendFramedMessage(Connection, CmdPong, streamId, buf.AsSpan(5, size - 5), SendType.Reliable);
                    return;
                }

                if (cmd == CmdPong && size >= 13)
                {
                    long sentTicks = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(5, 8));
                    double rttMs = System.Diagnostics.Stopwatch.GetElapsedTime(sentTicks).TotalMilliseconds;
                    LastRttPingMs = Math.Max(1, (int)Math.Round(rttMs));
                    return;
                }

                if (cmd == CmdHandshakeEndpoints)
                {
                    try
                    {
                        string json = Encoding.UTF8.GetString(buf, 5, size - 5);
                        var meta = JsonSerializer.Deserialize<HostDirectEndpointsMetadata>(json);
                        _remoteHostMeta = meta;
                        _endpointsTcs?.TrySetResult(meta);
                    }
                    catch
                    {
                        _endpointsTcs?.TrySetResult(null);
                    }
                    return;
                }

                if (cmd == CmdDataChunk)
                {
                    int payloadLen = size - 5;
                    if (payloadLen <= 0) return;
                    if (_streams.TryGetValue(streamId, out var state))
                    {
                        byte[] payload = new byte[payloadLen];
                        Buffer.BlockCopy(buf, 5, payload, 0, payloadLen);
                        EnqueueChunk(state.WriteChannel, payload, state.Token);
                    }
                }
                else if (cmd == CmdCloseStream)
                {
                    if (_streams.TryGetValue(streamId, out var state))
                    {
                        state.RemoteClosed = true;
                        // Complete bounded channel so the dedicated async writer drains all queued chunks in FIFO order before closing.
                        state.CompleteWrites();
                    }
                }
            }
            catch { }
        }

        public void CloseAll()
        {
            _closingIntentionally = true;
            CancelPendingOperations();
            AbortAllStreams();

            _nativeLock.EnterWriteLock();
            try
            {
                IsConnected = false;
                if (!_nativeClosed)
                {
                    _nativeClosed = true;
                    try { Close(); } catch { }
                }
            }
            finally
            {
                _nativeLock.ExitWriteLock();
            }
        }
    }
}
