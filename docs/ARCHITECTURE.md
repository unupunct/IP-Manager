# Architecture

IP Manager is a single-process Windows desktop application built on .NET 10, WPF, EF Core/SQLite and the generic host. It is intentionally boring at runtime: no Windows service, no local web server, no plugin downloader and no telemetry endpoint.

## Solution layout

```
IPManager.slnx
src/
  IPManager.Core/            domain types, IPv4 math, OUI/classification, conflict analyzer
  IPManager.Infrastructure/  SQLite/EF Core, paths, settings, backups, DPAPI, audit
  IPManager.Network/         scanners, ARP, ICMP/TCP, DNS wire client, mDNS, NetBIOS
  IPManager.Services/        orchestration, inventory, monitoring, providers, reports
  IPManager.App/             WPF shell, views, view models, themes, DI/composition root
tests/
  IPManager.Tests/           xUnit unit and integration tests with isolated temp databases
installer/
  Package.wxs                per-user MSI
  Bundle.wxs                 WiX Burn bootstrapper EXE
docs/                        user, operator and maintainer documentation
tools/                       build, capture and release scripts
```

### Dependency direction

```
App ──────────────► Services ─────────► Network
 │                     │                  │
 │                     └──────────────► Core
 └──────────────────► Infrastructure ──► Core
```

- **Core** has no UI, database or network dependency. It owns `Ipv4Address`, `Ipv4Network`, subnet math, entities, enums, the conflict analyzer and OUI/device classification.
- **Infrastructure** references Core and provides persistence and Windows-specific storage/security primitives.
- **Network** references Core and implements probes and protocol parsers. It does not know about pages or the database.
- **Services** references Core, Infrastructure and Network. It coordinates scans, inventory writes, conflicts, monitoring, reports and integrations.
- **App** is the composition root and the only WPF project. It references Services and Infrastructure.

There is no circular project reference. The WPF layer does not contain SQL or packet parsing, and the service layer does not reference WPF.

## Startup sequence

`App.OnStartup`:

