# DHCP Integrations

IP Manager separates **local DHCP documentation** from **external DHCP snapshots**. You can always document a pool or reservation without touching a router or server. External integrations are read-only in this release except for a deliberate import into the local documentation store.

## What is supported

| Provider | Direction | Input | Writes to the DHCP server? |
|---|---|---|---|
| Local documentation | Read/write | IP Manager SQLite database | No server is involved |
| dnsmasq files | Read-only | lease file, optional `dnsmasq.conf` | No |
| ISC DHCP | Read-only | `dhcpd.leases` | No |
| Kea DHCP | Read-only | memfile lease CSV | No |
| Windows Server DHCP | Read-only | PowerShell `DhcpServer` module / RSAT | No |

There is no UniFi, pfSense, OPNsense, MikroTik, RouterOS or vendor-cloud integration in this release, even if a provider name appears in an older enum. The pages and this document list what actually ships.

Credentials for Windows DHCP are not stored by IP Manager. The PowerShell call runs in the current user's context and uses Windows authentication. File providers use the Windows account's file-share permissions.

## Local documentation

The **DHCP** page's local section stores:

- scope name, server label, start/end address, subnet mask, gateway and DNS values where applicable,
- reservations with IP, MAC and hostname,
- provider label and notes.

Local scopes are also visible to the address map and conflict checks. A local reservation does not program a router. To make a reservation effective, create it on the actual DHCP server and document it here (or import it).

### Importing a snapshot

When you read an external provider, the page shows the scopes, leases and reservations. **Import** (or the equivalent save action) copies the snapshot into the local documentation. The import:

- adds or updates documented scopes and reservations,
- does not delete local records that are absent from the snapshot,
- records an audit entry,
- can create database-duplicate warnings if the same address is already documented elsewhere.

Review the result before relying on it. A lease is not a reservation, and a reservation on the server is not proof that the device is currently online.

## dnsmasq

### Prepare

The default lease file on many distributions is:

```
/var/lib/misc/dnsmasq.leases
```

The configuration is often `/etc/dnsmasq.conf` or a file under `/etc/dnsmasq.d/`. Windows can read a local copy or a UNC share if the account has access.

### Configure

1. Open **DHCP → provider → dnsmasq**.
2. Set **Endpoint** to the lease file, for example `\\server\share\dnsmasq.leases` or `C:\Temp\dnsmasq.leases`.
3. Optionally set the `configPath` option to a `dnsmasq.conf` or snippet path. The page accepts an options field for this value.
4. Read the provider.

### Parsing behaviour

Lease lines use the classic format:

```
<expiry-epoch> <mac> <ip> <hostname> <client-id>
```

- `expiry` equal to `0` is treated as no expiry.
- `*` as the hostname is treated as empty.
- A MAC must normalize to six octets and the IP must be IPv4.

`dnsmasq.conf` parsing reads:

- `dhcp-host=<mac>,<ip>[,<hostname>]` as reservations,
- `dhcp-range=<start>,<end>[,<mask>,<lease-time>]` as pools.

If no range is declared, the range is inferred from the lowest and highest lease addresses, and the scope name says so. That inferred range is a convenience, not a configured pool.

### Limitations

- Read-only. Edit `dnsmasq.conf` on the server and reload dnsmasq.
- Include files are not followed recursively; point `configPath` at the file that actually contains the directives.
- Lease files can be mid-write. Read a copy or retry during a quiet moment.
- dnsmasq host declarations can use tags and multiple addresses; only simple `mac,ip[,hostname]` entries are recognized.

## ISC DHCP

### Prepare

The lease file is commonly:

```
/var/lib/dhcp/dhcpd.leases
```

Copy it locally or expose it through a share. The parser expects normal `lease <ip> { ... }` blocks with a `hardware ethernet <mac>;` statement and optional `client-hostname` and `ends` values.

### Configure

1. Select **ISC DHCP**.
2. Set **Endpoint** to the lease file path.
3. Read the provider.

### Behaviour and limitations

