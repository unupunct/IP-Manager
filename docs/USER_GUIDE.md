# User Guide

This guide describes the shipped application. Labels match the UI; screenshots are in [`../screenshots/pages`](../screenshots/pages).

## Starting the application

Launch **IP Manager** from the Start Menu, desktop shortcut, or `IPManager.exe` in portable mode. The first run creates:

- the SQLite database and schema,
- the OUI vendor seed file,
- a startup backup (when enabled),
- `config.json` settings.

The last page you used is reopened on the next start. Closing the window shuts down monitoring and discovery.

## The shell

| Area | What it does |
|---|---|
| Left navigation | 16 pages. The selected page is saved as the last page. |
| Global search | Enter a term and press Enter (or the search button) to open **Devices** filtered by name, IP, MAC, hostname, vendor, tag or location. |
| Theme toggle | Switches between the dark and light themes and persists the choice. |
| Alerts button | Shows the unread alert count and opens **Alerts**. |
| Status bar | Shows the last operation result and time. Errors appear in dialogs and are also written to the log. |

The app is keyboard-friendly through normal Windows tab navigation. It does not define custom global hotkeys.

## Dashboard

The Dashboard is a live summary, not a separate data source.

- **Managed subnets, devices, online now, active conflicts, available addresses** are computed from the database and current device status.
- **Address allocation** breaks the documented address space into assigned, reserved, gateway, conflict, excluded and available counts.
- **Device status** shows online, offline and unknown devices.
- **Subnet utilization** lists per-subnet usage.
- **Recently seen devices** shows the latest observations.
- Buttons open **Discovery**, **Conflicts**, **Alerts** and **Devices**.

Use **Refresh** after changes made outside the page.

## Local Addresses

This page is a fixed 256-square map for one `/24` subnet.

1. Choose a subnet from the selector (the app prefers an existing `/24`).
2. Read the map row by row: `00`–`0F`, `10`–`1F`, ... `F0`–`FF`. The last octet is the square index.
3. Click a square. The **Selected address** card shows the address, status, device, hostname, MAC, allocation type, source and last-seen information.
4. Use **Refresh** to rebuild from the current database and discovery results.

### Map colours

| Colour | Meaning |
|---|---|
| Green | Assigned/online device |
| Dark gray | Assigned device currently marked offline by monitoring |
| Blue | Gateway |
| Violet | Reserved |
| Red | Address with a conflict record |
| Slate | Excluded |
| Panel colour | Available |
| Dark boundary | Network (`.0`) and broadcast (`.255`) addresses |

The offline colour is driven by `DeviceStatus.Offline`, which monitoring sets after the configured threshold. A single failed scan does not mark a device offline.

### Map actions

| Button | Result |
|---|---|
| **Scan network** | Opens **Discovery** with this subnet's CIDR and starts a scan automatically. |
| **View active devices** | Opens **Devices**. |
| **Check IP conflicts** | Opens **Conflicts**. |
| **Export results** | Saves all 256 rows as CSV with address, status, boundary flag, device, hostname, MAC and allocation. |

A subnet that is not `/24` shows an explicit notice instead of a misleading map. Use **Subnet Manager** to add a `/24` if needed.

## Discovery

Discovery sends only the probes you enable.

1. Select an adapter. **Use this adapter** fills the target list with its suggested CIDR.
2. Edit **Targets** if needed. Accepted forms: one IP, a range such as `192.168.1.10-192.168.1.40`, a CIDR such as `192.168.1.0/24`, or a comma/newline-separated list.
3. Choose methods:
   - **ARP** — local neighbour/ARP table evidence,
   - **ICMP** — echo requests,
   - **TCP** — probes the configured ports (default `80,443,22,445,3389,8080,9100,631,554,5000`),
   - **DNS / reverse DNS**, **mDNS**, **NetBIOS** — name enrichment.
4. Start discovery. Progress, checked/found counts and the current target are shown.
5. Review results: IP, MAC, name, vendor, evidence, open ports and response.
6. Select a host to see its evidence and ports. **Copy results** copies the current table.
7. **Cancel scan** stops the run.

The **external ranges** switch is off by default. Turning it on requires a confirmation dialog because scanning public or third-party addresses can be intrusive.

See [Network Discovery](NETWORK_DISCOVERY.md) for the full method and safety model.

## Devices

**Devices** is the durable inventory.

