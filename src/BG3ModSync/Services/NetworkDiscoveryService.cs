using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BG3ModSync.Services;

public enum NetworkCategory
{
    WAN,
    LAN,
    VPN
}

public class NetworkEndpointInfo
{
    public NetworkCategory Category { get; set; }
    public string AdapterName { get; set; } = "";
    public string Description { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; } = 27000;
    public string SubnetMask { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
    public int InterfaceIndex { get; set; } = -1;
    public string VpnProvider { get; set; } = "";

    public string CategoryBadge => Category switch
    {
        NetworkCategory.WAN => "[WAN]",
        NetworkCategory.VPN => "[VPN]",
        _ => "[LAN]"
    };

    public string DisplaySummary => $"{CategoryBadge} {AdapterName} — {IpAddress}";
    public string ShortDisplay => $"{CategoryBadge} {IpAddress} ({AdapterName})";
    public string HostAndPort => $"{IpAddress}:{Port}";
}

public static class NetworkDiscoveryService
{
    private static readonly string[] VpnKeywords =
    {
        "zerotier", "tailscale", "radmin", "hamachi", "wireguard", "openvpn",
        "ovpn", "ovpn-dco", "data channel offload", "tinc", "husarnet", "headscale", "playit",
        "l2tp", "sstp", "ikev2", "wintap",
        "tap-", "tap ", "tun", "wintun", "nordlynx", "proton", "mullvad",
        "surfshark", "softether", "cisco", "anyconnect", "fortinet", "pangp",
        "cloudflare", "warp", "vpn", "virtualip", "nebula", "netbird"
    };

    private static readonly string[] VirtualAdapterKeywords =
    {
        "vethernet", "wsl", "hyper-v", "docker", "virtualbox", "vbox",
        "vmware", "npcap", "default switch", "loopback",
        "lightweight filter", "qos packet scheduler", "wan miniport"
    };

    private static readonly string[] MobileHotspotKeywords =
    {
        "remote ndis", "rndis", "apple mobile device", "iphone", "android",
        "usb ethernet", "cellular", "wwan", "mobile broadband",
        "5g modem", "lte modem", "hotspot", "tethering"
    };

    private static readonly string[] WanIpServices =
    {
        "https://api.ipify.org",
        "https://icanhazip.com",
        "https://checkip.amazonaws.com",
        "http://api.ipify.org",
        "http://icanhazip.com",
        "http://checkip.amazonaws.com"
    };

