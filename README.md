# BG3ModSync — Zero-Copy Nginx HTTP, Steam P2P & VPN Edition

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Latest Release](https://img.shields.io/github/v/release/Alyciazsa/BG3-Mod-Sync-Utility?label=Release&color=brightgreen)](https://github.com/Alyciazsa/BG3-Mod-Sync-Utility/releases/latest)
[![.NET 10 Windows](https://img.shields.io/badge/.NET-10.0%20Windows%20Desktop-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform Windows x64](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D4.svg)](https://www.microsoft.com/windows)
[![Transport Modes](https://img.shields.io/badge/Transport-Nginx%20HTTP%20%7C%20Steam%20ICE%20%7C%20VPN-success.svg)](#-connection-modes--capabilities)
[![100% Peer-to-Peer](https://img.shields.io/badge/Privacy-100%25%20Direct%20P2P-orange.svg)](SECURITY.md)

A standalone Windows WPF (WinUI 3 Dark Fluent) desktop utility for synchronizing **Baldur's Gate 3** mod setups between Host and Client players at full line speed—without zipping archives, uploading 30+ GB packs to cloud storage, or duplicating files on disk.

https://www.nexusmods.com/baldursgate3/mods/25251

Synchronizes every critical mod artifact automatically:
- **Mod Archives:** `Mods/*.pak` (including **Vortex Mod Manager symlinks**)
- **Load Order:** `PlayerProfiles/Public/modsettings.lsx`
- **Script Extender & Configs:** `Script Extender/*.json` (and Larian configuration files)
- **Native DLLs / Bin Overrides:** `Baldurs Gate 3/bin/bink2w64.dll`, `DWrite.dll`, and `NativeMods/*`

> ⚡ **Single-File Portable Utility:** `BG3ModSync.exe` bundles **Portable Nginx 1.30.6** and all dependencies into a single self-contained `.exe`. No web server install, no manual folder sharing, and **0 bytes copied** on the Host machine.

<p align="center">
  <img src="src/BG3ModSync/Assets/logo.png" alt="BG3ModSync Application Logo" width="128"/>
</p>

---

## 🎬 Video Showcase & Walkthrough

<p align="center">
  <a href="https://www.youtube.com/watch?v=no7XMK9Rz4c">
    <img src="https://img.youtube.com/vi/no7XMK9Rz4c/maxresdefault.jpg" alt="BG3ModSync Video Showcase & Setup Guide" width="720"/>
  </a>
</p>
<p align="center">
  <em>▶ Click the thumbnail above to watch the full setup &amp; synchronization walkthrough on YouTube (<a href="https://youtu.be/no7XMK9Rz4c">youtu.be/no7XMK9Rz4c</a>)</em>
</p>

---

## 📋 Connection Modes & Capabilities

| Feature / Mode | **🛡️ VPN IP (Broadcast + Find Host)** | **🌐 Steam P2P (`BG3-...` / Select Host)** | **🌍 Direct IP (WAN / LAN + UPnP)** |
| :--- | :--- | :--- | :--- |
| **Best Network Scenario** | Both peers behind CGNAT, 4G/5G hotspots, or strict NAT | Standard residential NAT or LAN friends on Steam | Same Local LAN or Host with Public IPv4 (UPnP / Port Forward) |
| **Supported Networks** | **Tailscale, ZeroTier, NetBird, Radmin VPN, Hamachi, WireGuard, OpenVPN**, SoftEther, Nebula | Valve Steam Matchmaking Lobby + WebRTC Direct UDP ICE (`AppID 1086940` / `480`) | Direct IPv4 over Local Ethernet/Wi-Fi (`[LAN]`) or Public Internet (`[WAN]`) |
| **One-Click Host Discovery** | ✅ **`🔍 Find Host`** (UDP `27001` + multicast + mesh peer scan, **zero LAN leak**) | ✅ **`🎯 Select Host`** (Online Steam friends in same Game/Lobby) | Direct Host WAN/LAN IP entry (auto-scanned adapter dropdown) |
| **Port Configuration** | Configurable static TCP port (default `27000`; discovery on `UDP 27001`) | Automatic ephemeral TCP port (`useAutoPort`) — port box hidden | Configurable static TCP port (default `27000`) + 1-click **`📡 UPnP`** |
| **Dual Symmetric CGNAT** | ✅ **Works cleanly** (peer-to-peer virtual tunnel) | ❌ Bulk `.pak` relay blocked (`Direct-Only` prevents Valve SDR throttle/abuse) | Requires Host Public WAN + Port Forward/UPnP |
| **Bulk `.pak` Data Plane** | **Direct Nginx HTTP over VPN Tunnel** (Full tunnel speed) | **Stage 1 Direct HTTP Handoff** or **Stage 2 Direct UDP ICE Tunnel** | **Direct Nginx HTTP** (Multi-stream HTTP Range on SSD) |
| **Steam Status Hook** | **Standby (`RunningAppID = 0`)** — Steam stays offline/untouched | **Deferred Hook** — attaches only on `HOST` / `CLIENT` / Friend picker | **Standby (`RunningAppID = 0`)** — Steam stays offline/untouched |

---

## 📌 Why This Tool Is Needed

Multiplayer campaigns in *Baldur's Gate 3* fail immediately if even a single `.pak` checksum, `modsettings.lsx` entry, Script Extender config, or `NativeMods` DLL differs between Host and Clients. Managing 200–700+ mods manually creates major friction:

- **Cloud Uploads Don't Scale:** Compressing and uploading 30+ GB of mods to Google Drive / MEGA every time you add or update two files wastes hours and hits storage/bandwidth limits.
- **Standard File Sharing Breaks on Vortex & Mixed Drives:** Vortex stores `.pak` mods as NTFS symlinks across separate drives (`C:\`, `D:\`, `X:\`), while mixed SSD/HDD setups crawl if hashed or written sequentially vs. multi-threaded incorrectly.
- **Carrier CGNAT & Mobile Hotspots:** Many modern ISPs and 5G/LTE hotspots place players behind Symmetric CGNAT without UPnP, breaking naive port-forwarding guides.

**BG3ModSync** solves all three problems:
1. The **Host** hashes local files in-place with **XXH3-64**, generates `ModHash.txt` mapping each file's direct disk path, and serves them directly from disk via **Zero-Copy Portable Nginx**.
2. The **Client** downloads only the tiny `ModHash.txt` manifest, diffs every file locally, deletes obsolete mods safely, and streams missing or changed files directly over HTTP (multi-segment `Range` requests on SSD, sequential large buffers on HDD).
3. Flexible **VPN Broadcast + `Find Host`**, **Direct IP + UPnP**, and **Steam P2P Direct UDP ICE** let every group connect without relying on third-party file hosts.

---

## ✨ Key Features

- **Zero-Copy Portable Nginx Server:** Dynamically writes `bg3modsync_nginx.conf` inside `%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\.bg3modsync_runtime` mapping drive roots directly via `alias`. **Zero files are copied**, and the runtime directory + Nginx processes are wiped automatically on exit.
- **Storage-Aware XXH3-64 Hashing & Delta Sync:** Uses Win32 `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceSeekPenaltyProperty`) and symlink target resolution (`ResolveRealDirectoryPath`) to select SSD parallel workers (`8` hash workers, `4` download streams with `Range` segmentation > `32 MB`) vs. sequential HDD buffers (`1` worker, `4–16 MB` chunks). Rejects truncated HTTP responses before moving `.part` files into place.
- **Multi-VPN Broadcast & `🔍 Find Host` Browser:** Hosts in VPN mode broadcast presence every `3s` on `UDP 27001` strictly across `[VPN]` adapters (`IP_UNICAST_IF` / `IP_MULTICAST_IF` — zero LAN leak). Clients click **`🔍 Find Host`** to auto-discover hosts across Tailscale, ZeroTier, NetBird, Radmin VPN, Hamachi, WireGuard, OpenVPN, SoftEther, and Nebula, auto-filling the matching local VPN interface, Target IP, and Nginx Port in one click.
- **Two-Stage Steam P2P & 5-Layer CGNAT Detector:** Defers Steam initialization off app startup (`Standby` until `HOST`/`CLIENT`), preloads `steamwebrtc64.dll` for Direct UDP ICE hole-punching, upgrades to direct HTTP when Host endpoints are reachable, enforces Direct-Only bulk transfers, and runs a 5-layer CGNAT check (`UPnP IGD` mismatch, RFC 6598, Dual-STUN RFC 5389, Mobile/Tethering adapter detection, and ICMP TTL hop inspection).
- **Live Session Lobby & Telemetry:** Displays Host Crown card and connected Clients with source `IP:Port`, `⚠ CGNAT` badge when behind carrier NAT/relay, live RTT ping (`1s` loop), live speed (`MB/s`), and per-client sync progress bars.
- **One-Click `▶ PLAY BG3` (Vulkan / DX11):** Cleanly stops any active session, unhooks Steam AppID back to **Online**, and launches `bin\bg3.exe` (Vulkan) or `bin\bg3_dx11.exe` (DirectX 11).
- **Backup, Restore & Exclusion Rules:** Full backup/restore manager to any drive letter (`<Drive>:\BG3MOD`) or custom path, plus customizable per-category exclusion filters (`PAK`, `JSON`, `GAMEDATA`, `LOADORDER`).

---

## 📁 Repository Structure

```text
BG3ModSync/
├── BG3ModSync.sln                                  # Visual Studio / .NET 10 Solution
├── README.md                                       # Project documentation
├── SECURITY.md                                     # Security & direct P2P privacy policy
├── LICENSE                                         # MIT License (Copyright 2026 Alyciazsa)
├── .gitignore                                      # Git ignore rules (.exe artifacts excluded for Releases)
├── release/
│   └── BG3ModSync.exe                              # Single-file self-contained Windows x64 binary (via Builds/Releases)
├── tools/
│   └── nginx/                                      # Bundled Portable Nginx 1.30.6 (embedded into BG3ModSync.exe)
└── src/
    └── BG3ModSync/
        ├── BG3ModSync.csproj                       # .NET 10 WPF single-file project (Facepunch.Steamworks + System.IO.Hashing)
        ├── AssemblyInfo.cs
        ├── App.xaml / App.xaml.cs                  # WinUI 3 Dark Fluent styles & OnMainWindowClose clean exit
        ├── MainWindow.xaml / MainWindow.xaml.cs    # Hero controls, Mode bar, Live Session Lobby & Host/Client workflows
        ├── steam_appid.txt                         # Fallback AppID reference (overridden dynamically at runtime)
        ├── Assets/
        │   ├── app.ico
        │   └── logo.png
        ├── Models/
        │   ├── AppSettings.cs                      # Persistent settings (%LOCALAPPDATA%\...\ModSyncSettings.json)
        │   ├── BackupItem.cs                       # Backup archive & drive storage models
        │   ├── LobbyModels.cs                      # Telemetry & INotifyPropertyChanged lobby view-models
        │   └── ModHashEntry.cs                     # 4-column ModHash.txt parser & /drive/<X>/ URL encoder
        ├── Services/
        │   ├── BackupRestoreService.cs             # Async symlink-safe mod backup & restore engine
        │   ├── HttpSyncClientService.cs            # Parallel HTTP Range & sequential delta sync downloader
        │   ├── ModHashService.cs                   # Storage-tuned multi-threaded XXH3-64 scanner & ModHash writer
        │   ├── NetworkDiscoveryService.cs          # WAN/LAN/VPN classification, 5-layer CGNAT detector & IP_UNICAST_IF sockets
        │   ├── NginxServerService.cs               # Zero-copy Nginx config builder, /telemetry proxy & firewall rule helper
        │   ├── PathResolverService.cs              # Larian/BG3 path auto-detection & Win32 SSD SeekPenalty detector
        │   ├── RuntimeTempCleanupService.cs        # Isolated hidden .bg3modsync_runtime lifecycle & exit cleanup
        │   ├── SteamP2PRelayService.cs             # Steam Lobby matchmaking, Stage 1 Handoff & Stage 2 WebRTC ICE multiplexer
        │   ├── UpnpPortForwardService.cs           # SSDP/SOAP UPnP IGD port mapper & WAN IP detector
        │   └── VpnHostDiscoveryService.cs          # UDP 27001 VPN beacon/responder & Tailscale/ZeroTier/NetBird/ARP/Route discovery
        └── Views/
            ├── ActivityLogWindow.xaml / .cs        # Pop-out real-time activity log viewer
            ├── BackupRestoreWindow.xaml / .cs      # Backup / Restore manager modal
            ├── ExcludeWindow.xaml / .cs            # Per-category exclusion rule editor
            ├── SettingsWindow.xaml / .cs           # Larian AppData, BG3 install folder & runtime path confirmation
            ├── SteamAppIdWindow.xaml / .cs         # Steam AppID switcher (1086940 BG3 / 480 Spacewar / Custom)
            ├── SteamFriendsWindow.xaml / .cs       # Online Steam friends & active Lobby host picker
            └── VpnHostBrowserWindow.xaml / .cs     # Live VPN network Host browser ("Find Host")
```

---

## 🚀 Step-by-Step Guide

### First-Time Setup
1. Download `BG3ModSync.exe` from **[GitHub Releases](https://github.com/Alyciazsa/BG3-Mod-Sync-Utility/releases/latest)** (or build from source below) and run it—no installation required.
2. On first launch, confirm the auto-detected **Larian Studios AppData Path** and **Baldur's Gate 3 Game Directory** in the setup prompt.
3. Wait for the startup scan in the top-right status card showing detected **`WAN`**, **`LAN`**, **`VPN`** interfaces, and **`NAT/CGNAT Status`**.

---

### Method 1: 🛡️ VPN IP Mode + `Find Host` (Recommended for CGNAT / Any ISP)

Works identically across **Tailscale, ZeroTier, NetBird, Radmin VPN, Hamachi, WireGuard, OpenVPN**, SoftEther, and Nebula—even if both Host and Client are behind mobile 5G/ISP CGNAT.

* **On Host PC:**
  1. Ensure your virtual VPN client is connected (click **`↻ Refresh`** if you connected after opening the app).
  2. Select **`🛡️ VPN IP`** mode and confirm your listening **`Port:`** (default `27000`).
  3. Click **`📡 HOST`**. Portable Nginx starts and `BG3ModSync` automatically begins broadcasting your Host presence across active VPN interfaces on `UDP 27001`.
* **On Client PC:**
  1. Select **`🛡️ VPN IP`** mode.
  2. Click **`🔍 Find Host`** to open the live VPN Network Browser (auto-refreshes every `4s`).
  3. Wait for the Host card to appear (shows Host name, `[Provider]` badge, `VpnIp:Port`, mod count/GB, and live ping), then double-click it or click **`🎯 Select Host`**.
  4. Verify the matching local VPN interface, target **VPN IP**, and **Port** auto-fill, then click **`⚡ CLIENT`** to sync.

---

### Method 2: 🌐 Steam P2P Mode (`BG3-...` Room Code / `Select Host`)

Best when players own Baldur's Gate 3 on Steam (default `AppID 1086940`) or use Spacewar (`AppID 480`) across standard residential NAT / UPnP networks.

* **AppID Choice (Optional):** Click **`🆔 AppID: ...`** at the bottom to toggle between **Baldur's Gate 3 (`1086940`)** (default, keeps real BG3 Steam integration) or **Spacewar (`480`)** (for GOG/non-Steam tests with Steam running).
* **On Host PC:**
  1. Select **`🌐 Steam P2P`** and click **`📡 HOST`**.
  2. Steam hooks on demand, Portable Nginx binds an automatic free TCP port + UPnP (if available), and a unique **`BG3-...`** Room Code appears in **`Room / ID:`**.
  3. Share the Room Code (via **`📋 Copy Code`**) or ask friends to pick you from their Steam friends list.
* **On Client PC:**
  1. Select **`🌐 Steam P2P`**.
  2. Either paste the Host's `BG3-...` Room Code / 17-digit SteamID64, **or** click **`🎯 Select Host`** to select your online Steam friend directly.
  3. Click **`⚡ CLIENT`**. The client automatically tests Direct HTTP Handoff first, then punches Direct UDP ICE through NAT via `ISteamNetworkingSockets`.

---

### Method 3: 🌍 Direct IP Mode (Local LAN or Public WAN + `UPnP`)

* **Same Household / LAN:** Select **`🌍 Direct IP`**, select your **`[LAN]`** interface, click **`📡 HOST`**, give your LAN IP (`192.168.1.50`) and Port (`27000`) to friends on the same network, and click **`⚡ CLIENT`**.
* **Public Internet (`[WAN]`):** Select your **`[WAN]`** interface (requires a real non-CGNAT Public IPv4), click **`📡 UPnP`** (or forward TCP `27000` manually on your router), and click **`📡 HOST`**. Clients enter your WAN IP and Port `27000`.

---

### Method 4: Playing & Managing Backups

- **Launch Game Cleanly:** Before starting Baldur's Gate 3, choose **Vulkan (`bg3.exe`)** or **DX11 (`bg3_dx11.exe`)** at the top-center bar and click **`▶ PLAY BG3`**. Any active server stops cleanly and Steam status unhooks back to **Online**.
- **Backups:** Click **`📦 Backup`** to create snapshots (`Backup-<Name>-<Count>`), restore an earlier mod pack, or switch backup drive letters (`<Drive>:\BG3MOD`).
- **Exclusions:** Click **`🚫 Exclude`** to prevent specific files/patterns (e.g., `english.pak`, `.log`, `Armory`, local presets) from being synced or deleted.

---

## 🖥️ Example Activity Log Output

### Host & Client VPN Sync Example (Anonymized)

```text
================================================================================
  BALDUR'S GATE 3 MOD SYNC UTILITY — PORTABLE NGINX & STEAM P2P EDITION
================================================================================
[INIT] Larian AppData Path : C:\Users\Player\AppData\Local\Larian Studios\Baldur's Gate 3
[INIT] BG3 Game Directory  : D:\SteamLibrary\steamapps\common\Baldurs Gate 3
[INIT] Active Mods (.pak)  : 651 files (29.52 GB — includes 543 Vortex symlinked files)
[INIT] Backup Location     : C:\BG3MOD
[INIT] Active Steam AppID  : Baldur's Gate 3 (1086940)
[INIT] Steam Game Hook    : Standby (Hooks Baldur's Gate 3 (1086940) only on HOST / CLIENT)
[NET]  Scanning available WAN, LAN, and VPN network interfaces...
[NET]  [WAN]  Public Internet (WAN)        : 203.0.113.45 (External Public IPv4 Address)
[NET]  [LAN]  Ethernet                     : 192.168.1.50 (PCIe 2.5GbE Family Controller)
[NET]  [VPN]  Tailscale                    : 100.64.12.34 (Tailscale Tunnel)
[NET]  NAT/CGNAT Status : Direct / Standard NAT — Standard NAT / Direct Public IP (Public WAN: 203.0.113.45)
[READY] Select your Connection Mode (Steam Hole-Punch / WAN-LAN IP / VPN IP) and click HOST or CLIENT.
--------------------------------------------------------------------------------
--> STARTING HOST MODE (Port 27000 — Zero-Copy Portable Nginx HTTP + Steam P2P)
[NGINX] Generated zero-copy Nginx configuration at: C:\Users\Player\AppData\Local\Larian Studios\Baldur's Gate 3\.bg3modsync_runtime\nginx\conf\bg3modsync_nginx.conf
[NGINX] Direct file locations mapped from ModHash.txt (0 files copied to server storage).
[NGINX] Portable Nginx HTTP Server is ONLINE on port 27000 (PID 24180).
[VPN-DISC] Host VPN Broadcast & Discovery Responder ONLINE on UDP 27001 (Tailscale:100.64.12.34).
[HASH] Storage Mode: X: (Symlinked from D:) [SSD] (8 Workers, Buffer 1 MB)
[HASH] LOADORDER  : [============] 100%   1/1   [0.0s]
[HASH] PAK        : [============] 100%   651/651   [5.6s]
[HASH] JSON       : [============] 100%   14/14   [0.0s]
[HASH] GAMEDATA   : [============] 100%   12/12   [0.3s]
[NGINX] Reloaded zero-copy Nginx configuration with 678 file entries on port 27000.
```

---

## 🔬 Architecture & Networking Deep Dive

<details>
<summary><b>▶ Click to expand: Zero-Copy Nginx Alias Mapping & XXH3-64 Storage Engine</b></summary>

<br>

Inspecting [`ModHashService.cs`](src/BG3ModSync/Services/ModHashService.cs), [`NginxServerService.cs`](src/BG3ModSync/Services/NginxServerService.cs), [`PathResolverService.cs`](src/BG3ModSync/Services/PathResolverService.cs), and [`HttpSyncClientService.cs`](src/BG3ModSync/Services/HttpSyncClientService.cs) highlights the zero-copy data pipeline:

1. **4-Column `ModHash.txt` Format:**
   ```text
   {LOADORDER} {0123456789abcdef} {modsettings.lsx} {C:\Users\Player\AppData\Local\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\modsettings.lsx}
   {PAK} {fedcba9876543210} {ExampleMod.pak} {X:\Vortex Mods\baldursgate3\ExampleMod\ExampleMod.pak}
   ```
   When Nginx is configured (`BuildNginxConfig`), every unique Windows drive letter referenced in `DirectFilePath` is exposed read-only via Nginx's `alias` directive (`/drive/<Letter>/` with `sendfile on; output_buffers 4 2m; keepalive_requests 100000;`), while `/larian/` and `/gamedata/` provide fallback relative routing.

2. **Win32 Storage Detection & Symlink Resolution:**
   - `PathResolverService.IsSsdDrive` resolves NTFS symlinks/junctions to their physical drive letter and issues `DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceSeekPenaltyProperty)` (`IncursSeekPenalty == 0`) without requiring administrator rights.
   - **SSD Hosts:** `8` parallel workers using `ArrayPool<byte>` (`1 MB` buffers); **SSD Clients:** up to `4` concurrent files, splitting payloads `> 32 MB` into `4` parallel HTTP `Range` streams via `RandomAccess.WriteAsync`.
   - **HDD Hosts/Clients:** `1` sequential worker with `4 MB` scan / `16 MB` download buffers to prevent disk head thrashing.

3. **Integrity Enforcement:**
   - All downloads stream into `.bg3_part_<guid>.tmp`, verify `totalRead == fullFileBytes` before atomic replacement (`HttpSyncClientService.cs`), and keep `ModHash.txt` strictly accurate if any transfer fails.

</details>

---

<details>
<summary><b>▶ Click to expand: Two-Stage Steam P2P Handshake, Direct UDP ICE & 5-Layer CGNAT Detector</b></summary>

<br>

[`SteamP2PRelayService.cs`](src/BG3ModSync/Services/SteamP2PRelayService.cs) and [`NetworkDiscoveryService.cs`](src/BG3ModSync/Services/NetworkDiscoveryService.cs) implement a hybrid P2P stack on top of `Facepunch.Steamworks`:

```text
[Client clicks CLIENT] ──► (Join Lobby / Read host_info) ──► [Stage 1: Probe Host WAN/LAN/VPN HTTP]
                                                                    │
                                                Unreachable (NAT) ◄─┘
                                                                    ▼
                                                    [Stage 2: SteamNetworkingSockets UDP ICE]
                                                         (Preloaded steamwebrtc64.dll)
```

1. **Deferred Hook & Standby Design:**
   - App launch leaves `SteamClient` uninitialized (`RunningAppID = 0`), reading only `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser` for **`🆔 SteamID`**.
   - `TryInitialize()` runs only when starting Steam P2P `HOST`/`CLIENT` or opening `SteamFriendsWindow`, and detaches on `STOP` or **`▶ PLAY BG3`**.
2. **Direct UDP ICE Configuration:**
   - Preloads `<SteamPath>\steamwebrtc64.dll` via `LoadLibraryW` before `SteamClient.Init` so WebRTC ICE never fails with missing-DLL `3999`.
   - Tunes `SteamNetworkingUtils` for throughput & NAT traversal: `P2P_Transport_ICE_Enable (104) = 6` (`Private | Public`), `ICE_Penalty (105) = 0`, `SDR_Penalty (106) = 1000`, Valve Auto-STUN, `LogLevel_P2PRendezvous (17) = 5`, `DualWifi_Enable (39) = 0`, `SendBufferSize (9) = 64 MB`, `SendRateMin (10) = 256 KB/s`, `SendRateMax (11) = 64 MB/s`, `NagleTime (12) = 0`, `RecvBufferSize (47) = 4 MB`, `RecvBufferMessages (48) = 4096`.
   - Stage 2 TCP-over-P2P multiplexing uses per-stream bounded FIFO `Channel<byte[]>` queues and `Result.LimitExceeded` backpressure retries (`256 KB` chunks) over a `127.0.0.1` Nginx tunnel.
   - Enforces a **Direct-Only Policy** whenever `ConnectionInfo.popRelay != 0` mid-session or at connect time so 30+ GB bulk transfers never saturate Valve's SDR game relays.
3. **5-Layer Local CGNAT Detector (`DetectLocalCgnatAsync`):**
   - **Layer 1:** UPnP IGD External IP vs. HTTP Public WAN IP comparison.
   - **Layer 2:** Non-VPN physical/cellular interface check for RFC 6598 (`100.64.0.0/10`).
   - **Layer 3:** Dual-STUN RFC 5389 probe from one bound UDP socket (`stun.l.google.com:19302` vs. `stun.cloudflare.com:3478`) detecting Symmetric NAT port mapping variance.
   - **Layer 4:** Mobile/Cellular USB tether (`RNDIS`, `WWAN`, `Apple Mobile Device`) and phone hotspot subnet detection.
   - **Layer 5:** Bounded ICMP TTL `2..4` carrier hop check for ISP CGNAT relays.

</details>

---

<details>
<summary><b>▶ Click to expand: Multi-VPN Broadcast & Zero-LAN-Leak Host Discovery (UDP 27001)</b></summary>

<br>

[`VpnHostDiscoveryService.cs`](src/BG3ModSync/Services/VpnHostDiscoveryService.cs) discovers online `BG3ModSync` hosts across heterogeneous VPN topologies without scanning port ranges or touching physical LAN hardware:

1. **Host Responder & Periodic VPN Broadcast (`UDP 27001`):**
   - Host binds `0.0.0.0:27001` (`SIO_UDP_CONNRESET` disabled) and joins multicast `239.255.66.71` on `[VPN]` adapters.
   - In **VPN IP** mode, `HostBroadcastLoopAsync` sends `BG3MODSYNC_VPN_HOST_V1` every `3s` over interface-locked UDP sockets (`IP_UNICAST_IF` / `IP_MULTICAST_IF`) to directed subnet broadcasts, Radmin (`26.255.255.255`), Hamachi (`25.255.255.255`), multicast `239.255.66.71`, and cached online Tailscale/NetBird peers.
2. **Client Multi-Adapter Peer Enumeration & Discovery Window:**
   - **Tailscale:** Queries `tailscale status --json` (`Online`/`Active` vs. offline nodes).
   - **NetBird:** Queries `netbird status -d --json`.
   - **ZeroTier:** Queries local service HTTP API `http://127.0.0.1:9993/network` using `authtoken.secret`.
   - **Radmin VPN / Hamachi / WireGuard / OpenVPN / TAP / TUN:** Reads Win32 ARP cache (`GetIpNetTable` in `iphlpapi.dll`), Win32 IPv4 `/32` & `/24`–`/30` routes (`GetIpForwardTable`), adapter gateway/DHCP peers strictly filtered by `[VPN]` `InterfaceIndex`, plus `%LOCALAPPDATA%\LogMeIn Hamachi\*.ini|*.cfg`.
   - **Explicit LAN Exclusion:** Rejects every candidate IP or route overlapping any `[LAN]` adapter subnet.
   - **Port-27001 Coexistence & Non-Default Port Match:** When `UDP 27001` is already bound locally (same-PC hosting), Client scans avoid double-binding `:27001` (`ExclusiveAddressUse` collision avoidance) and rely on per-VPN ephemeral UDP sockets + loopback responder probe. UDP responses carry the Host's active `NginxPort`, verified via a bounded single-port `GET /host_info.json` request (`<= 32` online peers, concurrency `8`, `850ms` timeout).

</details>

---

## 🛠️ Building from Source

### Prerequisites
- **OS:** Windows 10 / Windows 11 (`win-x64`)
- **SDK:** [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (with Windows Desktop / WPF workload)

### Build & Publish Commands

```powershell
# Standard Release build
dotnet build BG3ModSync.sln -c Release

# Publish standalone single-file executable (embeds Portable Nginx 1.30.6 + .NET 10 runtime)
dotnet publish src/BG3ModSync/BG3ModSync.csproj -c Release -r win-x64 -o release
```

The single-file self-contained binary is output to `release\BG3ModSync.exe`.

---

## 📚 Credits & Acknowledgements

1. **[AlyciaX / @Alyciazsa](https://github.com/Alyciazsa)** — Project creator, networking/P2P/VPN architecture & WinUI 3 interface ([Nexus Mods Profile](https://www.nexusmods.com/profile/AlyciaXY)).
2. **[Nginx](https://nginx.org/)** — Bundled Portable HTTP Server (`1.30.6`) powering zero-copy disk-alias transfers.
3. **[Facepunch.Steamworks](https://github.com/Facepunch/Facepunch.Steamworks)** — C# Steamworks wrapper enabling Steam Lobby matchmaking and `ISteamNetworkingSockets` WebRTC ICE transport.
4. **.NET `System.IO.Hashing`** — Hardware-accelerated `XxHash3` implementation.

---

## ⚖️ License & Disclaimer

Distributed under the [MIT License](LICENSE).

*This project is an independent open-source community utility and is not affiliated with, endorsed by, or associated with Larian Studios or Valve Corporation. Always keep backups of your mod folder (`📦 Backup`) before synchronizing large mod packs.*
