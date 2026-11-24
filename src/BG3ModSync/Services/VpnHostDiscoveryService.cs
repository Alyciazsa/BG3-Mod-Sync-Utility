using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BG3ModSync.Models;

namespace BG3ModSync.Services;

/// <summary>
/// Represents an online BG3ModSync host discovered across active VPN networks.
/// </summary>
public class VpnDiscoveredHostInfo
{
    public string HostName { get; set; } = "";
    public string VpnIp { get; set; } = "";
    public int NginxPort { get; set; } = 27000;
    public string VpnProvider { get; set; } = "VPN";
    public int FileCount { get; set; }
    public long TotalModBytes { get; set; }
    public string HostingState { get; set; } = "Hosting";
    public int PingMs { get; set; } = -1;
    public bool DiscoveredViaUdp { get; set; }
    public bool VerifiedViaHttp { get; set; }
    public bool IsLocalSelf { get; set; }
    public string DiscoveryMethod { get; set; } = "";

    public string DisplayName => IsLocalSelf ? $"{HostName} (This PC)" : HostName;
    public string EndpointDisplay => $"{VpnIp}:{NginxPort}";
    public string ProviderBadge => $"[{VpnProvider}]";
    public string PingDisplay => PingMs >= 0 ? $"{PingMs} ms" : "Online";

    public string StatusSummary
    {
        get
        {
            string baseState = FileCount > 0
                ? $"{(string.IsNullOrWhiteSpace(HostingState) ? "Hosting" : HostingState)} • {FileCount} files ({PathResolverService.FormatBytes(TotalModBytes)})"
                : (!string.IsNullOrWhiteSpace(HostingState) ? HostingState : "Nginx HTTP Server Online");

            return string.IsNullOrWhiteSpace(DiscoveryMethod)
                ? baseState
                : $"{baseState} • {DiscoveryMethod}";
        }
    }
}

/// <summary>
/// Provides zero-LAN-leak VPN host broadcasting (UDP broadcast, multicast, and mesh unicast)
/// and one-click "Find Host" discovery across Tailscale, ZeroTier, NetBird, Radmin VPN,
/// LogMeIn Hamachi, WireGuard, OpenVPN, SoftEther, and Nebula virtual networks.
/// </summary>
public class VpnHostDiscoveryService : IDisposable
{
    public const int DiscoveryUdpPort = 27001;
    public const string MulticastGroupIp = "239.255.66.71";
    private const string DiscoverMagic = "BG3MODSYNC_VPN_DISCOVER_V1";
    private const string HostBeaconMagic = "BG3MODSYNC_VPN_HOST_V1";
    private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
    private const int IP_UNICAST_IF = 31;
    private const int IP_MULTICAST_IF = 9;
    private const int MaxHttpVerifyPeers = 32;
    private const int MaxSmallSubnetSweepHosts = 512;

    private static int _firewallRuleEnsured;
    private static readonly IPAddress MulticastAddress = IPAddress.Parse(MulticastGroupIp);
    private static readonly Regex HamachiIpv4Regex = new(@"\b25\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);

    private readonly string _localInstanceId = Guid.NewGuid().ToString("N");
    private readonly object _hostLock = new();

    private Socket? _hostListenerSocket;
    private CancellationTokenSource? _hostBeaconCts;
    private string _hostName = Environment.MachineName;
    private int _hostNginxPort = 27000;
    private int _hostFileCount;
    private long _hostTotalModBytes;
    private string _hostHostingState = "Hosting";
    private List<NetworkEndpointInfo> _hostVpnEndpoints = new();
    private bool _periodicBroadcastEnabled;
    private Action<string>? _hostLogCallback;

    private readonly object _peerCacheLock = new();
    private DateTime _peerCacheExpiresUtc = DateTime.MinValue;
    private List<(string Ip, string Provider, string PeerName)> _cachedMeshPeers = new();

    public string LocalInstanceId => _localInstanceId;

    public bool IsHostBeaconRunning
    {
        get
        {
            lock (_hostLock)
            {
                return _hostBeaconCts != null && !_hostBeaconCts.IsCancellationRequested && _hostListenerSocket != null;
            }
        }
    }

    private sealed class VpnBeaconPayload
    {
        public string Magic { get; set; } = HostBeaconMagic;
        public string Type { get; set; } = "ANNOUNCE";
        public string InstanceId { get; set; } = "";
        public string HostName { get; set; } = "";
        public string PrimaryVpnIp { get; set; } = "";
        public List<string> VpnIps { get; set; } = new();
        public List<string> VpnProviders { get; set; } = new();
        public int NginxPort { get; set; } = 27000;
        public int FileCount { get; set; }
        public long TotalModBytes { get; set; }
        public string HostingState { get; set; } = "Hosting";
    }

    private sealed record SubnetRange(uint Network, uint Mask, int PrefixLength);

    private sealed class UdpDiscoveryCapture
    {
        public VpnBeaconPayload Payload { get; init; } = new();
        public string SenderIp { get; init; } = "";
        public int ObservedRttMs { get; set; } = -1;
    }

    private sealed class PeerCandidateCollection
    {
        public Dictionary<string, (string Provider, string HostName)> KnownOnlinePeers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> KnownOfflinePeers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (string Provider, string HostName)> UdpOnlyPeers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SmallSubnetSweepIps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SubnetBroadcastTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts the UDP 27001 Host discovery responder and optional 3-second periodic VPN broadcast loop.
    /// </summary>
    public void StartHostBeacon(
        string hostName,
        int nginxPort,
        IReadOnlyList<NetworkEndpointInfo>? endpoints,
        int fileCount,
        long totalModBytes,
        string hostingState,
        bool periodicBroadcastEnabled,
        Action<string>? logCallback = null)
    {
        StopHostBeacon();
        _ = Task.Run(EnsureDiscoveryFirewallRuleOnce);

        List<NetworkEndpointInfo> vpnList = FilterVpnEndpoints(endpoints);

        lock (_hostLock)
        {
            _hostName = string.IsNullOrWhiteSpace(hostName) ? Environment.MachineName : hostName.Trim();
            _hostNginxPort = nginxPort > 0 ? nginxPort : 27000;
            _hostVpnEndpoints = vpnList;
            _hostFileCount = Math.Max(0, fileCount);
            _hostTotalModBytes = Math.Max(0, totalModBytes);
            _hostHostingState = string.IsNullOrWhiteSpace(hostingState) ? "Hosting" : hostingState;
            _periodicBroadcastEnabled = periodicBroadcastEnabled;
            _hostLogCallback = logCallback;

            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.EnableBroadcast = true;
                DisableUdpConnReset(socket);
                socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryUdpPort));
                JoinMulticastOnVpnInterfaces(socket, vpnList);

                _hostListenerSocket = socket;
                _hostBeaconCts = new CancellationTokenSource();
                var token = _hostBeaconCts.Token;

                _ = Task.Run(() => HostResponderLoopAsync(socket, token), token);
                _ = Task.Run(() => HostBroadcastLoopAsync(token), token);

                if (vpnList.Count > 0)
                {
                    string adapters = string.Join(", ", vpnList.Select(v => $"{(string.IsNullOrWhiteSpace(v.VpnProvider) ? "VPN" : v.VpnProvider)}:{v.IpAddress}"));
                    logCallback?.Invoke(periodicBroadcastEnabled
                        ? $"[VPN-DISC] Host VPN Broadcast & Discovery Responder ONLINE on UDP {DiscoveryUdpPort} ({adapters})."
                        : $"[VPN-DISC] Host Discovery Responder listening on UDP {DiscoveryUdpPort} ({adapters}).");
                }
                else
                {
                    logCallback?.Invoke($"[VPN-DISC] Host Discovery Responder listening on UDP {DiscoveryUdpPort} (Standby — no active VPN adapter detected).");
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[WARN] Could not start VPN Discovery UDP {DiscoveryUdpPort} responder: {ex.Message}");
                try { _hostListenerSocket?.Dispose(); } catch { }
                _hostListenerSocket = null;
                try { _hostBeaconCts?.Dispose(); } catch { }
                _hostBeaconCts = null;
            }
        }
    }