- Active and historical lease blocks are read. The page does not filter on expiry; check the lease's end time in the result.
- `ends never;` is treated as no expiry.
- Reservations are **not** parsed from `dhcpd.conf` in this release. Document them manually or export them from the server configuration.
- Read-only. Use the ISC tooling to change leases or reservations.
- The entire file is read into memory. Very large histories can be slow; archive old leases on the server.

## Kea DHCP

### Prepare

Kea's memfile backend writes a CSV such as:

```
kea-leases4.csv
```

The parser reads the header and expects at least `address` and `hwaddr`; `hostname`, `expire` and `valid_lifetime` are used when present. Both quoted and unquoted cells are accepted. A relative `valid_lifetime` is converted to an expiry from the current time.

### Configure

1. Select **Kea DHCP**.
2. Set **Endpoint** to the CSV path.
3. Read the provider.

### Limitations

- Read-only.
- One CSV is read at a time. Kea may rotate or split lease files; point at the active file or merge copies yourself.
- Lease state columns are not interpreted beyond the documented fields; expired rows may still appear.
- Reservations in Kea's JSON configuration are not imported in this release.

## Windows Server DHCP

### Requirements

- Windows Server with the DHCP Server role.
- The `DhcpServer` PowerShell module on the computer running IP Manager (usually installed with RSAT: **Role Administration Tools → DHCP Server Tools**).
- Permission to read scopes, reservations and leases on the target server.
- Network access to the server; WinRM/PowerShell remoting must be allowed if the server is remote.
- The server name (or `localhost`) entered as the **Endpoint**.

### Configure

1. Select **Windows Server DHCP**.
2. Enter the server name, for example `dhcp01` or `192.168.1.10`. Use `localhost` when running against the local DHCP role.
3. Read the provider.

The provider runs:

```powershell
Get-DhcpServerv4Scope -ComputerName '<server>'
Get-DhcpServerv4Reservation -ComputerName '<server>' -ScopeId <scope>
Get-DhcpServerv4Lease -ComputerName '<server>' -ScopeId <scope>
```

and converts the JSON result into scopes, reservations and leases. The computer name is single-quote escaped before being embedded in the script.

### Limitations

- Read-only. Create and change reservations in the Windows DHCP console.
- IPv4 scopes only (`DhcpServerv4*`). DHCPv6 is not imported.
- The current Windows user must have the required DHCP permissions. IP Manager does not store alternate credentials.
- If the module is missing, the page reports the PowerShell error. Install RSAT DHCP tools and reopen the page.
- The query can be slow on servers with many scopes or leases. There is a cancellation path, but PowerShell startup itself may take a few seconds.
- A firewall or WinRM policy can block the remote call even when the DHCP console works from another machine.

## Safety rules

- External DHCP providers are **read-only**. No reservations, leases or scope settings are written to a server.
- The local documentation store is the only place IP Manager writes DHCP-shaped data, and only when you save a record or import a snapshot.
- Import is **additive/update**, never a silent destructive sync.
- Importing a lease does not mark a device online. Online/offline status comes from monitoring.
- If a lease and a reservation disagree, review both; a stale lease is common after a reservation change.
- Audit entries record imports and local edits. They are visible in the audit log and retention settings.

## Troubleshooting

| Symptom | Check |
|---|---|
| `Lease file not found` | Path, share permissions, and whether the file is on the IP Manager computer. |
| dnsmasq returns leases but no pools | `dhcp-range` is not in the configured `configPath`; the scope may be inferred from leases. |
| ISC returns zero leases | The file may be empty, rotated, or use a format the parser does not recognize. Inspect the first lease block. |
| Kea returns zero leases | Wrong file, missing `address`/`hwaddr` header, or a non-memfile backend. |
| Windows DHCP PowerShell failed | Install the `DhcpServer` module, test `Get-DhcpServerv4Scope` manually, and verify WinRM and permissions. |
| Duplicate reservations after import | Both the server snapshot and the local record exist. Keep one source of truth and delete or merge the duplicate. |
| Imported lease has no hostname | DHCP did not supply one. Discovery or DNS may enrich it separately. |

See [Database](DATABASE.md) for tables and backup guidance, and [Troubleshooting](TROUBLESHOOTING.md) for general diagnostics.
