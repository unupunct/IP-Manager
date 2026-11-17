# IP Manager 1.0.0

**Initial public release.** A local-first Windows LAN IP address manager that documents the subnet, discovers what is really there, and keeps the two honest.

## Downloads

| Asset | Use |
|---|---|
| `IPManager-Setup-x64.exe` | Recommended per-user installer. No administrator prompt, Start Menu and desktop shortcuts. |
| `IPManager-Portable-x64.zip` | Extract and run. Data stays in the `data` folder. |
| `IPManager-Setup-x64.msi` | Advanced/managed deployment. The EXE bundle is the normal choice. |
| `checksums.txt` | SHA-256 for all release assets. Verify before running. |

## Highlights

- **16 functional pages:** Dashboard, Local Addresses, Discovery, Devices, IP Manager, Subnet Calculator, Subnet Manager, DHCP, DNS, Conflicts, Network Map, Monitoring, Diagnostics, Alerts, Reports, Settings.
- **Local Addresses `/24` map:** exactly 256 squares. Active devices green, offline dark gray, gateway blue, reserved violet, conflicts red, excluded slate. Click any square for details; export all 256 rows to CSV.
- **Conservative discovery:** ARP/neighbour table, ICMP, TCP probes, reverse DNS, mDNS and NetBIOS. Private ranges by default; explicit confirmation for public/third-party ranges; configurable timeouts, retries, concurrency and address cap.
- **Evidence-based conflicts:** confirmed live conflicts require multiple fresh MAC observations from independent methods. Stale ARP entries cannot confirm a conflict. Documentation duplicates are reported separately.
- **Documentation:** subnets, gateways, DHCP pools, static ranges, exclusions, reservations, VLANs, locations, devices and notes.
- **DHCP:** local documentation plus read-only imports from dnsmasq, ISC DHCP, Kea and Windows Server DHCP.
- **DNS:** built-in UDP/TCP-fallback resolver, system/server comparison, local record documentation, Pi-hole and Windows DNS reading, AdGuard Home and Cloudflare writes.
- **Monitoring and alerts:** in-process availability checks, offline thresholds, history, cooldown, Telegram and SMTP notification channels.
- **Reports:** inventory, utilization, DHCP, DNS, conflicts and availability as CSV, JSON or PDF.
- **Dark and light themes**, dark title bar, themed scrollbars, global search and per-page state.
- **No telemetry, no cloud account, no Windows service, no hidden agent.**

## Safety

- No public-range scanning without an explicit, confirmed opt-in.
- No silent router, DHCP or DNS changes. DHCP providers are read-only; only AdGuard Home and Cloudflare can write, and only after an explicit action.
- Credentials use Windows DPAPI at current-user scope.
- A single failed ping or scan never marks a device offline.
- Topology edges are `verified`, `inferred` or `unknown`; the app does not invent switch ports.
- Per-user install and normal user data paths; no administrator rights required.

## Requirements

- Windows 10 or Windows 11, x64.
- No .NET runtime install; the build is self-contained.
- About 150 MB installed.
- IPv4 LAN access for discovery.

## Install

1. Download `IPManager-Setup-x64.exe`.
2. Verify its SHA-256 against `checksums.txt`.
3. Run it. It installs to `%LOCALAPPDATA%\Programs\IP Manager` and creates Start Menu and desktop shortcuts.
4. Launch **IP Manager**.

Silent install/uninstall:

```powershell
.\IPManager-Setup-x64.exe /install /quiet
.\IPManager-Setup-x64.exe /uninstall /quiet
```

Data lives in `%LOCALAPPDATA%\IPManager`. Uninstalling preserves the data folder. Portable data lives in `<extracted folder>\data`.

## Unsigned build

No Authenticode certificate is available for this release, so the EXE and MSI are unsigned. Windows SmartScreen may show **Unknown publisher**. Verify the SHA-256 checksum before running. The app does not auto-update or download executable code at runtime. The optional OUI vendor database update is a data file and is explicitly triggered.

## Checksums

See the attached [`checksums.txt`](https://github.com/unupunct/IP-Manager/releases/latest/download/checksums.txt). Verify with:

```powershell
Get-FileHash .\IPManager-Setup-x64.exe -Algorithm SHA256
```

## Known limitations

- The Local Addresses map is intentionally `/24` only (256 squares). Other prefixes remain supported in the tables and calculators.
- Discovery is IPv4-first. IPv6 addresses and AAAA records can be documented/queried, but the map and scan pipeline are IPv4-focused.
- ARP visibility stops at the local broadcast domain; VLANs and routed subnets require separate documentation or a host in each segment.
- Windows Server DHCP and Windows DNS are read-only through PowerShell/RSAT; Pi-hole is read-only. ISC and Kea files are read-only.
- Offline is a monitoring state after the configured threshold, not an inference from a scan.
- The binaries are unsigned.

## Documentation

- [Installation](https://github.com/unupunct/IP-Manager/blob/main/docs/INSTALLATION.md)
- [User Guide](https://github.com/unupunct/IP-Manager/blob/main/docs/USER_GUIDE.md)
- [Network Discovery](https://github.com/unupunct/IP-Manager/blob/main/docs/NETWORK_DISCOVERY.md)
- [DHCP Integrations](https://github.com/unupunct/IP-Manager/blob/main/docs/DHCP_INTEGRATIONS.md)
- [DNS Integrations](https://github.com/unupunct/IP-Manager/blob/main/docs/DNS_INTEGRATIONS.md)
- [Database](https://github.com/unupunct/IP-Manager/blob/main/docs/DATABASE.md)
- [Architecture](https://github.com/unupunct/IP-Manager/blob/main/docs/ARCHITECTURE.md)
- [Troubleshooting](https://github.com/unupunct/IP-Manager/blob/main/docs/TROUBLESHOOTING.md)
- [Build and Release](https://github.com/unupunct/IP-Manager/blob/main/docs/BUILD_AND_RELEASE.md)

## License

MIT. See [LICENSE](https://github.com/unupunct/IP-Manager/blob/main/LICENSE).
