using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BG3ModSync.Services;

public class UpnpPortForwardService
{
    private string? _lastControlUrl;
    private string? _lastServiceType;
    private int _lastForwardedPort;

    public bool IsPortForwarded { get; private set; }
    public string? ForwardedExternalIp { get; private set; }

    public async Task<bool> TryForwardPortAsync(
        int port,
        Action<string> logCallback,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string? localLanIp = GetPrimaryLanIpForGateway();
            if (string.IsNullOrWhiteSpace(localLanIp))
            {
                logCallback("[UPnP] Could not determine local LAN IPv4 address for UPnP port mapping.");
                return false;
            }

            logCallback($"[UPnP] Announcing UPnP IGD discovery to auto-forward TCP Port {port} -> {localLanIp}:{port}...");

            var gateway = await DiscoverUpnpGatewayAsync(localLanIp, cancellationToken);
            if (gateway == null)
            {
                logCallback($"[UPnP] No UPnP-enabled router responded on LAN ({localLanIp}). If behind NAT/CGNAT, manual port-forwarding or Steam Hole-Punch may be required.");
                IsPortForwarded = false;
                return false;
            }

            var (controlUrl, serviceType) = gateway.Value;
            _lastControlUrl = controlUrl;
            _lastServiceType = serviceType;
            _lastForwardedPort = port;

            var (mapped, errorDetail) = await SendAddPortMappingSoapAsync(controlUrl, serviceType, port, localLanIp, "BG3ModSync Portable Nginx", cancellationToken);
            if (mapped)
            {
                IsPortForwarded = true;
                string? extIp = await TryGetExternalIpViaSoapAsync(controlUrl, serviceType, cancellationToken);
                ForwardedExternalIp = extIp;
                logCallback($"[UPnP] SUCCESS! Auto-forwarded WAN TCP Port {port} -> {localLanIp}:{port} via UPnP ({(string.IsNullOrEmpty(extIp) ? "Gateway IGD" : $"Router WAN IP: {extIp}")}).");
                return true;
            }

            logCallback($"[UPnP] Router rejected AddPortMapping for TCP port {port}{(string.IsNullOrEmpty(errorDetail) ? "." : $": {errorDetail}")}");
            IsPortForwarded = false;
            return false;
        }
        catch (Exception ex)
        {
            logCallback($"[UPnP] Auto port-forward attempt finished: {ex.Message}");
            IsPortForwarded = false;
            return false;
        }
    }

    public async Task RemovePortForwardAsync(Action<string>? logCallback = null)
    {
        if (!IsPortForwarded || string.IsNullOrEmpty(_lastControlUrl) || string.IsNullOrEmpty(_lastServiceType) || _lastForwardedPort <= 0)
            return;

        try
        {
            string soapBody =
                "<?xml version=\"1.0\"?>\r\n" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                "  <s:Body>\r\n" +
                $"    <u:DeletePortMapping xmlns:u=\"{_lastServiceType}\">\r\n" +
                "      <NewRemoteHost></NewRemoteHost>\r\n" +
                $"      <NewExternalPort>{_lastForwardedPort}</NewExternalPort>\r\n" +
                "      <NewProtocol>TCP</NewProtocol>\r\n" +
                "    </u:DeletePortMapping>\r\n" +
                "  </s:Body>\r\n" +
                "</s:Envelope>";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, _lastControlUrl);
            req.Headers.Add("SOAPAction", $"\"{_lastServiceType}#DeletePortMapping\"");
            req.Content = new StringContent(soapBody, Encoding.UTF8, "text/xml");
            await http.SendAsync(req);

            logCallback?.Invoke($"[UPnP] Removed UPnP port mapping for TCP port {_lastForwardedPort}.");
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            IsPortForwarded = false;
        }
    }

    public static async Task<string?> TryQueryExternalIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string? localLanIp = GetPrimaryLanIpForGateway();
            if (string.IsNullOrWhiteSpace(localLanIp)) return null;
            var gateway = await DiscoverUpnpGatewayAsync(localLanIp, cancellationToken);
            if (gateway == null) return null;
            return await TryGetExternalIpViaSoapAsync(gateway.Value.ControlUrl, gateway.Value.ServiceType, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetPrimaryLanIpForGateway()
    {
        var best = NetworkDiscoveryService.TryGetBestPhysicalLanAdapter();
        if (best.HasValue)
        {
            return best.Value.LocalIp.ToString();
        }
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                if (!ipProps.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address)))
                    continue;

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        string ip = ua.Address.ToString();
                        if (!NetworkDiscoveryService.IsVpnInterface(ni, ip))
                        {
                            return ip;
                        }
                    }
                }
            }
        }
        catch { }

        return NginxServerService.GetLocalIpAddresses().FirstOrDefault();
    }

    private static async Task<(string ControlUrl, string ServiceType)?> DiscoverUpnpGatewayAsync(string localLanIp, CancellationToken cancellationToken)
    {
        string[] searchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
            "upnp:rootdevice"
        };

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localLanIp), 0));
        udp.Client.ReceiveTimeout = 2000;
        var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        foreach (var st in searchTargets)
        {
            string msearch =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                $"ST: {st}\r\n\r\n";

            byte[] data = Encoding.ASCII.GetBytes(msearch);
            await udp.SendAsync(data, data.Length, multicastEndpoint);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                var receiveTask = udp.ReceiveAsync(timeoutCts.Token).AsTask();
                var result = await receiveTask;
                string responseText = Encoding.UTF8.GetString(result.Buffer);

                string? locationUrl = ParseLocationHeader(responseText);
                if (string.IsNullOrWhiteSpace(locationUrl)) continue;

                string xml = await http.GetStringAsync(locationUrl, timeoutCts.Token);
                var parsed = ExtractWanConnectionControlUrl(locationUrl, xml);
                if (parsed != null)
                {
                    return parsed;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore non-gateway SSDP responses and continue listening
            }
        }

        return null;
    }

    private static string? ParseLocationHeader(string ssdpResponse)
    {
        foreach (var line in ssdpResponse.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(9).Trim();
            }
        }
        return null;
    }

    private static (string ControlUrl, string ServiceType)? ExtractWanConnectionControlUrl(string baseLocationUrl, string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            foreach (var serviceNode in doc.Descendants().Where(e => e.Name.LocalName.Equals("service", StringComparison.OrdinalIgnoreCase)))
            {
                string? serviceType = serviceNode.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("serviceType", StringComparison.OrdinalIgnoreCase))?.Value;
                string? controlUrl = serviceNode.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("controlURL", StringComparison.OrdinalIgnoreCase))?.Value;

                if (!string.IsNullOrWhiteSpace(serviceType) &&
                    !string.IsNullOrWhiteSpace(controlUrl) &&
                    (serviceType.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) ||
                     serviceType.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase)))
                {
                    var baseUri = new Uri(baseLocationUrl);
                    var fullControlUri = new Uri(baseUri, controlUrl);
                    return (fullControlUri.ToString(), serviceType.Trim());
                }
            }
        }
        catch { }

        return null;
    }

    private static async Task<(bool Success, string ErrorDetail)> SendAddPortMappingSoapAsync(
        string controlUrl,
        string serviceType,
        int port,
        string internalClientIp,
        string description,
        CancellationToken cancellationToken)
    {
        // Try IGDv2-compatible dynamic lease duration (7200s) first, then fallback to static lease (0) for older IGDv1 routers
        int[] leaseDurations = { 7200, 0 };
        string lastError = "";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        foreach (int lease in leaseDurations)
        {
            string soapBody =
                "<?xml version=\"1.0\"?>\r\n" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                "  <s:Body>\r\n" +
                $"    <u:AddPortMapping xmlns:u=\"{serviceType}\">\r\n" +
                "      <NewRemoteHost></NewRemoteHost>\r\n" +
                $"      <NewExternalPort>{port}</NewExternalPort>\r\n" +
                "      <NewProtocol>TCP</NewProtocol>\r\n" +
                $"      <NewInternalPort>{port}</NewInternalPort>\r\n" +
                $"      <NewInternalClient>{internalClientIp}</NewInternalClient>\r\n" +
                "      <NewEnabled>1</NewEnabled>\r\n" +
                $"      <NewPortMappingDescription>{description}</NewPortMappingDescription>\r\n" +
                $"      <NewLeaseDuration>{lease}</NewLeaseDuration>\r\n" +
                "    </u:AddPortMapping>\r\n" +
                "  </s:Body>\r\n" +
                "</s:Envelope>";

            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
            req.Headers.Add("SOAPAction", $"\"{serviceType}#AddPortMapping\"");
            req.Content = new StringContent(soapBody, Encoding.UTF8, "text/xml");

            using var resp = await http.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                return (true, "");
            }

            try
            {
                string errXml = await resp.Content.ReadAsStringAsync(cancellationToken);
                var doc = XDocument.Parse(errXml);
                string? code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("errorCode", StringComparison.OrdinalIgnoreCase))?.Value;
                string? desc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("errorDescription", StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(desc))
                {
                    lastError = $"UPnP Error {code} ({desc})";
                }
            }
            catch
            {
                lastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            }
        }

        return (false, lastError);
    }

    private static async Task<string?> TryGetExternalIpViaSoapAsync(
        string controlUrl,
        string serviceType,
        CancellationToken cancellationToken)
    {
        try
        {
            string soapBody =
                "<?xml version=\"1.0\"?>\r\n" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                "  <s:Body>\r\n" +
                $"    <u:GetExternalIPAddress xmlns:u=\"{serviceType}\"></u:GetExternalIPAddress>\r\n" +
                "  </s:Body>\r\n" +
                "</s:Envelope>";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
            req.Headers.Add("SOAPAction", $"\"{serviceType}#GetExternalIPAddress\"");
            req.Content = new StringContent(soapBody, Encoding.UTF8, "text/xml");

            using var resp = await http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) return null;

            string xml = await resp.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(xml);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("NewExternalIPAddress", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
        }
        catch
        {
            return null;
        }
    }
}
