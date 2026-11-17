# Database

IP Manager stores its documentation and observations in a local SQLite database managed by EF Core 10. There is no database server, no cloud sync and no background service.

## Location

| Mode | Database |
|---|---|
| Normal install | `%LOCALAPPDATA%\IPManager\ipmanager.db` |
| Portable | `<application folder>\data\ipmanager.db` |
| Custom data folder | `<configured folder>\ipmanager.db` |

The custom folder is written to `%LOCALAPPDATA%\IPManager\config.json` (or the portable config next to the executable). `%LOCALAPPDATA%\IPManager` also contains:

```
backups\      ipmanager-YYYYMMDD-HHmmss[-label].db
logs\         rolling Serilog files
exports\      CSV, JSON and PDF reports, CSV exports
oui\          oui-database.csv
config.json   data-folder override, when used
```

Do not copy only `ipmanager.db` while the application is running. SQLite is in WAL mode, so recent transactions may be in `ipmanager.db-wal`. Use **Backup now** for a consistent copy.

## SQLite settings

On startup `DatabaseInitializer` runs the EF Core migrations and applies:

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;
PRAGMA synchronous = NORMAL;
```

- WAL allows readers while a writer is active and is checkpointed before backups.
- Foreign keys are enforced; cascade rules are configured in the EF model.
- `synchronous=NORMAL` is a deliberate performance/durability trade-off for a desktop app. A sudden power loss can lose the last transaction; backups are the recovery path.
- The connection string uses `Cache=Shared` and a 30-second command timeout.

## Tables

| Table | Entity | Purpose |
|---|---|---|
| `NetworkInterfaces` | `NetworkInterfaceRecord` | Last captured adapter snapshot, addresses, gateway, DNS, speed, selected adapter |
| `Subnets` | `Subnet` | Documented IPv4 networks, gateway, DNS, VLAN, DHCP/static ranges, exclusions, monitoring flag |
| `IpAddresses` | `IpAddressEntry` | Per-address allocation, status, device link, MAC, hostname, description and notes |
| `Devices` | `Device` | Durable device inventory, category, vendor, status, first/last seen, tags, notes, monitoring flags |
| `DeviceHistory` | `DeviceHistory` | Field-level change history for devices |
| `MacObservations` | `MacObservation` | IP/MAC pairs seen on the wire, with first/last seen, source and observation count |
| `IpConflicts` | `IpConflict` | Analyzed conflict records, evidence, state, confidence, resolution |
| `DhcpScopes` | `DhcpScope` | Local/DHCP scope documentation and imported snapshots |
| `DhcpReservations` | `DhcpReservation` | Reservation documentation linked to a scope |
| `DnsRecords` | `DnsRecordDoc` | Local DNS record documentation |
| `DnsQueryLogs` | `DnsQueryLog` | Recent DNS lookup history for the DNS page |
| `MonitoringTargets` | `MonitoringTarget` | What to check, method, port/URL, interval and timeout |
| `MonitoringResults` | `MonitoringResult` | Availability checks, success, response time and detail |
| `Alerts` | `Alert` | In-app alerts, severity, acknowledgement, recovery and cooldown key |
| `TopologyLayouts` | `TopologyLayout` | Saved network-map node positions as JSON |
| `Settings` | `AppSetting` | Persisted application settings as key/value pairs |
| `Integrations` | `IntegrationConfig` | Provider configuration, endpoint, username, DPAPI-protected secret, state |
| `AuditEvents` | `AuditEvent` | Application actions and their details (retention-controlled) |
| `ScanSessions` | `ScanSession` | Discovery run summary: targets, methods, counts, status, errors |
| `__EFMigrationsHistory` | EF Core | Applied migration identifiers; do not edit manually |

### Important indexes and relationships

- `Subnets(NetworkAddress, PrefixLength)` is unique.
- `Devices(MacAddress)`, `Devices(IpAddress)` and `Devices(Name)` are indexed.
- `IpAddresses(Address)` and `(SubnetId, Address)` are indexed.
- `MacObservations(IpAddress)` and `(IpAddress, MacAddress)` are indexed; the latter is unique.
- `IpConflicts(IpAddress)` and `IpConflicts(State)` are indexed.
- `MonitoringResults(DeviceId, Timestamp)` is indexed.
- `Alerts(CreatedAt)` and `Alerts(IsAcknowledged)` are indexed.
- `DhcpReservations(DhcpScopeId, IpAddress)` is indexed.
- `DnsQueryLogs(Timestamp)`, `AuditEvents(Timestamp)`, `ScanSessions(StartedAt)` are indexed.
- `DnsRecords(Zone, Name, Type)` and `DeviceHistory(DeviceId, ChangedAt)` are indexed.

Deleting a device cascades its history and monitoring targets/results. Deleting a subnet sets `SubnetId` to null on its addresses and devices rather than deleting them. Deleting a DHCP scope cascades its reservations.

### Key enums

| Enum | Values (as stored) |
|---|---|
| `IpStatus` | `Available`, `Assigned`, `Reserved`, `Gateway`, `Conflict`, `Excluded`, `Unknown` |
| `AllocationType` | `Static`, `Dynamic`, `Reserved`, `Unmanaged`, `Unknown` |
| `DeviceStatus` | `Online`, `Offline`, `Unknown` |
| `ConflictState` | `Suspected`, `Confirmed`, `Resolved`, `Ignored` |
| `ConflictMethod` | `MultipleMacObservations`, `DuplicateDatabaseAssignment`, plus other analyzer methods |
| `DhcpProviderKind` | `LocalDocumentation`, `DnsmasqFiles`, `IscDhcpFiles`, `KeaDhcp`, `WindowsServerDhcp` |
| `DnsProviderKind` | `PiHole`, `AdGuardHome`, `Cloudflare`, `WindowsDnsServer` |
| `MonitoringMethod` | `Icmp`, `Tcp`, `Http`, `Dns`, etc. |
| `AlertSeverity` | `Info`, `Warning`, `Critical` |

Enums are persisted as integers by EF Core. Do not reorder enum members in a future release without a migration.

## Migrations and upgrades

- The app uses EF Core migrations, not `EnsureCreated`, at runtime.
- On startup, `DatabaseInitializer.InitializeAsync` calls `MigrateAsync`, applies pending migrations, then sets WAL and foreign keys.
- The schema version is tracked in `__EFMigrationsHistory`.
- A newer app can migrate an older database. A downgrade is not supported: restore the backup taken before the upgrade.
- Tests use `EnsureCreated` with an isolated temporary database; that path is not used by the installed app.
- Startup backups are enabled by default. Keep them enabled when moving between versions.

## Backups

### Create

Use **Settings → Storage & maintenance → Backup now**, or the backup action on the relevant page. The backup service:

1. runs `PRAGMA wal_checkpoint(FULL)`,
2. opens the source read-only and a new destination file,
3. uses SQLite's online backup API (`BackupDatabase`),
4. prunes old files,
5. returns the path and size.

Backups are named `ipmanager-YYYYMMDD-HHmmss[-label].db` and are complete SQLite databases.

Startup backups use the configured retention count. The service also prunes to at most 20 files during a backup creation as a safety ceiling.

### Restore

The restore path:

1. verifies the SQLite header (`SQLite format 3\0`),
2. creates a `pre-restore` safety backup,
3. clears SQLite connection pools,
4. copies the selected backup over `ipmanager.db`.

Restart the application after a restore so every page reloads from the restored data. If the app refuses a file, it is not a valid SQLite database or is locked by another process.

### Export

**Export database package** creates a ZIP containing `ipmanager.db` and `config.json` (if present). It is a portable archive for support or migration, not a substitute for a normal backup. The backup inside the ZIP is consistent at the time of export.

### Manual copy

If you copy files manually:

1. close IP Manager,
2. copy `ipmanager.db`, `ipmanager.db-wal` and `ipmanager.db-shm` together (or open and close the app once to checkpoint),
3. store the set together.

A copy of only the `.db` file can miss the latest transactions. The application's backup action avoids this problem.

## Retention and growth

Retention is configured in **Settings → Storage & maintenance**:

- backup count,
- monitoring result days,
- alert days,
- audit days.

Discovery adds one `MacObservation` per IP/MAC pair and updates counts rather than inserting unbounded duplicates. Monitoring results are the main growth source. A 60-second interval on 50 devices produces 72,000 rows per day before retention; set the interval and retention deliberately.

Approximate sizing for a typical `/24`:

| Data | Typical size |
|---|---|
| Subnets, addresses, devices, DHCP/DNS docs | well under 1 MB |
| 30 days of monitoring for 50 devices at 60 s | tens of MB |
| Audit and alerts | small unless verbose |

SQLite handles this comfortably. If the database grows unexpectedly, check the monitoring interval and retention first.

## Integrity and recovery

Run a read-only integrity check while the app is closed:

```powershell
sqlite3 "$env:LOCALAPPDATA\IPManager\ipmanager.db" "PRAGMA integrity_check;"
```

If `sqlite3` is not installed, use **DB Browser for SQLite** and its integrity check, or restore a backup.

Corruption is rare but can follow a power loss or a full disk. Recovery order:

1. stop the application,
2. copy the current database aside (do not delete it),
3. restore the newest good backup,
4. restart and verify,
5. if the backup is older than you can accept, open a copy of the damaged file with a SQLite recovery tool.

A valid backup has a `SQLite format 3` header; the restore path checks this before overwriting.

## Direct access and safety

The database is a normal SQLite file, so third-party tools can read it. Prefer read-only access while IP Manager is running:

- Do not edit rows while the app is open; the app caches pages and may overwrite your change.
- Do not change the schema or `__EFMigrationsHistory`; the next startup will fail or misbehave.
- Do not reorder enum values.
- Do not copy a database between Windows users and expect DPAPI-protected integration secrets to work; re-enter them.
- The database is not encrypted at rest. It relies on the Windows user profile ACL. Use BitLocker/device encryption if the host is portable.
- If you need to script exports, use the app's CSV/JSON reports rather than writing to the database.

### Useful read-only queries

```sql
-- Device inventory
SELECT Name, IpAddress, MacAddress, Status, LastSeen FROM Devices ORDER BY Name;

-- Address allocation for one subnet
SELECT Address, Status, AllocationType, Hostname
FROM IpAddresses
WHERE SubnetId = 1
ORDER BY Address;

-- Recent conflicts
SELECT IpAddress, State, Confidence, DetectedAt
FROM IpConflicts
ORDER BY DetectedAt DESC
LIMIT 20;

-- Monitoring volume
SELECT COUNT(*) AS Results,
       MIN(Timestamp) AS Oldest,
       MAX(Timestamp) AS Newest
FROM MonitoringResults;
```

## Privacy

The database contains device names, MAC addresses, IP addresses, notes, locations and possibly integration endpoints. Treat it as internal network documentation. There is no telemetry upload. If you share a database export for support, review the devices, notes and integration rows first; secrets are DPAPI-encrypted and generally not portable to another user, but the rest of the inventory is readable.