    public static List<NetworkEndpointInfo> GetLocalAndVpnInterfaces(int port)
    {
        var results = new List<NetworkEndpointInfo>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                int ifIndex = -1;
                try
                {
                    ifIndex = ipProps.GetIPv4Properties()?.Index ?? -1;
                }
                catch { }

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue; // Skip APIPA link-local

                    string subnetMask = ua.IPv4Mask?.ToString() ?? "";
                    int prefixLen = 24;
                    try
                    {
                        if (ua.PrefixLength > 0 && ua.PrefixLength <= 32)
                            prefixLen = ua.PrefixLength;
                        else if (ua.IPv4Mask != null)
                            prefixLen = CountMaskBits(ua.IPv4Mask);
                    }
                    catch
                    {
                        if (ua.IPv4Mask != null)
                            prefixLen = CountMaskBits(ua.IPv4Mask);
                    }

                    bool isVpn = IsVpnInterface(ni, ip);
                    results.Add(new NetworkEndpointInfo
                    {
                        Category = isVpn ? NetworkCategory.VPN : NetworkCategory.LAN,
                        AdapterName = ni.Name,
                        Description = ni.Description,
                        IpAddress = ip,
                        Port = port,
                        SubnetMask = subnetMask,
                        PrefixLength = prefixLen,
                        InterfaceIndex = ifIndex,
                        VpnProvider = isVpn ? DetectVpnProviderName(ni, ip) : ""
                    });
                }
            }
        }
        catch
        {
            // Ignore adapter enumeration errors
        }

        return results
            .OrderBy(r => r.Category == NetworkCategory.LAN ? 0 : 1)
            .ThenBy(r => r.AdapterName)
            .ToList();
    }

    private static int CountMaskBits(IPAddress mask)
    {
        if (mask.AddressFamily != AddressFamily.InterNetwork) return 24;
        byte[] bytes = mask.GetAddressBytes();
        int bits = 0;
        foreach (byte b in bytes)
        {
            bits += System.Numerics.BitOperations.PopCount(b);
        }
        return bits > 0 && bits <= 32 ? bits : 24;
    }

    public static string DetectVpnProviderName(NetworkInterface ni, string ipAddress)
    {
        string combined = $"{ni.Name} {ni.Description}".ToLowerInvariant();
        if (combined.Contains("tailscale", StringComparison.Ordinal) || combined.Contains("headscale", StringComparison.Ordinal))
            return "Tailscale";
        if (combined.Contains("zerotier", StringComparison.Ordinal))
            return "ZeroTier";
        if (combined.Contains("netbird", StringComparison.Ordinal))
            return "NetBird";
        if (combined.Contains("radmin", StringComparison.Ordinal))
            return "Radmin VPN";
        if (combined.Contains("hamachi", StringComparison.Ordinal))
            return "Hamachi";
        if (combined.Contains("wireguard", StringComparison.Ordinal) || ni.Name.StartsWith("wg", StringComparison.OrdinalIgnoreCase))
            return "WireGuard";
        if (combined.Contains("openvpn", StringComparison.Ordinal) || combined.Contains("ovpn", StringComparison.Ordinal) || combined.Contains("data channel offload", StringComparison.Ordinal))
            return "OpenVPN";
        if (combined.Contains("softether", StringComparison.Ordinal))
            return "SoftEther";
        if (combined.Contains("nebula", StringComparison.Ordinal))
            return "Nebula";
        if (combined.Contains("cloudflare", StringComparison.Ordinal) || combined.Contains("warp", StringComparison.Ordinal))
            return "Cloudflare WARP";
        if (combined.Contains("husarnet", StringComparison.Ordinal))
            return "Husarnet";
        if (combined.Contains("tinc", StringComparison.Ordinal))
            return "tinc";
        if (combined.Contains("playit", StringComparison.Ordinal))
            return "playit.gg";

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            if (ipAddress.StartsWith("26.", StringComparison.Ordinal))
                return "Radmin VPN";
            if (ipAddress.StartsWith("25.", StringComparison.Ordinal))
                return "Hamachi";
        }

        return "VPN";
    }

    public static async Task<NetworkEndpointInfo?> GetWanEndpointAsync(int port, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3.5) };
        foreach (var serviceUrl in WanIpServices)
        {
            try
            {
                string raw = (await http.GetStringAsync(serviceUrl, cancellationToken)).Trim();
                if (IPAddress.TryParse(raw, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    return new NetworkEndpointInfo
                    {
                        Category = NetworkCategory.WAN,
                        AdapterName = "Public Internet (WAN)",
                        Description = "External Public IPv4 Address",
                        IpAddress = parsed.ToString(),
                        Port = port
                    };
                }
            }
            catch
            {
                // Try next service
            }
        }

        // Fallback: ask local UPnP IGD router for its External IP if HTTP lookup failed
        try
        {
            string? upnpWan = await UpnpPortForwardService.TryQueryExternalIpAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(upnpWan) &&
                IPAddress.TryParse(upnpWan, out var parsed) &&
                parsed.AddressFamily == AddressFamily.InterNetwork &&
                !IsPrivateRfc1918Ip(parsed) &&
                !IsRfc6598CgnatIp(parsed))
            {
                return new NetworkEndpointInfo
                {
                    Category = NetworkCategory.WAN,
                    AdapterName = "Public Internet (WAN via UPnP)",
                    Description = "External Public IPv4 Address",
                    IpAddress = parsed.ToString(),
                    Port = port
                };
            }
        }
        catch { }

        return null;
    }

    public static bool IsRfc6598CgnatIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    public static bool IsPrivateRfc1918Ip(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    public static bool IsVirtualAdapter(NetworkInterface ni)
    {
        string combined = $"{ni.Name} {ni.Description}".ToLowerInvariant();
        foreach (var kw in VirtualAdapterKeywords)
        {
            if (combined.Contains(kw, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsVpnAdapterByNameOrType(NetworkInterface ni)
    {
        string combined = $"{ni.Name} {ni.Description}".ToLowerInvariant();
        foreach (var kw in VpnKeywords)
        {
            if (combined.Contains(kw, StringComparison.Ordinal))
                return true;
        }

        return ni.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
               ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
    }

    public static async Task<List<NetworkEndpointInfo>> DiscoverAllEndpointsAsync(int port, CancellationToken cancellationToken = default)
    {
        var list = GetLocalAndVpnInterfaces(port);
        var wan = await GetWanEndpointAsync(port, cancellationToken);
        if (wan != null)
        {
            list.Insert(0, wan);
        }
        return list;
    }

    public static bool IsVpnInterface(NetworkInterface ni, string ipAddress)
    {
        string combined = $"{ni.Name} {ni.Description}".ToLowerInvariant();
        foreach (var kw in VpnKeywords)
        {
            if (combined.Contains(kw, StringComparison.Ordinal))
                return true;
        }

        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
            ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            return true;
        }

        // Common VPN IPv4 ranges:
        // 26.x.x.x (Radmin VPN), 25.x.x.x (LogMeIn Hamachi), 100.64.0.0/10 (Tailscale / CGNAT)
        if (ipAddress.StartsWith("26.", StringComparison.Ordinal) ||
            ipAddress.StartsWith("25.", StringComparison.Ordinal))
        {
            return true;
        }

        if (IPAddress.TryParse(ipAddress, out var ip))
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Classifies an IP address string as LAN, VPN, WAN, or Loopback based on local interfaces and standard RFC1918/VPN ranges.
    /// </summary>
    public static string ClassifyRemoteOrTargetIp(string hostOrIp, IEnumerable<NetworkEndpointInfo>? knownLocalEndpoints = null)
    {
        string cleanHost = hostOrIp.Trim();
        if (cleanHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            cleanHost = cleanHost.Substring(7);
        if (cleanHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            cleanHost = cleanHost.Substring(8);
        int colonIdx = cleanHost.IndexOf(':');
        if (colonIdx > 0) cleanHost = cleanHost.Substring(0, colonIdx);
        int slashIdx = cleanHost.IndexOf('/');
        if (slashIdx > 0) cleanHost = cleanHost.Substring(0, slashIdx);

        if (cleanHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || cleanHost == "127.0.0.1")
            return "Local Loopback (127.0.0.1)";

        if (!IPAddress.TryParse(cleanHost, out var ip))
            return $"Direct Host ({cleanHost})";

        byte[] b = ip.GetAddressBytes();

        // Check if it matches the subnet of a local VPN or LAN interface
        if (knownLocalEndpoints != null)
        {
            foreach (var ep in knownLocalEndpoints)
            {
                if (ep.Category == NetworkCategory.WAN) continue;
                if (IPAddress.TryParse(ep.IpAddress, out var localIp))
                {
                    byte[] lb = localIp.GetAddressBytes();
                    bool sameSubnet = (lb[0] == b[0] && lb[1] == b[1]) ||
                                      (ep.Category == NetworkCategory.VPN && IsRfc6598CgnatIp(localIp) && IsRfc6598CgnatIp(ip));
                    if (sameSubnet)
                    {
                        return ep.Category == NetworkCategory.VPN
                            ? $"Direct IP via VPN ({ep.AdapterName} — {cleanHost})"
                            : $"Direct IP via LAN ({ep.AdapterName} — {cleanHost})";
                    }
                }
            }
        }

        // Standard VPN ranges
        if (b[0] == 26) return $"Direct IP via VPN (Radmin {cleanHost})";
        if (b[0] == 25) return $"Direct IP via VPN (Hamachi {cleanHost})";
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return $"Direct IP via VPN (Tailscale/CGNAT {cleanHost})";

        // RFC1918 Private ranges
        if (b[0] == 192 && b[1] == 168) return $"Direct IP via LAN ({cleanHost})";
        if (b[0] == 10) return $"Direct IP via LAN/VPN ({cleanHost})";
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return $"Direct IP via LAN/VPN ({cleanHost})";

        return $"Direct IP via WAN ({cleanHost})";
    }

    public static bool IsVpnIpAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = ip.GetAddressBytes();
        // 26.x.x.x (Radmin), 25.x.x.x (Hamachi), 100.64.0.0/10 (Tailscale/CGNAT)
        if (b[0] == 26 || b[0] == 25) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;

        // Also check if any local VPN adapter shares the same /24 subnet
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var ipProps = ni.GetIPProperties();
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string uaStr = ua.Address.ToString();
                    if (!IsVpnInterface(ni, uaStr)) continue;
                    byte[] lb = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask;
                    if (mask != null)
                    {
                        byte[] mb = mask.GetAddressBytes();
                        if (mb.Length == 4 && (mb[0] != 0 || mb[1] != 0))
                        {
                            if ((lb[0] & mb[0]) == (b[0] & mb[0]) &&
                                (lb[1] & mb[1]) == (b[1] & mb[1]) &&
                                (lb[2] & mb[2]) == (b[2] & mb[2]) &&
                                (lb[3] & mb[3]) == (b[3] & mb[3]))
                            {
                                return true;
                            }
                            continue;
                        }
                    }
                    if (lb[0] == b[0] && lb[1] == b[1] && lb[2] == b[2])
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static (int InterfaceIndex, IPAddress LocalIp, string AdapterName)? TryGetBestPhysicalLanAdapter()
    {
        try
        {
            (int IfIdx, IPAddress Ip, string Name, int Score)? best = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (!ni.Supports(NetworkInterfaceComponent.IPv4)) continue;
                    if (IsVirtualAdapter(ni)) continue;
                    if (IsVpnAdapterByNameOrType(ni)) continue;

                    var ipProps = ni.GetIPProperties();
                    var ipv4Props = ipProps.GetIPv4Properties();
                    if (ipv4Props == null) continue;

                    bool hasGateway = ipProps.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !g.Address.Equals(IPAddress.Any));

                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;

                        string ipStr = ua.Address.ToString();
                        if (ipStr.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        if (IsVpnInterface(ni, ipStr)) continue;

                        int score = (hasGateway ? 100 : 0) +
                                    (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 50 : 20);

                        if (best == null || score > best.Value.Score)
                        {
                            best = (ipv4Props.Index, ua.Address, ni.Name, score);
                        }
                    }
                }
                catch
                {
                    // Skip filter/virtual miniport adapters that throw on GetIPv4Properties()
                }
            }

            if (best.HasValue)
            {
                return (best.Value.IfIdx, best.Value.Ip, best.Value.Name);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 5-layer local CGNAT / Symmetric NAT detector:
    /// 1. UPnP IGD External IP vs Public WAN IP (direct proof of real public IP vs double-NAT/CGNAT)
    /// 2. Non-VPN local interface holding RFC 6598 (100.64.0.0/10) carrier-grade NAT IP
    /// 3. RFC 5389 Dual-STUN (Google + Cloudflare from the same local UDP socket) to detect Symmetric NAT / CGNAT
    /// 4. Mobile 5G/LTE cellular adapter, USB tether, or phone hotspot subnet (when no UPnP public WAN confirmed)
    /// 5. Fast ICMP TTL 2..4 carrier hop probe checking for RFC 6598 (100.64.0.0/10) ISP CGNAT router
    /// </summary>
    public static async Task<(bool IsCgnat, string Reason)> DetectLocalCgnatAsync(
        IReadOnlyList<NetworkEndpointInfo>? knownEndpoints = null,
        CancellationToken cancellationToken = default)
    {
        string publicWanIp = knownEndpoints?.FirstOrDefault(e => e.Category == NetworkCategory.WAN)?.IpAddress ?? "";

        // Layer 1: UPnP IGD External IP vs Public WAN IP
        string? upnpExternalIp = null;
        try
        {
            upnpExternalIp = await UpnpPortForwardService.TryQueryExternalIpAsync(cancellationToken);
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(upnpExternalIp) && IPAddress.TryParse(upnpExternalIp, out var extParsed))
        {
            if (IsRfc6598CgnatIp(extParsed))
            {
                return (true, $"Router WAN IP {upnpExternalIp} is in RFC 6598 CGNAT range (100.64.0.0/10)");
            }
            if (IsPrivateRfc1918Ip(extParsed))
            {
                return (true, $"Router WAN IP {upnpExternalIp} is private RFC 1918 (Double-NAT / CGNAT)");
            }
            if (!string.IsNullOrWhiteSpace(publicWanIp))
            {
                if (!string.Equals(upnpExternalIp, publicWanIp, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, $"UPnP Router WAN ({upnpExternalIp}) != Public WAN ({publicWanIp}) — CGNAT / Double-NAT");
                }
                return (false, $"Direct Public WAN IP ({publicWanIp}) confirmed via UPnP IGD");
            }
        }

        // Layer 2: Non-VPN physical/cellular interface with RFC 6598 (100.64.0.0/10)
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (IsVirtualAdapter(ni) || IsVpnAdapterByNameOrType(ni)) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsRfc6598CgnatIp(ua.Address))
                    {
                        return (true, $"Adapter '{ni.Name}' has RFC 6598 carrier IP ({ua.Address})");
                    }
                }
            }
        }
        catch { }

        // Layer 3: RFC 5389 Dual-STUN from a single local UDP socket (detects Symmetric NAT / 5G CGNAT)
        try
        {
            var stunResult = await ProbeDualStunAsync(cancellationToken);
            if (stunResult.HasBoth)
            {
                if (!string.Equals(stunResult.Ip1, stunResult.Ip2, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, $"Dual-STUN public IP mismatch ({stunResult.Ip1} vs {stunResult.Ip2}) — CGNAT pool");
                }
                if (stunResult.Port1 != stunResult.Port2)
                {
                    return (true, $"Symmetric NAT / CGNAT detected via Dual-STUN (mapped ports {stunResult.Port1} vs {stunResult.Port2})");
                }
                if (!string.IsNullOrWhiteSpace(upnpExternalIp) &&
                    !string.Equals(upnpExternalIp, stunResult.Ip1, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, $"UPnP WAN ({upnpExternalIp}) != STUN Public IP ({stunResult.Ip1}) — CGNAT");
                }
            }
        }
        catch { }

        // Layer 4: Active default-gateway Mobile 5G/LTE modem, USB tether, or phone hotspot subnet
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (IsVirtualAdapter(ni) || IsVpnAdapterByNameOrType(ni)) continue;

                var ipProps = ni.GetIPProperties();
                bool hasGateway = ipProps.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));
                if (!hasGateway) continue;

                string combined = $"{ni.Name} {ni.Description}".ToLowerInvariant();
                foreach (var kw in MobileHotspotKeywords)
                {
                    if (combined.Contains(kw, StringComparison.Ordinal))
                    {
                        return (true, $"Mobile / Cellular tethering adapter detected ('{ni.Name}') — Carrier NAT (CGNAT)");
                    }
                }

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ipStr = ua.Address.ToString();
                    if (ipStr.StartsWith("172.20.10.", StringComparison.Ordinal) ||
                        ipStr.StartsWith("192.168.42.", StringComparison.Ordinal) ||
                        ipStr.StartsWith("192.168.43.", StringComparison.Ordinal) ||
                        ipStr.StartsWith("192.168.225.", StringComparison.Ordinal))
                    {
                        return (true, $"Mobile phone hotspot/tether subnet detected ({ipStr} on '{ni.Name}') — Carrier NAT (CGNAT)");
                    }
                }
            }
        }
        catch { }

        // Layer 5: Fast ICMP TTL 2..4 probe to 1.1.1.1 checking for ISP RFC 6598 CGNAT carrier hop
        try
        {
            byte[] payload = new byte[] { 0x42, 0x47, 0x33, 0x53 };
            using var pinger = new Ping();
            for (int ttl = 2; ttl <= 4; ttl++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reply = await pinger.SendPingAsync(IPAddress.Parse("1.1.1.1"), 700, payload, new PingOptions(ttl, false));
                if (reply.Address != null && reply.Address.AddressFamily == AddressFamily.InterNetwork && IsRfc6598CgnatIp(reply.Address))
                {
                    return (true, $"ISP CGNAT carrier hop detected at TTL {ttl} ({reply.Address})");
                }
                if (reply.Status == IPStatus.Success)
                {
                    break;
                }
            }
        }
        catch { }

        string wanNote = !string.IsNullOrWhiteSpace(publicWanIp) ? $" (Public WAN: {publicWanIp})" : "";
        return (false, $"Standard NAT / Direct Public IP{wanNote}");
    }

    private static async Task<(bool HasBoth, string Ip1, int Port1, string Ip2, int Port2)> ProbeDualStunAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var r1 = await QuerySingleStunServerAsync(udp, "stun.l.google.com", 19302, cancellationToken);
        if (!r1.HasValue) return (false, "", 0, "", 0);

        var r2 = await QuerySingleStunServerAsync(udp, "stun.cloudflare.com", 3478, cancellationToken);
        if (!r2.HasValue) return (false, r1.Value.Ip, r1.Value.Port, "", 0);

        return (true, r1.Value.Ip, r1.Value.Port, r2.Value.Ip, r2.Value.Port);
    }

    private static async Task<(string Ip, int Port)?> QuerySingleStunServerAsync(
        UdpClient udp,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(1400);

            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
            var serverIp = addresses.FirstOrDefault();
            if (serverIp == null) return null;

            byte[] req = new byte[20];
            req[0] = 0x00; req[1] = 0x01; // Binding Request
            req[2] = 0x00; req[3] = 0x00; // Length = 0
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; // RFC 5389 Magic Cookie
            Random.Shared.NextBytes(req.AsSpan(8, 12));

            await udp.SendAsync(req, new IPEndPoint(serverIp, port), cts.Token);

            var resp = await udp.ReceiveAsync(cts.Token);
            byte[] buf = resp.Buffer;
            if (buf.Length < 20) return null;
            if (buf[0] != 0x01 || buf[1] != 0x01) return null; // Binding Success Response
            if (buf[4] != 0x21 || buf[5] != 0x12 || buf[6] != 0xA4 || buf[7] != 0x42) return null;
            for (int i = 8; i < 20; i++)
            {
                if (buf[i] != req[i]) return null;
            }

            int msgLen = (buf[2] << 8) | buf[3];
            int end = Math.Min(buf.Length, 20 + msgLen);
            int offset = 20;
            while (offset + 4 <= end)
            {
                int attrType = (buf[offset] << 8) | buf[offset + 1];
                int attrLen = (buf[offset + 2] << 8) | buf[offset + 3];
                int valStart = offset + 4;
                if (valStart + attrLen > end) break;

                if (attrType == 0x0020 && attrLen >= 8 && buf[valStart + 1] == 0x01) // XOR-MAPPED-ADDRESS IPv4
                {
                    int mappedPort = ((buf[valStart + 2] << 8) | buf[valStart + 3]) ^ 0x2112;
                    var mappedIp = new IPAddress(new byte[]
                    {
                        (byte)(buf[valStart + 4] ^ 0x21),
                        (byte)(buf[valStart + 5] ^ 0x12),
                        (byte)(buf[valStart + 6] ^ 0xA4),
                        (byte)(buf[valStart + 7] ^ 0x42)
                    });
                    return (mappedIp.ToString(), mappedPort);
                }

                if (attrType == 0x0001 && attrLen >= 8 && buf[valStart + 1] == 0x01) // MAPPED-ADDRESS IPv4
                {
                    int mappedPort = (buf[valStart + 2] << 8) | buf[valStart + 3];
                    var mappedIp = new IPAddress(new byte[]
                    {
                        buf[valStart + 4],
                        buf[valStart + 5],
                        buf[valStart + 6],
                        buf[valStart + 7]
                    });
                    return (mappedIp.ToString(), mappedPort);
                }

                offset = valStart + ((attrLen + 3) & ~3);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Binds an outbound TCP socket directly to the physical LAN adapter using WinSock IP_UNICAST_IF (option 31)
    /// when connecting to a non-VPN LAN/WAN IPv4 address. This prevents Tailscale/ZeroTier subnet routes (/24)
    /// from hijacking physical LAN traffic through a slow router VPN tunnel.
    /// </summary>
    public static void TryBindSocketToPhysicalLanIfApplicable(Socket socket, IPAddress targetIp)
    {
        try
        {
            if (targetIp.AddressFamily != AddressFamily.InterNetwork) return;
            if (IPAddress.IsLoopback(targetIp)) return;
            if (IsVpnIpAddress(targetIp)) return;

            var lanAdapter = TryGetBestPhysicalLanAdapter();
            if (!lanAdapter.HasValue) return;

            // WinSock IP_UNICAST_IF = 31 (expects interface index in network byte order)
            int netOrderIfIndex = IPAddress.HostToNetworkOrder(lanAdapter.Value.InterfaceIndex);
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, netOrderIfIndex);
            socket.Bind(new IPEndPoint(lanAdapter.Value.LocalIp, 0));
        }
        catch
        {
            // Fallback to default OS routing if socket option fails
        }
    }

    public static SocketsHttpHandler CreateOptimizedHttpHandler(TimeSpan? connectTimeout = null)
    {
        return new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellationToken) =>
            {
                IPAddress? targetIp = null;
                if (!IPAddress.TryParse(context.DnsEndPoint.Host, out targetIp))
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                    targetIp = addresses.FirstOrDefault();
                }

                if (targetIp != null)
                {
                    var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                        ReceiveBufferSize = 4 * 1024 * 1024,
                        SendBufferSize = 1024 * 1024
                    };

                    try
                    {
                        TryBindSocketToPhysicalLanIfApplicable(socket, targetIp);
                        await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        // Fallback to standard unweighted OS socket if physical LAN binding couldn't reach host
                        var fallbackSocket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true,
                            ReceiveBufferSize = 4 * 1024 * 1024,
                            SendBufferSize = 1024 * 1024
                        };
                        try
                        {
                            await fallbackSocket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken);
                            return new NetworkStream(fallbackSocket, ownsSocket: true);
                        }
                        catch
                        {
                            fallbackSocket.Dispose();
                            throw;
                        }
                    }
                }

                var defaultSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await defaultSocket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(defaultSocket, ownsSocket: true);
                }
                catch
                {
                    defaultSocket.Dispose();
                    throw;
                }
            }
        };
    }

    public static HttpClient CreateOptimizedHttpClient(TimeSpan timeout, TimeSpan? connectTimeout = null)
    {
        var handler = CreateOptimizedHttpHandler(connectTimeout);
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };
    }
}