- Search by name, IP, MAC, host, vendor, tag or location.
- **New device** creates a manually managed record. Manual records survive discovery updates.
- **Refresh** reloads from the database.
- Select a row and edit name, category, MAC, hostname, vendor, operating system, notes, tags, location, trusted/important flags and monitoring exclusion.
- Device history is shown in the editor where available.
- Opening a device from another page (for example **Open device** on the Network Map) navigates here with a filter.

Discovered values are filled only when the field is empty; manual edits are not overwritten by later discovery. Use the IP Manager page for per-address reservations and assignments.

## IP Manager

**IP Manager** is the address-by-address documentation table for the selected subnet.

- Filter by address, device, MAC, hostname or description.
- **Find free** locates the first available address.
- **Reserve / assign** edits the selected address record, allocation type, status, description and notes.
- **Refresh** rebuilds the address map from the database and discovery data.
- The summary shows total, assigned, reserved, available, conflict and excluded counts plus utilization.

Large subnets are limited to the first 4096 rows for display; the page says so explicitly. The underlying data is not truncated.

## Subnet Calculator

- Enter an IPv4 address or CIDR, choose a prefix and press **Calculate**.
- Review network, mask, broadcast, usable range, host count and utilization.
- **Copy summary** copies the calculated details.
- **Divide** splits the network into equal subnets and shows CIDR, mask, range and usable count.
- The IPv6 section validates an address and divides it into a new prefix, with a message describing the result.

## Subnet Manager

Document subnets and their addressing plan.

- **New subnet** or **Add from adapter**.
- Fields: name, network address, prefix, gateway, DNS servers, VLAN, location, description, DHCP pool start/end, static range start/end, excluded addresses and monitoring toggle.
- The table shows CIDR, gateway, used count, utilization and DHCP range, plus warnings for inconsistent ranges.
- Editing a subnet updates the address map and the Local Addresses page.

## DHCP

The DHCP page has two parts:

1. **Local documentation** — scopes and reservations stored in the IP Manager database. You can create, edit and delete them without touching a router or server.
2. **Provider import** — read a supported external source and optionally copy its snapshot into local documentation.

Supported providers:

| Provider | Access | Notes |
|---|---|---|
| Local documentation | Read/write | The only writable provider |
| dnsmasq files | Read-only | Lease file and optional `dnsmasq.conf` |
| ISC DHCP | Read-only | `dhcpd.leases`; reservations are not parsed from `dhcpd.conf` |
| Kea DHCP | Read-only | memfile lease CSV |
| Windows Server DHCP | Read-only | PowerShell `DhcpServer` module/RSAT required |

The page also shows **local DHCP client information** from `Win32_NetworkAdapterConfiguration` (adapter, MAC, address, DHCP server, lease times) when available.

Importing a snapshot is additive and audited. See [DHCP Integrations](DHCP_INTEGRATIONS.md).

## DNS

The DNS page provides:

- **Lookup** for A, AAAA, CNAME, PTR, MX, TXT, NS and SRV records.
- **Reverse** lookup for an address.
- A resolver that speaks DNS over UDP with TCP fallback.
- **Compare** across multiple servers (system default and explicit servers).
- **Recent queries** with server, success, duration and time.
- **Local records** documented in the database (zone, name, type, value, TTL, notes).
- **Integrations** for Pi-hole (read-only), AdGuard Home (rewrite API, read/write), Cloudflare (API token, read/write) and Windows DNS Server (PowerShell, read-only).

No query is sent to a public resolver unless you choose one or use the system default. See [DNS Integrations](DNS_INTEGRATIONS.md).

## Conflicts

**Run analysis** evaluates two separate classes of evidence:

1. **Live network observations** — the same IP seen with multiple MAC addresses. A conflict is **confirmed** only when at least two MACs are fresh and the evidence comes from at least two independent methods/observations. Stale ARP entries can produce a **suspected** finding but never a confirmed one.
2. **Documentation duplicates** — the same address assigned to more than one record, or duplicate DHCP reservations. These are labelled as database duplicates, not live conflicts.

The table shows address, state, confidence, method, MACs and last-seen time. Select a row to read the full evidence. **Resolve** marks a conflict resolved with a note; a later analysis can reopen it.

Freshness and stale windows are configured in **Settings → Conflicts & alerts**.

## Network Map

The map is an evidence-aware topology, not a physical cable diagram.

1. **Build map** reads documented subnets, gateways, devices and the active adapter.
2. Edges are labelled:
   - **Verified** — the device itself answers on the gateway address (IP + MAC evidence).
   - **Inferred** — same configured subnet/default route; the physical uplink is unknown.
   - **Unknown** — a device without a mapped subnet relationship.