1. Registers global exception handlers (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`).
2. Loads `AppPaths` (normal vs portable).
3. Creates the Serilog logger at `logs/ipmanager-YYYYMMDD.log`, 14-day rolling retention.
4. Builds the generic `Host` and registers infrastructure, services, UI services, all page view models, `MainWindow` and the shell.
5. Starts the host.
6. Runs `DatabaseInitializer.InitializeAsync` (EF migrations, WAL, foreign keys, synchronous mode, OUI seed, audit).
7. Loads settings and applies the saved theme.
8. Initializes the OUI database.
9. Starts an optional startup backup in the background (failure is logged, never fatal).
10. Shows `MainWindow`, then initializes the shell and navigates to the last page.

`OnExit` stops the host with a 4-second budget, disposes it, flushes Serilog and exits. A second launch is not blocked; the app does not install a single-instance mutex. Running two copies against one SQLite database is not recommended for writing.

## UI composition (MVVM)

- **Shell:** `MainWindow` + `ShellViewModel`. The shell owns navigation, global search, theme toggle, alert count and the status bar.
- **Pages:** one view model and one view per page. All page view models are registered as singletons in DI.
- **Data templates:** `App.xaml` maps each view model type to its view. `MainWindow` hosts the selected page in a `ContentControl`. Pages are not recreated on navigation; their state survives.
- **Commands:** CommunityToolkit.Mvvm `[RelayCommand]` and `[ObservableProperty]` generate `ICommand` and `INotifyPropertyChanged` members.
- **Lifecycle:** `IActivatable` provides `ActivateAsync`/`DeactivateAsync`; `INavigationAware` receives a navigation argument. The shell deactivates the previous page before activating the next and queues a pending navigation if a page is still busy.
- **Services injected into view models:** `IUiService` for dialogs/status/dispatcher, `IUiNavigator` for page requests, `ISettingsService`, `IThemeService`, and the domain services. View models do not construct `DbContext`s or `HttpClient`s directly.
- **Theme:** resource dictionaries for dark/light; `ThemeService` swaps the dictionary and applies the DWM immersive dark title bar. Custom `ScrollBar` and `ScrollViewer` templates keep the chrome consistent.
- **Design consistency:** `TextPrimaryBrush` is set at the window root and section headers bind to it explicitly. This avoids the default black `TextBlock` foreground leaking into the dark theme.

### Navigation keys

`Dashboard`, `LocalAddresses`, `Discovery`, `Devices`, `IpManager`, `SubnetCalculator`, `SubnetManager`, `Dhcp`, `Dns`, `Conflicts`, `NetworkMap`, `Monitoring`, `Diagnostics`, `Alerts`, `Reports`, `Settings`.

The shell persists the last key in settings and restores it on the next start. `Local Addresses` is a first-class page, not a tab inside the dashboard.

## Persistence

- `IDbContextFactory<IpManagerDbContext>` is used for short-lived contexts. There is no long-lived shared `DbContext` in the UI.
- The connection string is `Data Source=<data>/ipmanager.db;Cache=Shared`, with a 30-second command timeout.
- Startup applies EF migrations and WAL/foreign-key/synchronous pragmas.
- Entities live in Core; mapping and indexes live in `IpManagerDbContext`.
- Services write through the factory and use `AsNoTracking` for read projections where mutation is not needed.
- Backups use SQLite's online backup API after a WAL checkpoint. Restore validates the SQLite header and creates a `pre-restore` safety copy.
- Settings are key/value rows (`Settings` table) plus `config.json` for the data-folder override.

See [Database](DATABASE.md) for the complete table and retention details.

## Discovery and inventory flow

```
DiscoveryViewModel
  └─ ScannerService.ScanAsync(options, targets)
        ├─ target parser + scope/size validation
        ├─ ARP/neighbour table seed
        ├─ mDNS listener
        ├─ bounded ICMP/TCP probes
        ├─ reverse DNS / NetBIOS / mDNS enrichment
        └─ second ARP read
  └─ InventoryService.ImportScanAsync(results)
        ├─ upsert Devices
        ├─ upsert IpAddressEntry rows
        ├─ upsert MacObservations (unique IP+MAC)
        ├─ write DeviceHistory for changed values
        ├─ create ScanSession summary
        └─ update alerts / conflict inputs
```

The scanner is deliberately stateless with respect to the database. It returns evidence, and the inventory service decides what to persist. Manual device and address fields are preserved; discovery fills empty fields and updates observations rather than overwriting user edits.

### Conflict analysis

`ConflictAnalyzer` in Core is a pure function over observations and database entries:

- live findings require multiple fresh MACs, independent sources and a minimum observation count,
- stale ARP entries produce only suspected findings,
- duplicate database assignments are a separate method and never labelled as live conflicts.

`ConflictService` orchestrates persistence, resolution notes and UI-friendly projections. Fresh/stale windows are configurable settings.

### Monitoring

`MonitoringBackgroundService` is an `IHostedService` inside the desktop process. It:

- waits for the configured interval,
- selects enabled targets that are due,
- executes ICMP/TCP/HTTP/DNS checks with timeouts and cancellation,
- writes `MonitoringResults`,
- updates `Device.Status`/`LastSeenOnline` using the offline threshold,
- creates and recovers alerts, respecting the cooldown key,
- exits when the host stops.

It is not a Windows service and does not run when the app is closed. Starting with Windows is an optional HKCU `Run` entry.

## Integrations

Both DHCP and DNS use provider interfaces (`IDhcpProvider`, `IDnsProvider`) registered as `IEnumerable<T>` in DI. The service layer selects a provider by kind, resolves a stored `IntegrationConfig`, decrypts its secret through `ISecretProtector`, and executes the operation.

- DHCP providers are read-only except the local documentation store. Imports are orchestrated by the service, not by provider-to-database code.
- DNS providers expose read and optional write methods. Only AdGuard Home and Cloudflare set `CanWrite`.
- Secrets are DPAPI-protected at current-user scope before storage; the UI never round-trips a clear-text secret.
- HTTP clients are named (`dns`, `notifications`, `downloads`) with explicit timeouts and a product user agent.
- PowerShell integrations are read-only and run in the current user context.

## Error handling and diagnostics

- Expected failures return result records (for example `DhcpProviderResult`, `DnsQueryResult`) and are rendered in the page.
- Unexpected exceptions are caught at the page boundary and shown with `IUiService.Error`, while the detail goes to Serilog.
- Global handler exceptions never terminate silently; the user sees a message and the log has the stack.
- Logs are daily rolling files under `logs/`, retained for 14 days. Log level is configurable in Settings.
- Diagnostics provides ping, trace, TCP port, ARP and adapter views without writing to the inventory.
- Audit events record application actions, provider use and backups according to retention settings.

## Security boundaries

| Boundary | Control |
|---|---|
| Installation | Per-user MSI, no admin, no service, no machine-wide driver |
| Network scanning | Private ranges by default; explicit external confirmation; address cap |
| Remote changes | Write-capable providers only; confirmation setting; no silent sync |
| Credentials | DPAPI current-user scope; never plain text in the database |
| Data at rest | Windows profile ACL; optional BitLocker; no application-level encryption |
| Telemetry | None |
| Code execution | No plugin loading, no downloaded executables, no auto-update |
| Update/OUI download | Explicit data-file download only |

## Build and release architecture

- `dotnet publish` produces a self-contained `win-x64` folder (`PublishSingleFile=false`, framework included). This avoids a prerequisite runtime and keeps native SQLite assets straightforward.
- WiX v5 builds a per-user MSI with a fixed upgrade code and component GUIDs.
- WiX Burn wraps the MSI in `IPManager-Setup-x64.exe` with `WixStandardBootstrapperApplication`.
- The portable ZIP is a copy of the publish folder plus `portable.flag`; it is built separately so the installed MSI never includes the flag.
- Release assets are not committed to git; `release/` binaries are ignored. Checksums and release notes are generated by the build script.

See [Build and Release](BUILD_AND_RELEASE.md).

## Testing strategy

- `IPManager.Tests` uses xUnit.
- Database tests use an isolated temporary workspace and `EnsureCreated`; they do not touch the user's real database.
- Pure Core functions (IPv4 math, parsing, conflict rules), file parsers, DNS wire encoding/decoding and service projections are unit tested.
- Provider parsers are tested with fixture text, not live servers.
- UI behavior is verified manually with the shipped build and screenshots. There is no automated UI test harness.

## Extension points

To add a provider:

1. Implement `IDhcpProvider` or `IDnsProvider` in `IPManager.Services`.
2. Set `Kind`, `DisplayName`, `Requirements`, `CanRead` and `CanWrite` honestly.
3. Return result records rather than throwing for expected failures.
4. Register it in `AddIpManagerServices`.
5. Add parser/unit tests and document the credentials and limitations.
6. Do not add a write operation without a confirmation path, audit entry and tests.

To add a page:

1. Create the view model (singleton) and view.
2. Implement `IActivatable`/`INavigationAware` if the page loads data.
3. Register the view model and add a `DataTemplate` mapping.
4. Add a `NavigationItem` in `ShellViewModel` and a subtitle.
5. Add a documentation section and recapture screenshots.

## Known design trade-offs

- **SQLite instead of a service/database server:** zero administration and portable data, at the cost of single-process writing and no multi-user concurrency.
- **In-process monitoring:** no installed service and no always-on agent, at the cost of no monitoring while the app is closed.
- **Conservative discovery:** fewer false positives and safer defaults, at the cost of missing hosts behind ICMP filtering, VLAN boundaries or client isolation.
- **Read-only by default for infrastructure:** safer, but reservations and DNS changes still require the native console or a write-capable provider.
- **`/24` map only:** a deliberate, legible 256-square visualization; other prefixes remain supported in the tables and calculators.
- **Unsigned binaries:** no certificate cost, but SmartScreen friction; checksums and source builds are the integrity path.