    /// <summary>
    /// Updates hosted port, VPN endpoints, file counts, and periodic broadcast state without restarting the UDP listener.
    /// </summary>
    public void UpdateHostBeaconMetadata(
        int nginxPort,
        IReadOnlyList<NetworkEndpointInfo>? endpoints,
        int fileCount,
        long totalModBytes,
        string hostingState,
        bool periodicBroadcastEnabled,
        string? hostName = null)
    {
        List<NetworkEndpointInfo> vpnList = FilterVpnEndpoints(endpoints);
        lock (_hostLock)
        {
            if (!string.IsNullOrWhiteSpace(hostName))
                _hostName = hostName.Trim();
            if (nginxPort > 0)
                _hostNginxPort = nginxPort;
            if (endpoints != null)
            {
                _hostVpnEndpoints = vpnList;
                if (_hostListenerSocket != null)
                {
                    JoinMulticastOnVpnInterfaces(_hostListenerSocket, vpnList);
                }
            }
            _hostFileCount = Math.Max(0, fileCount);
            _hostTotalModBytes = Math.Max(0, totalModBytes);
            if (!string.IsNullOrWhiteSpace(hostingState))
                _hostHostingState = hostingState;
            _periodicBroadcastEnabled = periodicBroadcastEnabled;
        }
    }

    public void SetPeriodicBroadcastEnabled(bool enabled, IReadOnlyList<NetworkEndpointInfo>? endpoints = null)
    {
        List<NetworkEndpointInfo>? vpnList = endpoints != null ? FilterVpnEndpoints(endpoints) : null;
        lock (_hostLock)
        {
            _periodicBroadcastEnabled = enabled;
            if (vpnList != null)
            {
                _hostVpnEndpoints = vpnList;
            }
        }
    }