3. **Fit**, **Auto layout** and **Save layout** control the canvas. Layouts are stored in the database.
4. Select a node to see its details; **Open device** navigates to Devices.

IP Manager does not have LLDP/SNMP topology data and does not claim switch-port or uplink verification it cannot observe.

## Monitoring

Monitoring runs in-process at the configured interval.

- Enable or disable monitoring per subnet/device via the monitoring settings and device flags.
- **Run cycle** performs an immediate check.
- The page shows summary counts, uptime percentage, checks, successes, failures, average response and observation buckets.
- **New target** adds a monitoring target for a device (ICMP, TCP, HTTP or DNS depending on the target).
- **Observation history** lists timestamp, result, method, response and detail.
- Offline status is set only after the last success is older than **Offline threshold** (default 5 minutes). A recovery can raise an alert when enabled.

Monitoring results are retained according to **Monitoring retention days** (default 30).

## Diagnostics

Quick network checks against a host or IP:

- **Ping** with count, interval and timeout.
- **Trace** (traceroute) with hop, address and timings.
- **Port** probe for a TCP port.
- **ARP** cache display.
- **Adapters** reload.
- **Stop** cancels the active operation.
- Shortcuts: **Use gateway**, **Use this PC**, **Use DNS server**.

Results are displayed in the page; they are not written into the inventory automatically.

## Alerts

Alerts are recorded in-app even when outbound notifications are disabled.

- Filter by title, message, address or type.
- **Mark all read** and **Acknowledge all** update the badge and rows.
- **Purge old** applies the alert retention setting.
- Select an alert to see severity, type, address, time and detail.
- Alert cooldown prevents duplicate alerts for the same condition within the configured window.

Optional outbound channels are configured in **Settings → Notifications**:
- **Telegram** — bot token (DPAPI-protected) and chat ID.
- **Email** — SMTP host, port, from/to, username and password (DPAPI-protected), TLS.

## Reports

Choose a report and a date range (where applicable), then **Build**:

| Report | Contents |
|---|---|
| Network inventory | Devices, addresses, categories, status, last seen |
| Subnet utilization | Per-subnet totals and percentages |
| DHCP | Scopes, reservations and lease documentation |
| IP conflicts | Conflict records and evidence |
| DNS | System DNS servers and documented records |
| Availability | Monitoring results and uptime over the selected range |

Export the current report as **CSV**, **JSON** or **PDF**. **Export all (CSV)** writes every report. **Open folder** opens the exports directory. Reports are written as UTF-8 CSV with a BOM, JSON with metadata, or A4-landscape PDF.

## Settings

| Tab | Settings |
|---|---|
| General | Theme, 24-hour clock, UI refresh interval, data location, version, storage summary |
| Discovery | Default range, preferred adapter, timeout, concurrency, retries, probe delay, max addresses, methods, TCP ports, external confirmation, auto-scan on startup |
| Monitoring | Availability monitoring, interval, offline threshold, timeout, gateway monitoring, persistence |
| Conflicts & alerts | Fresh/stale windows, utilization/DHCP thresholds, alert types, cooldown, recovery notifications |
| Notifications | In-app alert information, Telegram, SMTP email |
| Storage & maintenance | Backup on startup, backup retention, monitoring/alert/audit retention, start with Windows, confirm remote changes, log level, OUI update |

**Save settings** persists changes. A few settings (data folder) require a restart.

### Data and backups

- **Open data folder** opens `%LOCALAPPDATA%\IPManager` or the portable `data` folder.
- **Change data folder** writes a `config.json` override and requires a restart.
- **Backup now** (where shown) creates a SQLite backup; startup backups are enabled by default with a retention count.
- Restoring a backup is available in **Storage & maintenance**; the app creates a safety backup before overwriting the current database.

## Practical tips

- Start with one subnet and a `/24` scan. Expand documentation only after you know what is real.
- Prefer a static reservation documented in both IP Manager and the DHCP source; imports make the two visible.
- Treat a conflict as a lead, not a verdict. Read the evidence and freshness.
- Use **Excluded** for addresses you never want to allocate (printers, appliances, reserved pools).
- Export the map or reports before major changes, and keep a backup.
- If a scan finds fewer devices than expected, check host firewalls and whether the devices are on the same broadcast domain before changing timeouts.
