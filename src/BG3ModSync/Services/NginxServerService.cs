using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BG3ModSync.Models;

namespace BG3ModSync.Services;

public class NginxServerService : IDisposable
{
    private const string NginxDownloadUrl = "https://nginx.org/download/nginx-1.30.6.zip";
    private const string ConfigFileName = "bg3modsync_nginx.conf";

    private Process? _nginxProcess;
    private CancellationTokenSource? _logTailCts;
    private TcpListener? _telemetryListener;
    private int _internalTelemetryPort;
    private string? _activeNginxDir;
    public bool IsRunning => _nginxProcess != null && !_nginxProcess.HasExited;
    public int ActivePort { get; private set; } = 27000;
    public event Action<string>? OnClientHttpConnected;
    public event Action<ClientLobbyTelemetry>? OnClientTelemetryReceived;

    public static string LocateNginxDirectory()
    {
        // Use the isolated per-session runtime temp folder so no files clutter the .exe directory
        // and everything is automatically wiped when the program closes.
        string runtimeNginxDir = Path.Combine(RuntimeTempCleanupService.SessionRuntimeDir, "nginx");
        RuntimeTempCleanupService.TrackTempDirectory(runtimeNginxDir);
        return runtimeNginxDir;
    }

    public async Task<string> EnsurePortableNginxAsync(Action<string> logCallback)
    {
        string nginxDir = LocateNginxDirectory();
        string exePath = Path.Combine(nginxDir, "nginx.exe");
        string mimePath = Path.Combine(nginxDir, "conf", "mime.types");

        if (File.Exists(exePath) && File.Exists(mimePath))
        {
            return nginxDir;
        }

        Directory.CreateDirectory(nginxDir);
        Directory.CreateDirectory(Path.Combine(nginxDir, "conf"));

        // 1. Extract embedded nginx.exe and mime.types from single-file .exe resources
        var asm = typeof(NginxServerService).Assembly;
        bool extractedExe = await TryExtractEmbeddedResourceAsync(asm, "BG3ModSync.Embedded.nginx.exe", exePath);
        bool extractedMime = await TryExtractEmbeddedResourceAsync(asm, "BG3ModSync.Embedded.mime.types", mimePath);

        if (extractedExe && File.Exists(exePath))
        {
            RuntimeTempCleanupService.TrackTempFile(exePath);
            RuntimeTempCleanupService.TrackTempFile(mimePath);
            return nginxDir;
        }

        // 2. Fallback: check local workspace tools/nginx if running unpacked during development
        string baseDir = AppContext.BaseDirectory;
        string[] devCandidates =
        {
            Path.Combine(baseDir, "tools", "nginx"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "tools", "nginx")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "tools", "nginx"))
        };
        foreach (var candidate in devCandidates)
        {
            string candidateExe = Path.Combine(candidate, "nginx.exe");
            if (File.Exists(candidateExe))
            {
                File.Copy(candidateExe, exePath, overwrite: true);
                string candidateMime = Path.Combine(candidate, "conf", "mime.types");
                if (File.Exists(candidateMime))
                {
                    File.Copy(candidateMime, mimePath, overwrite: true);
                }
                return nginxDir;
            }
        }

        // 3. Final fallback: download portable Nginx if neither embedded resource nor dev folder exists
        logCallback($"[NGINX] Auto-downloading portable Nginx 1.30.6 to temporary runtime folder...");
        string toolsParent = RuntimeTempCleanupService.SessionRuntimeDir;
        string zipPath = Path.Combine(toolsParent, "nginx_portable.zip");
        RuntimeTempCleanupService.TrackTempFile(zipPath);

        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        {
            var bytes = await http.GetByteArrayAsync(NginxDownloadUrl);
            await File.WriteAllBytesAsync(zipPath, bytes);
        }

        ZipFile.ExtractToDirectory(zipPath, toolsParent, overwriteFiles: true);
        try { File.Delete(zipPath); } catch { }

        string extracted = Path.Combine(toolsParent, "nginx-1.30.6");
        if (Directory.Exists(extracted))
        {
            if (Directory.Exists(nginxDir))
                Directory.Delete(nginxDir, recursive: true);
            Directory.Move(extracted, nginxDir);
        }

        logCallback($"[NGINX] Portable Nginx prepared in temporary runtime storage.");
        return nginxDir;
    }

    private static async Task<bool> TryExtractEmbeddedResourceAsync(System.Reflection.Assembly asm, string resourceName, string destPath)
    {
        try
        {
            await using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return false;

            string? dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int PickFreeTcpPort()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port > 0 && port <= 65535) return port;
        }
        catch { }
        return 27000;
    }

    public async Task<bool> StartServerAsync(
        AppSettings settings,
        string modHashFilePath,
        List<ModHashEntry> entries,
        Action<string> logCallback,
        HostDirectEndpointsMetadata? hostMeta = null,
        bool useAutoPort = false)
    {
        StopServer(logCallback, silent: true);

        string nginxDir = await EnsurePortableNginxAsync(logCallback);
        _activeNginxDir = nginxDir;
        if (useAutoPort)
        {
            ActivePort = PickFreeTcpPort();
            logCallback($"[NGINX] Steam P2P Auto-Port enabled: OS assigned free ephemeral TCP port {ActivePort}.");
        }
        else
        {
            ActivePort = settings.NginxPort > 0 ? settings.NginxPort : 27000;
        }

        if (hostMeta != null)
        {
            hostMeta.NginxPort = ActivePort;
        }

        Directory.CreateDirectory(Path.Combine(nginxDir, "conf"));
        Directory.CreateDirectory(Path.Combine(nginxDir, "logs"));
        Directory.CreateDirectory(Path.Combine(nginxDir, "temp"));
        Directory.CreateDirectory(Path.Combine(nginxDir, "temp", "client_body_temp"));
        Directory.CreateDirectory(Path.Combine(nginxDir, "temp", "proxy_temp"));
        Directory.CreateDirectory(Path.Combine(nginxDir, "temp", "fastcgi_temp"));
        Directory.CreateDirectory(Path.Combine(nginxDir, "temp", "uwsgi_temp"));
        Directory.CreateDirectory(Path.Combine(nginxDir, "temp", "scgi_temp"));

        string accessLogPath = Path.Combine(nginxDir, "logs", "bg3_access.log");
        try { File.WriteAllText(accessLogPath, "", new UTF8Encoding(false)); } catch { }

        // If modHashFilePath does not exist yet (e.g. while Host is still scanning files), create an empty placeholder
        if (string.IsNullOrWhiteSpace(modHashFilePath) || !File.Exists(modHashFilePath))
        {
            modHashFilePath = Path.Combine(nginxDir, "conf", "ModHash_pending.txt");
            try { await File.WriteAllTextAsync(modHashFilePath, "", new UTF8Encoding(false)); } catch { }
        }

        string hostInfoPath = Path.Combine(nginxDir, "conf", "host_info.json");
        if (hostMeta != null)
        {
            try { await File.WriteAllTextAsync(hostInfoPath, JsonSerializer.Serialize(hostMeta), new UTF8Encoding(false)); } catch { }
        }
        else
        {
            try { await File.WriteAllTextAsync(hostInfoPath, "{}", new UTF8Encoding(false)); } catch { }
        }

        StartInternalTelemetryListener();

        string confContent = BuildNginxConfig(settings, modHashFilePath, hostInfoPath, entries, ActivePort, _internalTelemetryPort);
        string confFilePath = Path.Combine(nginxDir, "conf", ConfigFileName);
        await File.WriteAllTextAsync(confFilePath, confContent, new UTF8Encoding(false));

        logCallback($"[NGINX] Generated zero-copy Nginx configuration at: {confFilePath}");
        logCallback($"[NGINX] Direct file locations mapped from ModHash.txt (0 files copied to server storage).");

        EnsureFirewallRule( Path.Combine(nginxDir, "nginx.exe"), ActivePort, logCallback);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(nginxDir, "nginx.exe"),
            Arguments = $"-p \"{nginxDir}\" -c conf/{ConfigFileName}",
            WorkingDirectory = nginxDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        _nginxProcess = Process.Start(psi);
        await Task.Delay(500);

        if (_nginxProcess == null || _nginxProcess.HasExited)
        {
            string err = _nginxProcess != null ? await _nginxProcess.StandardError.ReadToEndAsync() : "Process failed to start";
            logCallback($"[ERROR] Portable Nginx failed to start: {err}");
            return false;
        }

        // Verify HTTP health endpoint
        bool healthy = false;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    var resp = await http.GetStringAsync($"http://127.0.0.1:{ActivePort}/health");
                    if (resp.Contains("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        healthy = true;
                        break;
                    }
                }
                catch
                {
                    await Task.Delay(250);
                }
            }
        }

        if (!healthy)
        {
            logCallback($"[WARN] Nginx started (PID {_nginxProcess.Id}), but local health check on port {ActivePort} timed out.");
        }
        else
        {
            logCallback($"[NGINX] Portable Nginx HTTP Server is ONLINE on port {ActivePort} (PID {_nginxProcess.Id}).");
        }

        StartAccessLogTailer(accessLogPath, logCallback);
        return true;
    }

    public async Task<bool> UpdateHostManifestAndReloadAsync(
        AppSettings settings,
        string modHashFilePath,
        List<ModHashEntry> entries,
        Action<string> logCallback,
        HostDirectEndpointsMetadata? hostMeta = null,
        bool useAutoPort = false)
    {
        int configuredPort = settings.NginxPort > 0 ? settings.NginxPort : 27000;
        bool portModeMismatch = useAutoPort
            ? (ActivePort == configuredPort)
            : (ActivePort != configuredPort);

        if (!IsRunning || string.IsNullOrEmpty(_activeNginxDir) || portModeMismatch)
        {
            return await StartServerAsync(settings, modHashFilePath, entries, logCallback, hostMeta, useAutoPort);
        }

        string nginxDir = _activeNginxDir;
        string hostInfoPath = Path.Combine(nginxDir, "conf", "host_info.json");
        if (hostMeta != null)
        {
            hostMeta.NginxPort = ActivePort;
            try { await File.WriteAllTextAsync(hostInfoPath, JsonSerializer.Serialize(hostMeta), new UTF8Encoding(false)); } catch { }
        }

        if (_internalTelemetryPort <= 0)
        {
            StartInternalTelemetryListener();
        }

        string confContent = BuildNginxConfig(settings, modHashFilePath, hostInfoPath, entries, ActivePort, _internalTelemetryPort);
        string confFilePath = Path.Combine(nginxDir, "conf", ConfigFileName);
        await File.WriteAllTextAsync(confFilePath, confContent, new UTF8Encoding(false));

        var reloadPsi = new ProcessStartInfo
        {
            FileName = Path.Combine(nginxDir, "nginx.exe"),
            Arguments = $"-p \"{nginxDir}\" -c conf/{ConfigFileName} -s reload",
            WorkingDirectory = nginxDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var reloadProc = Process.Start(reloadPsi);
        if (reloadProc != null)
        {
            await reloadProc.WaitForExitAsync();
        }

        logCallback($"[NGINX] Reloaded zero-copy Nginx configuration with {entries.Count} file entries on port {ActivePort}.");
        return IsRunning;
    }

    private void StartInternalTelemetryListener()
    {
        try
        {
            if (_telemetryListener != null && _internalTelemetryPort > 0)
                return;

            _telemetryListener = new TcpListener(IPAddress.Loopback, 0);
            _telemetryListener.Start();
            _internalTelemetryPort = ((IPEndPoint)_telemetryListener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                var listener = _telemetryListener;
                while (listener != null)
                {
                    TcpClient? client = null;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync();
                        _ = HandleTelemetryHttpClientAsync(client);
                    }
                    catch
                    {
                        client?.Dispose();
                        break;
                    }
                }
            });
        }
        catch
        {
            _internalTelemetryPort = 0;
        }
    }

    private async Task HandleTelemetryHttpClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                string remoteIp = "";
                string remotePort = "";
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync()))
                {
                    if (headerLine.StartsWith("X-Real-IP:", StringComparison.OrdinalIgnoreCase))
                    {
                        remoteIp = headerLine.Substring("X-Real-IP:".Length).Trim();
                    }
                    else if (headerLine.StartsWith("X-Real-Port:", StringComparison.OrdinalIgnoreCase))
                    {
                        remotePort = headerLine.Substring("X-Real-Port:".Length).Trim();
                    }
                }

                string remoteEndpoint = !string.IsNullOrEmpty(remoteIp)
                    ? (!string.IsNullOrEmpty(remotePort) ? $"{remoteIp}:{remotePort}" : remoteIp)
                    : "";

                if (!string.IsNullOrEmpty(remoteIp) && remoteIp != "127.0.0.1")
                {
                    OnClientHttpConnected?.Invoke(remoteEndpoint);
                }

                // Parse ?data=<base64> or ?telemetry=<base64> from request line
                int qIdx = requestLine.IndexOf("?data=", StringComparison.Ordinal);
                int prefixLen = 6;
                if (qIdx < 0)
                {
                    qIdx = requestLine.IndexOf("?telemetry=", StringComparison.Ordinal);
                    prefixLen = 11;
                }

                if (qIdx >= 0)
                {
                    int start = qIdx + prefixLen;
                    int end = requestLine.IndexOf(' ', start);
                    if (end < 0) end = requestLine.Length;
                    int amp = requestLine.IndexOf('&', start);
                    if (amp >= start && amp < end) end = amp;

                    if (end > start)
                    {
                        string encoded = requestLine.Substring(start, end - start).Trim();
                        string b64 = Uri.UnescapeDataString(encoded);
                        byte[] jsonBytes = Convert.FromBase64String(b64);
                        var telemetry = JsonSerializer.Deserialize<ClientLobbyTelemetry>(jsonBytes);
                        if (telemetry != null)
                        {
                            // Stamp client's real source IP:Port observed by Nginx (never Host's listening ActivePort!)
                            if (!string.IsNullOrEmpty(remoteIp) && remoteIp != "127.0.0.1")
                            {
                                telemetry.IpAndPort = remoteEndpoint;
                            }
                            if (string.IsNullOrWhiteSpace(telemetry.ClientKey))
                            {
                                telemetry.ClientKey = telemetry.SteamId > 0 && telemetry.IsSteamMode
                                    ? $"steam_{telemetry.SteamId}"
                                    : (!string.IsNullOrWhiteSpace(telemetry.Name) ? $"ip_{telemetry.Name}" : $"ip_{remoteIp}");
                            }
                            OnClientTelemetryReceived?.Invoke(telemetry);
                        }
                    }
                }

                byte[] respBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                await stream.WriteAsync(respBytes);
                await stream.FlushAsync();
            }
            catch
            {
                // Ignore malformed telemetry request
            }
        }
    }

    private static string BuildNginxConfig(
        AppSettings settings,
        string modHashFilePath,
        string hostInfoFilePath,
        List<ModHashEntry> entries,
        int port,
        int internalTelemetryPort = 0)
    {
        string baseLarian = PathResolverService.GetLarianBasePath(settings).Replace('\\', '/').TrimEnd('/');
        string? bg3Install = PathResolverService.GetBg3InstallDir(settings);
        string modHashForward = Path.GetFullPath(modHashFilePath).Replace('\\', '/');
        string hostInfoForward = Path.GetFullPath(hostInfoFilePath).Replace('\\', '/');

        var driveLetters = new SortedSet<char>();
        if (modHashForward.Length >= 2 && modHashForward[1] == ':')
            driveLetters.Add(char.ToUpperInvariant(modHashForward[0]));
        if (baseLarian.Length >= 2 && baseLarian[1] == ':')
            driveLetters.Add(char.ToUpperInvariant(baseLarian[0]));
        if (!string.IsNullOrWhiteSpace(bg3Install) && bg3Install.Length >= 2 && bg3Install[1] == ':')
            driveLetters.Add(char.ToUpperInvariant(bg3Install[0]));

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.DirectFilePath) && entry.DirectFilePath.Length >= 2 && entry.DirectFilePath[1] == ':')
            {
                driveLetters.Add(char.ToUpperInvariant(entry.DirectFilePath[0]));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("worker_processes  4;");
        sb.AppendLine("error_log  logs/bg3_error.log notice;");
        sb.AppendLine("pid        logs/nginx.pid;");
        sb.AppendLine();
        sb.AppendLine("events {");
        sb.AppendLine("    worker_connections  1024;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("http {");
        sb.AppendLine("    include       mime.types;");
        sb.AppendLine("    default_type  application/octet-stream;");
        sb.AppendLine("    log_format    bg3sync '$remote_addr:$remote_port [$time_local] \"$request\" $status $body_bytes_sent';");
        sb.AppendLine("    access_log    logs/bg3_access.log bg3sync;");
        sb.AppendLine("    sendfile        on;");
        sb.AppendLine("    sendfile_max_chunk 0;");
        sb.AppendLine("    tcp_nopush      on;");
        sb.AppendLine("    tcp_nodelay     on;");
        sb.AppendLine("    output_buffers  4 2m;");
        sb.AppendLine("    keepalive_timeout  65;");
        sb.AppendLine("    keepalive_requests 100000;");
        sb.AppendLine("    client_body_temp_path temp/client_body_temp;");
        sb.AppendLine("    proxy_temp_path       temp/proxy_temp;");
        sb.AppendLine("    fastcgi_temp_path     temp/fastcgi_temp;");
        sb.AppendLine("    uwsgi_temp_path       temp/uwsgi_temp;");
        sb.AppendLine("    scgi_temp_path        temp/scgi_temp;");
        sb.AppendLine();
        sb.AppendLine("    server {");
        sb.AppendLine($"        listen       0.0.0.0:{port};");
        sb.AppendLine("        server_name  localhost;");
        sb.AppendLine("        charset      utf-8;");
        sb.AppendLine();
        sb.AppendLine("        location = /health {");
        sb.AppendLine("            default_type text/plain;");
        sb.AppendLine("            add_header X-Client-Endpoint \"$remote_addr:$remote_port\" always;");
        sb.AppendLine("            return 200 'BG3ModSync Nginx OK';");
        sb.AppendLine("        }");
        sb.AppendLine();
        if (internalTelemetryPort > 0)
        {
            sb.AppendLine("        location = /telemetry {");
            sb.AppendLine($"            proxy_pass http://127.0.0.1:{internalTelemetryPort};");
            sb.AppendLine("            proxy_http_version 1.1;");
            sb.AppendLine("            proxy_set_header Connection \"\";");
            sb.AppendLine("            proxy_set_header X-Real-IP $remote_addr;");
            sb.AppendLine("            proxy_set_header X-Real-Port $remote_port;");
            sb.AppendLine("            add_header X-Client-Endpoint \"$remote_addr:$remote_port\" always;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }
        sb.AppendLine("        location = /host_info.json {");
        sb.AppendLine($"            alias \"{hostInfoForward}\";");
        sb.AppendLine("            default_type application/json;");
        sb.AppendLine("            add_header Cache-Control \"no-cache, no-store, must-revalidate\";");
        sb.AppendLine("            add_header X-Client-Endpoint \"$remote_addr:$remote_port\" always;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        location = /ModHash.txt {");
        sb.AppendLine($"            alias \"{modHashForward}\";");
        sb.AppendLine("            add_header Cache-Control \"no-cache, no-store, must-revalidate\";");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        location /larian/ {");
        sb.AppendLine($"            alias \"{baseLarian}/\";");
        sb.AppendLine("        }");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(bg3Install))
        {
            string binForward = Path.Combine(bg3Install, "bin").Replace('\\', '/').TrimEnd('/');
            if (Directory.Exists(binForward))
            {
                sb.AppendLine("        location /gamedata/ {");
                sb.AppendLine($"            alias \"{binForward}/\";");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        foreach (char drv in driveLetters)
        {
            sb.AppendLine($"        location /drive/{drv}/ {{");
            sb.AppendLine($"            alias \"{drv}:/\";");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private void StartAccessLogTailer(string accessLogPath, Action<string> logCallback)
    {
        _logTailCts?.Cancel();
        _logTailCts = new CancellationTokenSource();
        var token = _logTailCts.Token;

        _ = Task.Run(async () =>
        {
            long lastPos = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(accessLogPath))
                    {
                        using var fs = new FileStream(accessLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (fs.Length > lastPos)
                        {
                            fs.Seek(lastPos, SeekOrigin.Begin);
                            using var sr = new StreamReader(fs, Encoding.UTF8);
                            string? line;
                            while ((line = await sr.ReadLineAsync(token)) != null)
                            {
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    string remoteEndpoint = "";
                                    string remoteIp = "";
                                    int spaceIdx = line.IndexOf(' ');
                                    if (spaceIdx > 0)
                                    {
                                        remoteEndpoint = line.Substring(0, spaceIdx).Trim();
                                        int cIdx = remoteEndpoint.LastIndexOf(':');
                                        remoteIp = cIdx > 0 ? remoteEndpoint.Substring(0, cIdx) : remoteEndpoint;
                                        if (!string.IsNullOrEmpty(remoteIp) && remoteIp != "127.0.0.1")
                                        {
                                            OnClientHttpConnected?.Invoke(remoteEndpoint);
                                        }
                                    }

                                    int telIdx = line.IndexOf("?telemetry=", StringComparison.Ordinal);
                                    if (telIdx >= 0)
                                    {
                                        int start = telIdx + 11;
                                        int end = line.IndexOf(' ', start);
                                        if (end > start)
                                        {
                                            string encoded = line.Substring(start, end - start).Trim();
                                            try
                                            {
                                                string b64 = Uri.UnescapeDataString(encoded);
                                                byte[] jsonBytes = Convert.FromBase64String(b64);
                                                var telemetry = JsonSerializer.Deserialize<ClientLobbyTelemetry>(jsonBytes);
                                                if (telemetry != null)
                                                {
                                                    if (!string.IsNullOrEmpty(remoteIp) && remoteIp != "127.0.0.1")
                                                    {
                                                        telemetry.IpAndPort = remoteEndpoint;
                                                    }
                                                    if (string.IsNullOrWhiteSpace(telemetry.ClientKey))
                                                    {
                                                        telemetry.ClientKey = telemetry.SteamId > 0
                                                            ? $"steam_{telemetry.SteamId}"
                                                            : (!string.IsNullOrEmpty(remoteIp) ? $"ip_{remoteIp}" : "client");
                                                    }
                                                    OnClientTelemetryReceived?.Invoke(telemetry);
                                                }
                                            }
                                            catch { }
                                        }
                                    }

                                        if (!line.Contains("/health", StringComparison.Ordinal) &&
                                        !line.Contains("/telemetry", StringComparison.Ordinal) &&
                                        !line.Contains("/host_info.json", StringComparison.Ordinal))
                                    {
                                        string unescaped = Uri.UnescapeDataString(line);
                                        logCallback($"[NGINX-HTTP] {unescaped}");
                                    }
                                }
                            }
                            lastPos = fs.Position;
                        }
                    }
                }
                catch
                {
                    // Ignore transient file access errors
                }
                await Task.Delay(150, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, token);
    }

    private static void EnsureFirewallRule(string nginxExePath, int port, Action<string> logCallback)
    {
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
                proc.WaitForExit(2000);
                return proc.HasExited ? proc.ExitCode : -1;
            }

            if (RunNetsh("advfirewall firewall show rule name=\"BG3ModSync Portable Nginx\"") != 0)
            {
                RunNetsh($"advfirewall firewall add rule name=\"BG3ModSync Portable Nginx\" dir=in action=allow program=\"{nginxExePath}\" enable=yes profile=any");
            }

            string portRuleName = $"BG3ModSync Portable Nginx ({port})";
            if (RunNetsh($"advfirewall firewall show rule name=\"{portRuleName}\"") != 0)
            {
                RunNetsh($"advfirewall firewall add rule name=\"{portRuleName}\" dir=in action=allow protocol=TCP localport={port}");
            }
        }
        catch
        {
            // Non-fatal if not running as Admin
        }
    }

    public void StopServer(Action<string>? logCallback = null, bool silent = false)
    {
        try
        {
            _logTailCts?.Cancel();
            _logTailCts = null;

            try { _telemetryListener?.Stop(); } catch { }
            _telemetryListener = null;
            _internalTelemetryPort = 0;

            string nginxDir = _activeNginxDir ?? LocateNginxDirectory();
            string exePath = Path.Combine(nginxDir, "nginx.exe");
            if (File.Exists(exePath) && (_nginxProcess != null && !_nginxProcess.HasExited))
            {
                var stopPsi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"-p \"{nginxDir}\" -c conf/{ConfigFileName} -s stop",
                    WorkingDirectory = nginxDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var stopProc = Process.Start(stopPsi);
                stopProc?.WaitForExit(2000);
            }

            if (_nginxProcess != null && !_nginxProcess.HasExited)
            {
                _nginxProcess.Kill(entireProcessTree: true);
            }
            _nginxProcess = null;

            // Also clean up any orphaned nginx worker processes launched from our tools folder
            foreach (var p in Process.GetProcessesByName("nginx"))
            {
                try
                {
                    string? fullPath = p.MainModule?.FileName;
                    if (fullPath != null && fullPath.StartsWith(nginxDir, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                }
                catch
                {
                    // Ignore
                }
            }

            if (!silent)
            {
                logCallback?.Invoke("[NGINX] Portable Nginx HTTP server stopped.");
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                logCallback?.Invoke($"[WARN] Error stopping Nginx: {ex.Message}");
            }
        }
    }

    public static List<string> GetLocalIpAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        ips.Add(ua.Address.ToString());
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        if (ips.Count == 0) ips.Add("127.0.0.1");
        return ips.Distinct().ToList();
    }

    public void Dispose()
    {
        StopServer(silent: true);
    }
}