    public void StopHostBeacon()
    {
        CancellationTokenSource? cts;
        Socket? socket;
        lock (_hostLock)
        {
            cts = _hostBeaconCts;
            _hostBeaconCts = null;
            socket = _hostListenerSocket;
            _hostListenerSocket = null;
        }

        try { cts?.Cancel(); } catch { }
        try { socket?.Close(); } catch { }
        try { socket?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private async Task HostResponderLoopAsync(Socket socket, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        EndPoint anyRemote = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, anyRemote, token);
                if (result.ReceivedBytes <= 0 || result.RemoteEndPoint is not IPEndPoint remoteEp)
                    continue;

                string text = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                if (!text.Contains(DiscoverMagic, StringComparison.Ordinal))
                    continue;

                VpnBeaconPayload payload = BuildHostPayloadSnapshot("RESPONSE", remoteEp.Address);
                byte[] respBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                await socket.SendToAsync(new ArraySegment<byte>(respBytes), SocketFlags.None, remoteEp, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted || ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }
            catch
            {
                await Task.Delay(50, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task HostBroadcastLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool shouldBroadcast;
                List<NetworkEndpointInfo> vpnSnapshot;
                lock (_hostLock)
                {
                    shouldBroadcast = _periodicBroadcastEnabled;
                    vpnSnapshot = _hostVpnEndpoints.ToList();
                }

                if (shouldBroadcast && vpnSnapshot.Count > 0)
                {
                    await BroadcastAnnounceOnceAsync(vpnSnapshot, token);
                }

                await Task.Delay(3000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(3000, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task BroadcastAnnounceOnceAsync(List<NetworkEndpointInfo> vpnEndpoints, CancellationToken token)
    {
        var meshPeers = await GetCachedMeshOnlinePeersAsync(vpnEndpoints, token);
        byte[] defaultBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(BuildHostPayloadSnapshot("ANNOUNCE", IPAddress.Any)));

        foreach (var ep in vpnEndpoints)
        {
            if (token.IsCancellationRequested) break;
            if (!IPAddress.TryParse(ep.IpAddress, out var localVpnIp)) continue;

            using var sender = CreateBoundVpnUdpSocket(localVpnIp, ep.InterfaceIndex);
            if (sender == null) continue;

            byte[] scopedBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(BuildHostPayloadSnapshot("ANNOUNCE", localVpnIp)));
            var targets = new HashSet<IPAddress>();

            var directedBcast = ComputeDirectedBroadcast(localVpnIp, ep.PrefixLength);
            if (directedBcast != null) targets.Add(directedBcast);
            if (ep.IpAddress.StartsWith("26.", StringComparison.Ordinal)) targets.Add(IPAddress.Parse("26.255.255.255"));
            if (ep.IpAddress.StartsWith("25.", StringComparison.Ordinal)) targets.Add(IPAddress.Parse("25.255.255.255"));
            targets.Add(MulticastAddress);
            targets.Add(IPAddress.Broadcast);

            foreach (var peer in meshPeers)
            {
                if (IPAddress.TryParse(peer.Ip, out var peerIp) && !Equals(peerIp, localVpnIp) && DoesPeerMatchVpnEndpoint(peerIp, ep))
                {
                    targets.Add(peerIp);
                }
            }

            foreach (var targetIp in targets)
            {
                try
                {
                    byte[] payloadToSend = targetIp.Equals(MulticastAddress) ? defaultBytes : scopedBytes;
                    await sender.SendToAsync(new ArraySegment<byte>(payloadToSend), SocketFlags.None, new IPEndPoint(targetIp, DiscoveryUdpPort), token);
                }
                catch
                {
                    // Individual L3 adapters may disallow 255.255.255.255 or multicast
                }
            }
        }
    }

    private VpnBeaconPayload BuildHostPayloadSnapshot(string type, IPAddress remoteAddr)
    {
        lock (_hostLock)
        {
            string primaryIp = PickBestHostVpnIpForRemote(remoteAddr, _hostVpnEndpoints);
            return new VpnBeaconPayload
            {
                Magic = HostBeaconMagic,
                Type = type,
                InstanceId = _localInstanceId,
                HostName = _hostName,
                PrimaryVpnIp = primaryIp,
                VpnIps = _hostVpnEndpoints.Select(e => e.IpAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                VpnProviders = _hostVpnEndpoints
                    .Select(e => string.IsNullOrWhiteSpace(e.VpnProvider) ? "VPN" : e.VpnProvider)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                NginxPort = _hostNginxPort,
                FileCount = _hostFileCount,
                TotalModBytes = _hostTotalModBytes,
                HostingState = _hostHostingState
            };
        }
    }

    private static string PickBestHostVpnIpForRemote(IPAddress remoteAddr, List<NetworkEndpointInfo> vpnEndpoints)
    {
        if (vpnEndpoints.Count == 0)
            return IPAddress.IsLoopback(remoteAddr) ? "127.0.0.1" : "";

        if (IPAddress.IsLoopback(remoteAddr))
            return vpnEndpoints[0].IpAddress;

        foreach (var ep in vpnEndpoints)
        {
            if (!IPAddress.TryParse(ep.IpAddress, out var localIp)) continue;
            if (IsInSameSubnet(remoteAddr, localIp, ep.PrefixLength))
                return ep.IpAddress;
        }

        if (NetworkDiscoveryService.IsRfc6598CgnatIp(remoteAddr))
        {
            var rfc6598 = vpnEndpoints.FirstOrDefault(e =>
                IPAddress.TryParse(e.IpAddress, out var ip) && NetworkDiscoveryService.IsRfc6598CgnatIp(ip));
            if (rfc6598 != null) return rfc6598.IpAddress;
        }

        byte[] rb = remoteAddr.GetAddressBytes();
        if (rb.Length == 4 && rb[0] == 26)
        {
            var radmin = vpnEndpoints.FirstOrDefault(e => e.IpAddress.StartsWith("26.", StringComparison.Ordinal));
            if (radmin != null) return radmin.IpAddress;
        }
        if (rb.Length == 4 && rb[0] == 25)
        {
            var hamachi = vpnEndpoints.FirstOrDefault(e => e.IpAddress.StartsWith("25.", StringComparison.Ordinal));
            if (hamachi != null) return hamachi.IpAddress;
        }

        return vpnEndpoints[0].IpAddress;
    }

    /// <summary>
    /// Discovers online BG3ModSync hosts across all active VPN networks using passive/active UDP 27001 discovery,
    /// VPN mesh/ARP/route peer enumeration (never touching LAN), and bounded single-port HTTP metadata verification.
    /// </summary>
    public async Task<List<VpnDiscoveredHostInfo>> DiscoverVpnHostsAsync(
        int configuredNginxPort,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default,
        bool includeLoopbackForTest = false)
    {
        int defaultPort = configuredNginxPort > 0 ? configuredNginxPort : 27000;
        var allLocal = NetworkDiscoveryService.GetLocalAndVpnInterfaces(defaultPort);
        var lanEndpoints = allLocal.Where(e => e.Category == NetworkCategory.LAN).ToList();
        var vpnEndpoints = allLocal.Where(e => e.Category == NetworkCategory.VPN).ToList();

        if (vpnEndpoints.Count == 0 && !includeLoopbackForTest)
        {
            return new List<VpnDiscoveredHostInfo>();
        }

        List<SubnetRange> lanExclusionSubnets = BuildLanExclusionSubnets(lanEndpoints);
        HashSet<string> ownLocalIps = allLocal
            .Select(e => e.IpAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        PeerCandidateCollection candidates = await GatherVpnPeerCandidatesAsync(vpnEndpoints, lanExclusionSubnets, ownLocalIps, cancellationToken);

        var udpCaptures = new ConcurrentDictionary<string, UdpDiscoveryCapture>(StringComparer.OrdinalIgnoreCase);
        using var discoveryWindowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var listenSockets = new List<(Socket Socket, NetworkEndpointInfo? Endpoint, bool IsPassiveGlobal)>();
        var receiverTasks = new List<Task>();
        long probeStartTicks = Stopwatch.GetTimestamp();

        try
        {
            // Passive global listener catches 3-second periodic ANNOUNCE broadcasts on 0.0.0.0:27001
            // ONLY when UDP 27001 is not already bound locally (avoids stealing unicast DISCOVER packets on Windows SO_REUSEADDR)
            bool isPortAlreadyBound = IsDiscoveryPortBoundLocally();
            if (!IsHostBeaconRunning && !isPortAlreadyBound)
            {
                var passiveSocket = CreatePassiveDiscoverySocket(vpnEndpoints);
                if (passiveSocket != null)
                {
                    listenSockets.Add((passiveSocket, null, true));
                    receiverTasks.Add(ReceiveDiscoveryRepliesAsync(passiveSocket, udpCaptures, probeStartTicks, discoveryWindowCts.Token));
                }
            }

            var activeProbeSpecs = new List<NetworkEndpointInfo>(vpnEndpoints);
            if (includeLoopbackForTest || IsHostBeaconRunning || isPortAlreadyBound)
            {
                activeProbeSpecs.Add(new NetworkEndpointInfo
                {
                    Category = NetworkCategory.VPN,
                    AdapterName = "LoopbackTest",
                    IpAddress = "127.0.0.1",
                    PrefixLength = 8,
                    InterfaceIndex = -1,
                    VpnProvider = "VPN"
                });
            }

            foreach (var ep in activeProbeSpecs)
            {
                if (!IPAddress.TryParse(ep.IpAddress, out var bindIp)) continue;
                var sock = CreateBoundVpnUdpSocket(bindIp, ep.InterfaceIndex);
                if (sock == null) continue;

                listenSockets.Add((sock, ep, false));
                receiverTasks.Add(ReceiveDiscoveryRepliesAsync(sock, udpCaptures, probeStartTicks, discoveryWindowCts.Token));
            }

            byte[] discoverBytes = Encoding.UTF8.GetBytes(DiscoverMagic);
            var boundSenders = listenSockets.Where(x => !x.IsPassiveGlobal && x.Endpoint != null).ToList();

            // Send discovery wave 1 (t = 0) and wave 2 (t = 350ms) for UDP reliability
            for (int wave = 0; wave < 2; wave++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                probeStartTicks = Stopwatch.GetTimestamp();

                foreach (var (sock, ep, _) in boundSenders)
                {
                    await SendDiscoveryWaveOnInterfaceAsync(sock, ep!, candidates, discoverBytes, cancellationToken);
                }

                await Task.Delay(wave == 0 ? 350 : 750, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            try { discoveryWindowCts.Cancel(); } catch { }
            foreach (var (sock, _, _) in listenSockets)
            {
                try { sock.Close(); } catch { }
                try { sock.Dispose(); } catch { }
            }
            try { await Task.WhenAll(receiverTasks); } catch { }
        }

        // Verify UDP responders + up to MaxHttpVerifyPeers known-online VPN peers via single-port HTTP
        var hostsByKey = await VerifyAndMergeDiscoveredHostsAsync(
            udpCaptures,
            candidates,
            vpnEndpoints,
            ownLocalIps,
            defaultPort,
            includeLoopbackForTest,
            cancellationToken);

        var sorted = hostsByKey.Values
            .OrderBy(h => h.IsLocalSelf ? 1 : 0)
            .ThenByDescending(h => h.VerifiedViaHttp)
            .ThenBy(h => h.PingMs >= 0 ? h.PingMs : int.MaxValue)
            .ThenBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return sorted;
    }

    private async Task SendDiscoveryWaveOnInterfaceAsync(
        Socket socket,
        NetworkEndpointInfo ep,
        PeerCandidateCollection candidates,
        byte[] discoverBytes,
        CancellationToken token)
    {
        if (!IPAddress.TryParse(ep.IpAddress, out var localIp)) return;

        var targets = new HashSet<IPAddress>();
        bool isLoopback = IPAddress.IsLoopback(localIp);

        if (isLoopback)
        {
            targets.Add(IPAddress.Loopback);
        }
        else
        {
            // Self VPN IP ensures Host + Client on the same PC always finds (This PC) cleanly over Wintun
            targets.Add(localIp);

            var directedBcast = ComputeDirectedBroadcast(localIp, ep.PrefixLength);
            if (directedBcast != null) targets.Add(directedBcast);
            if (ep.IpAddress.StartsWith("26.", StringComparison.Ordinal)) targets.Add(IPAddress.Parse("26.255.255.255"));
            if (ep.IpAddress.StartsWith("25.", StringComparison.Ordinal)) targets.Add(IPAddress.Parse("25.255.255.255"));
            targets.Add(MulticastAddress);
            targets.Add(IPAddress.Broadcast);

            foreach (string rawBcast in candidates.SubnetBroadcastTargets)
            {
                if (IPAddress.TryParse(rawBcast, out var bIp)) targets.Add(bIp);
            }

            foreach (string peerStr in candidates.KnownOnlinePeers.Keys)
            {
                if (IPAddress.TryParse(peerStr, out var peerIp) && DoesPeerMatchVpnEndpoint(peerIp, ep))
                    targets.Add(peerIp);
            }

            foreach (string peerStr in candidates.UdpOnlyPeers.Keys)
            {
                if (IPAddress.TryParse(peerStr, out var peerIp) && DoesPeerMatchVpnEndpoint(peerIp, ep))
                    targets.Add(peerIp);
            }

            foreach (string sweepStr in candidates.SmallSubnetSweepIps)
            {
                if (IPAddress.TryParse(sweepStr, out var sweepIp) && DoesPeerMatchVpnEndpoint(sweepIp, ep))
                    targets.Add(sweepIp);
            }
        }

        foreach (var targetIp in targets)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                await socket.SendToAsync(new ArraySegment<byte>(discoverBytes), SocketFlags.None, new IPEndPoint(targetIp, DiscoveryUdpPort), token);
            }
            catch
            {
                // Ignore unsupported broadcast/multicast errors on L3 TUN drivers
            }
        }
    }

    private async Task ReceiveDiscoveryRepliesAsync(
        Socket socket,
        ConcurrentDictionary<string, UdpDiscoveryCapture> captures,
        long probeStartTicks,
        CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        EndPoint anyRemote = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            try
            {
                var res = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, anyRemote, token);
                if (res.ReceivedBytes <= 0 || res.RemoteEndPoint is not IPEndPoint remoteEp)
                    continue;

                string json = Encoding.UTF8.GetString(buffer, 0, res.ReceivedBytes);
                if (!json.Contains(HostBeaconMagic, StringComparison.Ordinal))
                    continue;

                var payload = JsonSerializer.Deserialize<VpnBeaconPayload>(json);
                if (payload == null || !string.Equals(payload.Magic, HostBeaconMagic, StringComparison.Ordinal))
                    continue;

                string ipKey = !string.IsNullOrWhiteSpace(payload.PrimaryVpnIp)
                    ? payload.PrimaryVpnIp.Trim()
                    : remoteEp.Address.ToString();

                int elapsedMs = (int)Math.Max(0, Stopwatch.GetElapsedTime(probeStartTicks).TotalMilliseconds);
                var capture = new UdpDiscoveryCapture
                {
                    Payload = payload,
                    SenderIp = remoteEp.Address.ToString(),
                    ObservedRttMs = string.Equals(payload.Type, "RESPONSE", StringComparison.OrdinalIgnoreCase)
                        ? Math.Max(1, elapsedMs)
                        : -1
                };

                captures.AddOrUpdate(
                    ipKey,
                    capture,
                    (_, existing) =>
                    {
                        if (capture.ObservedRttMs < 0 && existing.ObservedRttMs >= 0)
                            capture.ObservedRttMs = existing.ObservedRttMs;
                        return capture;
                    });

                if (payload.VpnIps != null &&
                    payload.VpnIps.Count > 1 &&
                    (IPAddress.IsLoopback(remoteEp.Address) || remoteEp.Address.Equals(MulticastAddress)))
                {
                    foreach (string extraIpRaw in payload.VpnIps)
                    {
                        string extraIp = extraIpRaw?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(extraIp) ||
                            string.Equals(extraIp, ipKey, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var extraCapture = new UdpDiscoveryCapture
                        {
                            Payload = payload,
                            SenderIp = capture.SenderIp,
                            ObservedRttMs = capture.ObservedRttMs
                        };
                        captures.AddOrUpdate(
                            extraIp,
                            extraCapture,
                            (_, existing) =>
                            {
                                if (extraCapture.ObservedRttMs < 0 && existing.ObservedRttMs >= 0)
                                    extraCapture.ObservedRttMs = existing.ObservedRttMs;
                                return extraCapture;
                            });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted || ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }
            catch
            {
                await Task.Delay(25, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task<Dictionary<string, VpnDiscoveredHostInfo>> VerifyAndMergeDiscoveredHostsAsync(
        ConcurrentDictionary<string, UdpDiscoveryCapture> udpCaptures,
        PeerCandidateCollection candidates,
        List<NetworkEndpointInfo> vpnEndpoints,
        HashSet<string> ownLocalIps,
        int defaultPort,
        bool includeLoopbackForTest,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, VpnDiscoveredHostInfo>(StringComparer.OrdinalIgnoreCase);

        // Plain HTTP handler WITHOUT physical-LAN socket binding so WireGuard/OpenVPN 10.x/192.168.x routes stay on VPN
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(700),
            PooledConnectionLifetime = TimeSpan.Zero,
            UseProxy = false
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(850) };

        var targets = new Dictionary<string, (int Port, UdpDiscoveryCapture? Udp, string PeerProvider, string PeerName)>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in udpCaptures)
        {
            int port = kv.Value.Payload.NginxPort > 0 ? kv.Value.Payload.NginxPort : defaultPort;
            targets[kv.Key] = (port, kv.Value, "", "");
        }

        int extraAdded = 0;
        foreach (var kv in candidates.KnownOnlinePeers)
        {
            if (extraAdded >= MaxHttpVerifyPeers) break;
            if (!targets.ContainsKey(kv.Key))
            {
                targets[kv.Key] = (defaultPort, null, kv.Value.Provider, kv.Value.HostName);
                extraAdded++;
            }
        }

        if (includeLoopbackForTest && !targets.ContainsKey("127.0.0.1"))
        {
            targets["127.0.0.1"] = (defaultPort, null, "VPN", Environment.MachineName);
        }

        using var sem = new SemaphoreSlim(8);
        var tasks = targets.Select(async item =>
        {
            await sem.WaitAsync(cancellationToken);
            try
            {
                string ip = item.Key;
                int port = item.Value.Port;
                UdpDiscoveryCapture? udp = item.Value.Udp;

                (HostDirectEndpointsMetadata? meta, bool healthOk, int httpRttMs) =
                    await ProbeHostHttpMetadataAsync(http, ip, port, cancellationToken);

                if (udp == null && meta == null && !healthOk)
                    return;

                int pingMs = await SteamP2PRelayService.MeasureEndpointPingMsAsync(
                    $"{ip}:{port}",
                    null,
                    http,
                    cancellationToken,
                    allowTcpFallback: false);

                if (pingMs < 0)
                {
                    pingMs = httpRttMs >= 0
                        ? httpRttMs
                        : (udp != null && udp.ObservedRttMs >= 0 ? udp.ObservedRttMs : 1);
                }

                string hostName = !string.IsNullOrWhiteSpace(meta?.HostName)
                    ? meta!.HostName
                    : (!string.IsNullOrWhiteSpace(udp?.Payload.HostName)
                        ? udp!.Payload.HostName
                        : (!string.IsNullOrWhiteSpace(item.Value.PeerName) ? item.Value.PeerName : ip));

                string provider = ResolveProviderForPeerIp(ip, vpnEndpoints, item.Value.PeerProvider, udp?.Payload.VpnProviders, meta?.VpnProviders);
                bool verifiedHttp = meta != null || healthOk;
                bool discoveredUdp = udp != null;
                string method = (discoveredUdp, verifiedHttp) switch
                {
                    (true, true) => "UDP + HTTP",
                    (true, false) => "UDP Beacon",
                    (false, true) => "HTTP Probe",
                    _ => ""
                };

                bool isLocalSelf = (udp != null && string.Equals(udp.Payload.InstanceId, _localInstanceId, StringComparison.Ordinal)) ||
                                   ownLocalIps.Contains(ip) ||
                                   string.Equals(ip, "127.0.0.1", StringComparison.Ordinal);

                var info = new VpnDiscoveredHostInfo
                {
                    HostName = hostName,
                    VpnIp = ip,
                    NginxPort = meta?.NginxPort > 0 ? meta.NginxPort : port,
                    VpnProvider = provider,
                    FileCount = meta != null && meta.FileCount > 0 ? meta.FileCount : (udp?.Payload.FileCount ?? 0),
                    TotalModBytes = meta != null && meta.TotalModBytes > 0 ? meta.TotalModBytes : (udp?.Payload.TotalModBytes ?? 0),
                    HostingState = !string.IsNullOrWhiteSpace(meta?.HostingState)
                        ? meta!.HostingState
                        : (!string.IsNullOrWhiteSpace(udp?.Payload.HostingState) ? udp!.Payload.HostingState : "Hosting"),
                    PingMs = pingMs,
                    DiscoveredViaUdp = discoveredUdp,
                    VerifiedViaHttp = verifiedHttp,
                    IsLocalSelf = isLocalSelf,
                    DiscoveryMethod = method
                };

                results[ip] = info;
            }
            catch
            {
                // Ignore unreachable peer
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new Dictionary<string, VpnDiscoveredHostInfo>(results, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<(HostDirectEndpointsMetadata? Meta, bool HealthOk, int HttpRttMs)> ProbeHostHttpMetadataAsync(
        HttpClient http,
        string ip,
        int port,
        CancellationToken cancellationToken)
    {
        long startTicks = Stopwatch.GetTimestamp();
        try
        {
            using var infoResp = await http.GetAsync($"http://{ip}:{port}/host_info.json", cancellationToken);
            int rtt = Math.Max(1, (int)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds);
            if (infoResp.IsSuccessStatusCode)
            {
                string json = await infoResp.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(json) && json.Trim() != "{}")
                {
                    var meta = JsonSerializer.Deserialize<HostDirectEndpointsMetadata>(json);
                    if (meta != null && (!string.IsNullOrWhiteSpace(meta.HostName) || meta.NginxPort > 0 || meta.FileCount > 0))
                    {
                        return (meta, true, rtt);
                    }
                }
            }
        }
        catch
        {
            // Fall through to /health fallback (e.g. older host without /host_info.json)
        }

        try
        {
            long healthStart = Stopwatch.GetTimestamp();
            using var healthResp = await http.GetAsync($"http://{ip}:{port}/health", cancellationToken);
            int rtt = Math.Max(1, (int)Stopwatch.GetElapsedTime(healthStart).TotalMilliseconds);
            if (healthResp.IsSuccessStatusCode)
            {
                string body = await healthResp.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("BG3ModSync", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, true, rtt);
                }
            }
        }
        catch { }

        return (null, false, -1);
    }

    private async Task<List<(string Ip, string Provider, string PeerName)>> GetCachedMeshOnlinePeersAsync(
        List<NetworkEndpointInfo> vpnEndpoints,
        CancellationToken token)
    {
        lock (_peerCacheLock)
        {
            if (DateTime.UtcNow < _peerCacheExpiresUtc)
                return _cachedMeshPeers.ToList();
        }

        var list = new List<(string Ip, string Provider, string PeerName)>();
        bool hasTailscale = vpnEndpoints.Any(e => e.VpnProvider == "Tailscale");
        bool hasNetBird = vpnEndpoints.Any(e => e.VpnProvider == "NetBird");

        if (hasTailscale)
        {
            foreach (var p in await QueryTailscalePeersAsync(token))
            {
                if (p.Online) list.Add((p.Ip, "Tailscale", p.Name));
            }
        }
        if (hasNetBird)
        {
            foreach (var p in await QueryNetBirdPeersAsync(token))
            {
                if (p.Online) list.Add((p.Ip, "NetBird", p.Name));
            }
        }

        lock (_peerCacheLock)
        {
            _cachedMeshPeers = list;
            _peerCacheExpiresUtc = DateTime.UtcNow.AddSeconds(10);
            return _cachedMeshPeers.ToList();
        }
    }

    private async Task<PeerCandidateCollection> GatherVpnPeerCandidatesAsync(
        List<NetworkEndpointInfo> vpnEndpoints,
        List<SubnetRange> lanExclusionSubnets,
        HashSet<string> ownLocalIps,
        CancellationToken token)
    {
        var collection = new PeerCandidateCollection();
        HashSet<int> vpnIfIndices = vpnEndpoints
            .Where(e => e.InterfaceIndex > 0)
            .Select(e => e.InterfaceIndex)
            .ToHashSet();

        bool hasTailscale = vpnEndpoints.Any(e => e.VpnProvider == "Tailscale");
        bool hasNetBird = vpnEndpoints.Any(e => e.VpnProvider == "NetBird");
        bool hasZeroTier = vpnEndpoints.Any(e => e.VpnProvider == "ZeroTier");
        bool hasHamachi = vpnEndpoints.Any(e => e.VpnProvider == "Hamachi" || e.IpAddress.StartsWith("25.", StringComparison.Ordinal));

        var tsTask = hasTailscale ? QueryTailscalePeersAsync(token) : Task.FromResult(new List<(string Ip, string Name, bool Online)>());
        var nbTask = hasNetBird ? QueryNetBirdPeersAsync(token) : Task.FromResult(new List<(string Ip, string Name, bool Online)>());
        var ztTask = hasZeroTier ? QueryZeroTierManagedSubnetsAsync(token) : Task.FromResult(new List<SubnetRange>());

        await Task.WhenAll(tsTask, nbTask, ztTask);

        // 1. Tailscale peers (authoritative online/offline state)
        var tsOffline = new List<(string Ip, string Name)>();
        foreach (var p in tsTask.Result)
        {
            if (!IsValidVpnPeerCandidate(p.Ip, lanExclusionSubnets, ownLocalIps)) continue;
            if (p.Online)
            {
                collection.KnownOnlinePeers[p.Ip] = ("Tailscale", p.Name);
            }
            else
            {
                collection.KnownOfflinePeers.Add(p.Ip);
                tsOffline.Add((p.Ip, p.Name));
            }
        }
        if (tsOffline.Count <= 30)
        {
            foreach (var p in tsOffline)
            {
                collection.UdpOnlyPeers.TryAdd(p.Ip, ("Tailscale", p.Name));
            }
        }

        // 2. NetBird peers
        foreach (var p in nbTask.Result)
        {
            if (!IsValidVpnPeerCandidate(p.Ip, lanExclusionSubnets, ownLocalIps)) continue;
            if (p.Online)
                collection.KnownOnlinePeers[p.Ip] = ("NetBird", p.Name);
            else
                collection.KnownOfflinePeers.Add(p.Ip);
        }

        // 3. ZeroTier managed subnets (broadcast + small /24../30 UDP sweep if private and non-LAN)
        foreach (var subnet in ztTask.Result)
        {
            AddSmallSubnetTargets(subnet, lanExclusionSubnets, ownLocalIps, collection);
        }

        // 4. Windows ARP Table strictly filtered to [VPN] interface indices (catches Radmin, Hamachi, ZeroTier, TAP)
        foreach (string arpIp in QueryArpNeighborsForInterfaces(vpnIfIndices))
        {
            if (!IsValidVpnPeerCandidate(arpIp, lanExclusionSubnets, ownLocalIps)) continue;
            if (collection.KnownOfflinePeers.Contains(arpIp)) continue;
            string prov = GuessProviderByIpOrEndpoints(arpIp, vpnEndpoints);
            collection.KnownOnlinePeers.TryAdd(arpIp, (prov, ""));
        }

        // 5. Windows IPv4 Route Table strictly filtered to [VPN] interface indices (catches WireGuard /32, OpenVPN TUN)
        var (hostRoutes, smallSubnets) = QueryRoutePeersForInterfaces(vpnIfIndices);
        foreach (string routeIp in hostRoutes)
        {
            if (!IsValidVpnPeerCandidate(routeIp, lanExclusionSubnets, ownLocalIps)) continue;
            if (collection.KnownOfflinePeers.Contains(routeIp)) continue;
            string prov = GuessProviderByIpOrEndpoints(routeIp, vpnEndpoints);
            collection.KnownOnlinePeers.TryAdd(routeIp, (prov, ""));
        }
        foreach (var subnet in smallSubnets)
        {
            AddSmallSubnetTargets(subnet, lanExclusionSubnets, ownLocalIps, collection);
        }

        // Also check small non-100.64 subnets directly assigned to OpenVPN / WireGuard / ZeroTier adapters
        foreach (var ep in vpnEndpoints)
        {
            if (IPAddress.TryParse(ep.IpAddress, out var epIp) &&
                ep.PrefixLength >= 24 && ep.PrefixLength <= 30 &&
                !NetworkDiscoveryService.IsRfc6598CgnatIp(epIp) &&
                !ep.IpAddress.StartsWith("25.", StringComparison.Ordinal) &&
                !ep.IpAddress.StartsWith("26.", StringComparison.Ordinal))
            {
                uint mask = ~0u << (32 - ep.PrefixLength);
                uint net = IpToHostUInt32(epIp) & mask;
                AddSmallSubnetTargets(new SubnetRange(net, mask, ep.PrefixLength), lanExclusionSubnets, ownLocalIps, collection);
            }
        }

        // 6. VPN adapter gateway / DHCP peers + Hamachi peer cache files
        foreach (string gwIp in QueryVpnAdapterGatewayAndDhcpPeers(vpnIfIndices))
        {
            if (!IsValidVpnPeerCandidate(gwIp, lanExclusionSubnets, ownLocalIps)) continue;
            if (collection.KnownOfflinePeers.Contains(gwIp)) continue;
            collection.KnownOnlinePeers.TryAdd(gwIp, (GuessProviderByIpOrEndpoints(gwIp, vpnEndpoints), ""));
        }
        if (hasHamachi)
        {
            foreach (string hamIp in ReadHamachiPeerIpsBestEffort())
            {
                if (!IsValidVpnPeerCandidate(hamIp, lanExclusionSubnets, ownLocalIps)) continue;
                collection.KnownOnlinePeers.TryAdd(hamIp, ("Hamachi", ""));
            }
        }

        return collection;
    }

    private static async Task<List<(string Ip, string Name, bool Online)>> QueryTailscalePeersAsync(CancellationToken token)
    {
        var results = new List<(string Ip, string Name, bool Online)>();
        string? json = await RunCliJsonAsync(
            new[]
            {
                @"C:\Program Files\Tailscale\tailscale.exe",
                @"C:\Program Files (x86)\Tailscale\tailscale.exe",
                "tailscale.exe"
            },
            "status --json",
            2200,
            token);

        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Peer", out var peersObj) || peersObj.ValueKind != JsonValueKind.Object)
                return results;

            foreach (var prop in peersObj.EnumerateObject())
            {
                var peer = prop.Value;
                bool online = (peer.TryGetProperty("Online", out var onEl) && onEl.ValueKind == JsonValueKind.True) ||
                              (peer.TryGetProperty("Active", out var actEl) && actEl.ValueKind == JsonValueKind.True);

                string hostName = peer.TryGetProperty("HostName", out var hnEl) ? (hnEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(hostName) && peer.TryGetProperty("DNSName", out var dnsEl))
                {
                    hostName = (dnsEl.GetString() ?? "").TrimEnd('.').Split('.')[0];
                }

                if (peer.TryGetProperty("TailscaleIPs", out var ipsEl) && ipsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ipItem in ipsEl.EnumerateArray())
                    {
                        string? raw = ipItem.GetString();
                        if (!string.IsNullOrWhiteSpace(raw) &&
                            IPAddress.TryParse(raw, out var parsed) &&
                            parsed.AddressFamily == AddressFamily.InterNetwork)
                        {
                            results.Add((parsed.ToString(), hostName, online));
                            break;
                        }
                    }
                }
            }
        }
        catch { }

        return results;
    }

    private static async Task<List<(string Ip, string Name, bool Online)>> QueryNetBirdPeersAsync(CancellationToken token)
    {
        var results = new List<(string Ip, string Name, bool Online)>();
        string? json = await RunCliJsonAsync(
            new[]
            {
                @"C:\Program Files\NetBird\netbird.exe",
                @"C:\Program Files (x86)\NetBird\netbird.exe",
                "netbird.exe"
            },
            "status -d --json",
            2000,
            token);

        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("peers", out var peersObj) ||
                !peersObj.TryGetProperty("details", out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in details.EnumerateArray())
            {
                string rawIp = item.TryGetProperty("netbirdIp", out var ipEl) ? (ipEl.GetString() ?? "") : "";
                int slash = rawIp.IndexOf('/');
                if (slash > 0) rawIp = rawIp.Substring(0, slash);
                if (!IPAddress.TryParse(rawIp, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                string fqdn = item.TryGetProperty("fqdn", out var fqdnEl) ? (fqdnEl.GetString() ?? "") : "";
                string name = fqdn.Split('.')[0];
                string status = item.TryGetProperty("status", out var stEl) ? (stEl.GetString() ?? "") : "";
                bool online = status.Equals("Connected", StringComparison.OrdinalIgnoreCase);
                results.Add((parsed.ToString(), name, online));
            }
        }
        catch { }

        return results;
    }

    private static async Task<List<SubnetRange>> QueryZeroTierManagedSubnetsAsync(CancellationToken token)
    {
        var results = new List<SubnetRange>();
        string? authToken = TryReadZeroTierAuthToken();
        if (string.IsNullOrWhiteSpace(authToken)) return results;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9993/network");
            req.Headers.TryAddWithoutValidation("X-ZT1-Auth", authToken);

            using var resp = await http.SendAsync(req, token);
            if (!resp.IsSuccessStatusCode) return results;

            string json = await resp.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var netObj in doc.RootElement.EnumerateArray())
            {
                if (!netObj.TryGetProperty("assignedAddresses", out var addrsEl) || addrsEl.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in addrsEl.EnumerateArray())
                {
                    string? cidr = item.GetString();
                    if (TryParseCidr(cidr, out var subnet) && subnet != null)
                    {
                        results.Add(subnet);
                    }
                }
            }
        }
        catch { }

        return results;
    }

    private static string? TryReadZeroTierAuthToken()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string[] candidates =
        {
            Path.Combine(localApp, "ZeroTier", "authtoken.secret"),
            Path.Combine(localApp, "ZeroTier", "One", "authtoken.secret"),
            Path.Combine(progData, "ZeroTier", "One", "authtoken.secret")
        };

        foreach (string path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    string token = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(token)) return token;
                }
            }
            catch { }
        }
        return null;
    }

    private static async Task<string?> RunCliJsonAsync(string[] exeCandidates, string args, int timeoutMs, CancellationToken token)
    {
        foreach (string candidate in exeCandidates)
        {
            bool isAbsolute = Path.IsPathRooted(candidate);
            if (isAbsolute && !File.Exists(candidate)) continue;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) continue;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(timeoutMs);

                Task<string> readTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await proc.WaitForExitAsync(timeoutCts.Token);
                string output = await readTask;
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    return output;
            }
            catch
            {
                // Try next executable path
            }
        }
        return null;
    }

    #region Win32 IP Helper (ARP + Routes)

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] bPhysAddr;
        public uint dwAddr;
        public int dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARDROW
    {
        public uint dwForwardDest;
        public uint dwForwardMask;
        public int dwForwardPolicy;
        public uint dwForwardNextHop;
        public int dwForwardIfIndex;
        public int dwForwardType;
        public int dwForwardProto;
        public int dwForwardAge;
        public int dwForwardNextHopAS;
        public int dwForwardMetric1;
        public int dwForwardMetric2;
        public int dwForwardMetric3;
        public int dwForwardMetric4;
        public int dwForwardMetric5;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

    private static List<string> QueryArpNeighborsForInterfaces(HashSet<int> vpnIfIndices)
    {
        var results = new List<string>();
        if (vpnIfIndices.Count == 0) return results;

        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 4) return results;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buf, ref size, false) != 0)
                return results;

            int count = Marshal.ReadInt32(buf);
            IntPtr rowPtr = IntPtr.Add(buf, 4);
            int rowSize = Marshal.SizeOf<MIB_IPNETROW>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPNETROW>(IntPtr.Add(rowPtr, i * rowSize));
                if (!vpnIfIndices.Contains(row.dwIndex)) continue;
                if (row.dwType != 3 && row.dwType != 4) continue; // Dynamic or Static only

                var ip = new IPAddress((long)row.dwAddr);
                if (ip.Equals(IPAddress.Broadcast) || ip.Equals(IPAddress.Any)) continue;
                byte[] b = ip.GetAddressBytes();
                if (b[0] >= 224 || b[3] == 255 || b[3] == 0) continue; // Skip multicast / subnet-end broadcast

                results.Add(ip.ToString());
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return results;
    }

