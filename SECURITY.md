# Security Policy

> **Looking for the mod or downloads?**
> - **Main Project Page:** [BG3-Mod-Sync-Utility README](https://github.com/Alyciazsa/BG3-Mod-Sync-Utility#readme)
> - **GitHub Releases:** [Latest Releases](https://github.com/Alyciazsa/BG3-Mod-Sync-Utility/releases)
> - **Nexus Mods:** [https://www.nexusmods.com/baldursgate3/mods/25251](https://www.nexusmods.com/baldursgate3/mods/25251)


## 🔒 Privacy & Direct Peer-to-Peer Guarantee
All mod hashing, indexing, discovery, and file transfers in **BG3ModSync** execute **100% locally and directly peer-to-peer** between Host and Client machines:

- **Zero Cloud Storage or Telemetry Servers:** Your mod files (`.pak`), load orders (`modsettings.lsx`), hashes (`ModHash.txt`), local file paths, and connection endpoints are never uploaded to third-party servers, cloud storage, or external analytics backends.
- **Scoped Zero-Copy Nginx HTTP Serving:** Portable Nginx runs exclusively while hosting is active inside an isolated runtime directory (`%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\.bg3modsync_runtime`), serves only explicit read-only routes (`ModHash.txt`, `/drive/<Letter>/`, `/larian/`, `/gamedata/`, `/host_info.json`, `/health`, `/telemetry`), and is stopped and cleaned up on exit.
- **Strict LAN Isolation in VPN Discovery:** `VpnHostDiscoveryService` binds `UDP 27001` broadcast and multicast (`239.255.66.71`) packets directly to active virtual VPN interfaces via WinSock `IP_UNICAST_IF` / `IP_MULTICAST_IF` and explicitly excludes physical LAN subnets so discovery probes never leak onto local routers or smart-home devices.
- **Strict Direct-Only Policy for Steam P2P Bulk Transfers:** Bulk `.pak` downloads only proceed over negotiated Direct HTTP Handoff or Direct UDP ICE hole-punching and refuse Valve SDR relays to prevent circuit throttling or abuse flags on multi-gigabyte mod packs.
- **Auditable Source:** The entire C# / WPF (`.NET 10`) source code is unminified and open for inspection.

## 📦 Supported Versions

| Version | Supported |
| :--- | :--- |
| `2.0.x` (Portable Nginx HTTP + Steam ICE + VPN Broadcast) | ✅ Supported |
| `< 2.0.0` | ❌ Unsupported |

## 🛡️ Reporting a Vulnerability
If you discover a security vulnerability, path traversal issue, or accidental data exposure within this project, please report it privately rather than opening a public issue:

- Open a **[Private Vulnerability Report](https://github.com/Alyciazsa/BG3-Mod-Sync-Utility/security/advisories/new)** via GitHub Security Advisories.
- Or contact **[@Alyciazsa](https://github.com/Alyciazsa)** directly via GitHub.