    private static (List<string> HostRoutes, List<SubnetRange> SmallSubnets) QueryRoutePeersForInterfaces(HashSet<int> vpnIfIndices)
    {
        var hostRoutes = new List<string>();
        var smallSubnets = new List<SubnetRange>();
        if (vpnIfIndices.Count == 0) return (hostRoutes, smallSubnets);

        int size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (size <= 4) return (hostRoutes, smallSubnets);

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buf, ref size, false) != 0)
                return (hostRoutes, smallSubnets);

            int count = Marshal.ReadInt32(buf);
            IntPtr rowPtr = IntPtr.Add(buf, 4);
            int rowSize = Marshal.SizeOf<MIB_IPFORWARDROW>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPFORWARDROW>(IntPtr.Add(rowPtr, i * rowSize));
                if (!vpnIfIndices.Contains(row.dwForwardIfIndex)) continue;

                var destIp = new IPAddress((long)row.dwForwardDest);
                var maskIp = new IPAddress((long)row.dwForwardMask);
                if (destIp.AddressFamily != AddressFamily.InterNetwork || maskIp.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                byte[] db = destIp.GetAddressBytes();
                if (db[0] >= 224 || db[0] == 127 || db[0] == 0) continue;

                if (row.dwForwardMask == 0xFFFFFFFFu)
                {
                    if (db[3] != 255) // Skip Windows local directed-broadcast /32 route entries
                    {
                        hostRoutes.Add(destIp.ToString());
                    }
                    continue;
                }

                int prefix = CountMaskBits(maskIp);
                if (prefix >= 24 && prefix <= 30)
                {
                    uint netHost = IpToHostUInt32(destIp);
                    uint maskHost = IpToHostUInt32(maskIp);
                    smallSubnets.Add(new SubnetRange(netHost & maskHost, maskHost, prefix));
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return (hostRoutes, smallSubnets);
    }

    private static List<string> QueryVpnAdapterGatewayAndDhcpPeers(HashSet<int> vpnIfIndices)
    {
        var peers = new List<string>();
        if (vpnIfIndices.Count == 0) return peers;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    var props = ni.GetIPProperties();
                    int idx = props.GetIPv4Properties()?.Index ?? -1;
                    if (!vpnIfIndices.Contains(idx)) continue;

                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !gw.Address.Equals(IPAddress.Any))
                            peers.Add(gw.Address.ToString());
                    }
                    foreach (var dhcp in props.DhcpServerAddresses)
                    {
                        if (dhcp.AddressFamily == AddressFamily.InterNetwork && !dhcp.Equals(IPAddress.Any))
                            peers.Add(dhcp.ToString());
                    }
                }
                catch { }
            }
        }
        catch { }
        return peers;
    }

    private static List<string> ReadHamachiPeerIpsBestEffort()
    {
        var results = new List<string>();
        try
        {
            string hamDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogMeIn Hamachi");
            if (!Directory.Exists(hamDir)) return results;

            foreach (string file in Directory.EnumerateFiles(hamDir, "*.*")
                         .Where(f => f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > 2 * 1024 * 1024) continue;

                    string content = File.ReadAllText(file);
                    foreach (Match m in HamachiIpv4Regex.Matches(content))
                    {
                        if (IPAddress.TryParse(m.Value, out var ip))
                            results.Add(ip.ToString());
                    }
                }
                catch { }
            }
        }
        catch { }
        return results;
    }

    #endregion

    #region Socket & IP Helpers

    private static bool IsDiscoveryPortBoundLocally()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Any(ep => ep.Port == DiscoveryUdpPort);
        }
        catch
        {
            return false;
        }
    }

    private static Socket? CreatePassiveDiscoverySocket(List<NetworkEndpointInfo> vpnEndpoints)
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.ExclusiveAddressUse = true;
            s.EnableBroadcast = true;
            DisableUdpConnReset(s);
            s.Bind(new IPEndPoint(IPAddress.Any, DiscoveryUdpPort));
            JoinMulticastOnVpnInterfaces(s, vpnEndpoints);
            return s;
        }
        catch
        {
            return null;
        }
    }

    private static Socket? CreateBoundVpnUdpSocket(IPAddress localVpnIp, int interfaceIndex)
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.EnableBroadcast = true;
            DisableUdpConnReset(s);
            if (interfaceIndex > 0)
            {
                try
                {
                    int netOrderIdx = IPAddress.HostToNetworkOrder(interfaceIndex);
                    s.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_UNICAST_IF, netOrderIdx);
                    s.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_MULTICAST_IF, netOrderIdx);
                }
                catch { }
            }
            s.Bind(new IPEndPoint(localVpnIp, 0));
            return s;
        }
        catch
        {
            return null;
        }
    }

    private static void JoinMulticastOnVpnInterfaces(Socket socket, List<NetworkEndpointInfo> vpnEndpoints)
    {
        foreach (var ep in vpnEndpoints)
        {
            try
            {
                if (IPAddress.TryParse(ep.IpAddress, out var vpnIp))
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(MulticastAddress, vpnIp));
                }
            }
            catch
            {
                // Ignore L3 adapters without multicast support
            }
        }
    }

    private static void DisableUdpConnReset(Socket socket)
    {
        try
        {
            socket.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[4], null);
        }
        catch { }
    }

    private static void EnsureDiscoveryFirewallRuleOnce()
    {
        if (Interlocked.Exchange(ref _firewallRuleEnsured, 1) != 0)
            return;

        try
        {
            static int RunNetsh(string args)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return -1;
                proc.WaitForExit(1800);
                return proc.HasExited ? proc.ExitCode : -1;
            }

            if (RunNetsh("advfirewall firewall show rule name=\"BG3ModSync VPN Discovery (UDP)\"") != 0)
            {
                RunNetsh($"advfirewall firewall add rule name=\"BG3ModSync VPN Discovery (UDP)\" dir=in action=allow protocol=UDP localport={DiscoveryUdpPort} profile=any");
            }
        }
        catch
        {
            // Non-fatal without admin elevation
        }
    }

    private static List<NetworkEndpointInfo> FilterVpnEndpoints(IReadOnlyList<NetworkEndpointInfo>? endpoints)
    {
        return endpoints?
            .Where(e => e.Category == NetworkCategory.VPN && !string.IsNullOrWhiteSpace(e.IpAddress))
            .ToList()
            ?? new List<NetworkEndpointInfo>();
    }

    private static List<SubnetRange> BuildLanExclusionSubnets(List<NetworkEndpointInfo> lanEndpoints)
    {
        var list = new List<SubnetRange>();
        foreach (var lan in lanEndpoints)
        {
            if (!IPAddress.TryParse(lan.IpAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                continue;
            int prefix = lan.PrefixLength is >= 8 and <= 32 ? lan.PrefixLength : 24;
            uint mask = ~0u << (32 - prefix);
            uint net = IpToHostUInt32(ip) & mask;
            list.Add(new SubnetRange(net, mask, prefix));
        }
        return list;
    }

    private static void AddSmallSubnetTargets(
        SubnetRange subnet,
        List<SubnetRange> lanExclusions,
        HashSet<string> ownLocalIps,
        PeerCandidateCollection collection)
    {
        // Strictly reject any subnet that overlaps a physical LAN interface!
        if (DoesSubnetOverlapAny(subnet, lanExclusions))
            return;

        uint firstOctet = subnet.Network >> 24;
        uint bcastHost = subnet.Network | ~subnet.Mask;
        collection.SubnetBroadcastTargets.Add(HostUInt32ToIp(bcastHost).ToString());

        // Never unicast-sweep CGNAT 100.64/10, Radmin 26.x, or Hamachi 25.x; only small private /24../30 VPN subnets
        IPAddress netIp = HostUInt32ToIp(subnet.Network);
        if (NetworkDiscoveryService.IsRfc6598CgnatIp(netIp) || firstOctet == 25 || firstOctet == 26)
            return;

        if (subnet.PrefixLength < 24 || subnet.PrefixLength > 30)
            return;

        for (uint addr = subnet.Network + 1; addr < bcastHost; addr++)
        {
            if (collection.SmallSubnetSweepIps.Count >= MaxSmallSubnetSweepHosts)
                break;

            string ipStr = HostUInt32ToIp(addr).ToString();
            if (!ownLocalIps.Contains(ipStr))
            {
                collection.SmallSubnetSweepIps.Add(ipStr);
            }
        }
    }

    private static bool DoesSubnetOverlapAny(SubnetRange target, List<SubnetRange> exclusions)
    {
        foreach (var ex in exclusions)
        {
            uint smallerMask = target.Mask & ex.Mask;
            if ((target.Network & smallerMask) == (ex.Network & smallerMask))
                return true;
        }
        return false;
    }

    private static bool IsValidVpnPeerCandidate(
        string ipStr,
        List<SubnetRange> lanExclusions,
        HashSet<string> ownLocalIps)
    {
        if (string.IsNullOrWhiteSpace(ipStr) || ownLocalIps.Contains(ipStr))
            return false;

        if (!IPAddress.TryParse(ipStr, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast))
            return false;

        byte[] b = ip.GetAddressBytes();
        if (b[0] >= 224 || b[0] == 0) return false; // Multicast / 0.x
        if (b[0] == 169 && b[1] == 254) return false; // APIPA

        uint ipHost = IpToHostUInt32(ip);
        foreach (var lan in lanExclusions)
        {
            if ((ipHost & lan.Mask) == lan.Network)
                return false;
        }

        return true;
    }

    private static bool DoesPeerMatchVpnEndpoint(IPAddress peerIp, NetworkEndpointInfo ep)
    {
        if (!IPAddress.TryParse(ep.IpAddress, out var localIp)) return false;

        if (NetworkDiscoveryService.IsRfc6598CgnatIp(localIp) && NetworkDiscoveryService.IsRfc6598CgnatIp(peerIp))
            return true;

        byte[] lb = localIp.GetAddressBytes();
        byte[] pb = peerIp.GetAddressBytes();
        if (lb[0] == 26 && pb[0] == 26) return true;
        if (lb[0] == 25 && pb[0] == 25) return true;

        int prefix = ep.PrefixLength is >= 8 and <= 30 ? ep.PrefixLength : 24;
        if (IsInSameSubnet(peerIp, localIp, prefix))
            return true;

        // WireGuard / OpenVPN point-to-point /32 tunnel interface can route private 10.x / 172.16.x / 192.168.x peers
        if (ep.PrefixLength == 32 && NetworkDiscoveryService.IsPrivateRfc1918Ip(peerIp))
            return true;

        return false;
    }

    private static string ResolveProviderForPeerIp(
        string peerIpStr,
        List<NetworkEndpointInfo> vpnEndpoints,
        string explicitPeerProvider,
        List<string>? udpProviders,
        List<string>? httpProviders)
    {
        if (!string.IsNullOrWhiteSpace(explicitPeerProvider) && explicitPeerProvider != "VPN")
            return explicitPeerProvider;

        string guessed = GuessProviderByIpOrEndpoints(peerIpStr, vpnEndpoints);
        if (!string.IsNullOrWhiteSpace(guessed) && guessed != "VPN")
            return guessed;

        string? remoteProv = httpProviders?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p != "VPN")
                             ?? udpProviders?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p != "VPN");
        if (!string.IsNullOrWhiteSpace(remoteProv))
            return remoteProv;

        return !string.IsNullOrWhiteSpace(guessed) ? guessed : "VPN";
    }

    private static string GuessProviderByIpOrEndpoints(string peerIpStr, List<NetworkEndpointInfo> vpnEndpoints)
    {
        if (!IPAddress.TryParse(peerIpStr, out var peerIp)) return "VPN";

        if (peerIpStr.StartsWith("26.", StringComparison.Ordinal)) return "Radmin VPN";
        if (peerIpStr.StartsWith("25.", StringComparison.Ordinal)) return "Hamachi";

        foreach (var ep in vpnEndpoints)
        {
            if (DoesPeerMatchVpnEndpoint(peerIp, ep) && !string.IsNullOrWhiteSpace(ep.VpnProvider))
                return ep.VpnProvider;
        }

        if (NetworkDiscoveryService.IsRfc6598CgnatIp(peerIp))
        {
            var mesh = vpnEndpoints.FirstOrDefault(e => e.VpnProvider is "Tailscale" or "NetBird" or "ZeroTier");
            if (mesh != null) return mesh.VpnProvider;
        }

        return vpnEndpoints.Count == 1 && !string.IsNullOrWhiteSpace(vpnEndpoints[0].VpnProvider)
            ? vpnEndpoints[0].VpnProvider
            : "VPN";
    }

    private static bool TryParseCidr(string? cidr, out SubnetRange? subnet)
    {
        subnet = null;
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        int slash = cidr.IndexOf('/');
        if (slash <= 0) return false;

        string ipPart = cidr.Substring(0, slash).Trim();
        if (!IPAddress.TryParse(ipPart, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (!int.TryParse(cidr.Substring(slash + 1).Trim(), out int prefix) || prefix < 8 || prefix > 32)
            return false;

        uint mask = ~0u << (32 - prefix);
        uint net = IpToHostUInt32(ip) & mask;
        subnet = new SubnetRange(net, mask, prefix);
        return true;
    }

    private static bool IsInSameSubnet(IPAddress a, IPAddress b, int prefixLen)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork)
            return false;
        int p = Math.Clamp(prefixLen, 1, 32);
        uint mask = ~0u << (32 - p);
        return (IpToHostUInt32(a) & mask) == (IpToHostUInt32(b) & mask);
    }

    private static IPAddress? ComputeDirectedBroadcast(IPAddress ip, int prefixLen)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || prefixLen < 16 || prefixLen > 30)
            return null;
        uint mask = ~0u << (32 - prefixLen);
        return HostUInt32ToIp(IpToHostUInt32(ip) | ~mask);
    }

    private static int CountMaskBits(IPAddress mask)
    {
        byte[] bytes = mask.GetAddressBytes();
        int bits = 0;
        foreach (byte b in bytes)
            bits += System.Numerics.BitOperations.PopCount(b);
        return bits > 0 && bits <= 32 ? bits : 24;
    }

    private static uint IpToHostUInt32(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress HostUInt32ToIp(uint val)
    {
        return new IPAddress(new[]
        {
            (byte)(val >> 24),
            (byte)(val >> 16),
            (byte)(val >> 8),
            (byte)val
        });
    }

    #endregion

    public void Dispose()
    {
        StopHostBeacon();
    }
}
