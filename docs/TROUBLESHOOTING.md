# Troubleshooting

Start with the log. Both the installed and portable builds write a daily rolling log:

```
%LOCALAPPDATA%\IPManager\logs\ipmanager-YYYYMMDD.log
<portable folder>\data\logs\ipmanager-YYYYMMDD.log
```

The status bar shows short messages. Unexpected exceptions also open a dialog and write the stack trace to the log. Logs are retained for 14 days.

## Startup

### The application does not start

1. Check the log for `Startup failed`, `Database ready`, `Migrating`, or a SQLite error.
2. Confirm the data folder is writable. A portable copy in `C:\Program Files` is not writable for a normal user; move it to `%LOCALAPPDATA%`, `D:\Tools` or another user-writable folder.
3. Confirm the process is not already running and holding the database. Close other copies.
4. If the database was copied from another machine, make sure `ipmanager.db`, `ipmanager.db-wal` and `ipmanager.db-shm` were copied together, or restore a backup instead.
5. Check free disk space and antivirus quarantine events.

A self-contained build does not require a separate .NET runtime. If Windows reports a missing DLL, the publish folder is incomplete; reinstall or re-extract the ZIP.

### Database migration fails

The app applies EF migrations at startup. A failed migration usually means:

- the file is not an IP Manager SQLite database,
- the schema was edited manually,
- `__EFMigrationsHistory` was changed,
- a newer database was opened by an older app (downgrade is unsupported),
- the disk is full or the file is locked.

Do not delete the database in place. Stop the app, copy `ipmanager.db` aside, and restore the newest backup from `backups\`. If the backup predates the upgrade, use the newest one and re-enter recent manual changes.

### `database is locked`

- Close every IP Manager instance.
- Close DB Browser/SQLite tools that may hold a write transaction.
- Check antivirus or backup software scanning the data folder.
- Wait a few seconds and retry.
- If the lock persists, stop the app, copy the database set aside, and restore a backup.

### DPAPI decrypt errors

Integration secrets are encrypted for the current Windows user. After moving the database to another user, a new Windows profile, or a new machine:

1. Open the integration.
2. Re-enter the password or API token.
3. Save and test.

A copied DPAPI blob cannot be decrypted by a different user, by design.

## Installer and updates

| Symptom | Action |
|---|---|
| SmartScreen: unknown publisher | Expected for unsigned builds. Verify SHA-256 against `checksums.txt`, then **More info → Run anyway** if it matches. |
| Exit code 1603 | Read the MSI log in `%TEMP%`; find `Return value 3` and the action just before it. Common causes: locked files, a damaged previous install, antivirus. |
| `requires 64-bit Windows` | The build is x64. Check `PROCESSOR_ARCHITECTURE`. |
| Installer runs but no shortcut | Check `%LOCALAPPDATA%\Programs\IP Manager` and the Start Menu folder. The per-user install does not use `Program Files`. |
| Uninstall leaves data | By design. `%LOCALAPPDATA%\IPManager` is preserved. Back it up before deleting. |
| Upgrade fails | Uninstall the old version (data is preserved), then install the new one. Restore a backup if the database does not open. |
| MSI log missing | Use `msiexec /i ... /qn /l*v "$env:TEMP\ipmanager.log"` and inspect the log. |

The bundle accepts `/install /quiet`, `/passive`, and `/uninstall /quiet`. The MSI is intentionally UI-less; use the EXE bundle for the normal setup experience.

## Discovery

### Scan finds fewer hosts than expected

- ICMP is commonly blocked by Windows Firewall and endpoint protection. Enable TCP and mDNS.
- Confirm the target is in the same broadcast domain. ARP does not cross routers or VLANs.
- Wi-Fi client isolation prevents peer-to-peer probes.
- A sleeping device may not answer; scan again or use monitoring.
- Check the **Evidence** column. An ARP-only row means the host may be stale.
- Check **Targets** and the address cap. A `/16` target list is capped by design.
- Some devices only answer on non-default ports. Add a port they expose to the TCP port list.

### The scan refuses the range

The range is public, link-local handling is unexpected, or it exceeds `MaxAddresses`. Public and third-party ranges require **Allow external ranges** and a confirmation. Do not enable it without authorization. Reduce a large private range to the subnet you actually manage.

### All results are ARP-only

The ARP/neighbour table still had recently disconnected devices, or firewalls dropped every probe. The app labels these rows as possibly stale. Use a longer ping timeout, enable TCP, and rescan after the device has been active. Do not treat an ARP-only row as proof of a live host.

### MAC addresses change for phones/laptops

Modern phones and some laptops use randomized Wi-Fi MAC addresses. The app shows the observed MAC and marks locally administered addresses. Treat identity as hostname + behavior + reservation, not MAC alone, when randomization is in use.

### Discovery is slow

Timeouts and retries multiply by the target count. A `/24` with 900 ms timeouts and one retry is usually around 30 seconds; a `/16` is far slower. Reduce the target range, retries or timeout, and keep concurrency sane for Wi-Fi.

## Conflicts

### A conflict looks wrong

Read the **Evidence** panel. Check:

- how many fresh MACs are present,
- the sources (ARP, TCP, ICMP, etc.),
- the last-seen timestamps,
- whether the finding is **Suspected** or **Confirmed**,
- whether it is a **database duplicate** rather than a live conflict.

A stale ARP entry, a router address move, or a VM MAC change can look suspicious. Wait for the stale window, rescan, and resolve with a note if it is a false positive. A confirmed finding requires fresh MACs from independent sources.

### Duplicate addresses in the database

Open the address rows and merge/delete the duplicate. A database duplicate is a documentation problem, not proof of two live hosts. The conflict page labels it separately for that reason.

## Monitoring

### Devices show offline incorrectly

- Check **Offline threshold** and the monitoring interval.
- A blocked ICMP check will fail even when the device is up. Use a TCP or HTTP target for hosts that block ping.
- Check that the target is enabled and the device is not excluded from monitoring.
- Verify the device's address has not changed.
- Read the observation detail; a timeout is different from a refused connection.

Offline is only set after the last success is older than the threshold, not after one failed scan.

### Monitoring does not run

- Monitoring runs only while the application is open.
- Check that availability monitoring is enabled and the interval is not enormous.
- Check that the subnet or device is enabled for monitoring.
- **Run cycle** performs an immediate check and is the quickest verification.

### Database grows quickly

Monitoring results are the main growth source. Increase the interval or reduce **Monitoring retention days**. Discovery observations are upserted by IP+MAC and do not grow per scan indefinitely. Alerts and audit events also have retention settings.

## Alerts and notifications

### In-app alerts are absent

- Check the alert type toggles and severity settings.
- Check the cooldown; a duplicate condition inside the window is suppressed.
- Look at the observation/result that should have triggered it.
- Run the relevant analysis or a monitoring cycle.

### Telegram does not send

- Verify the bot token and chat ID.
- The bot must be able to message the chat; start a conversation with the bot first.
- Check outbound HTTPS access and the log for the API error.
- Tokens are DPAPI-protected; re-enter after a profile change.

### Email does not send

- Verify host, port, TLS mode, username and password.
- Check whether the SMTP server allows the sender/recipient and the current network.
- Port 587 with STARTTLS is common; port 465 may require implicit TLS depending on the server.
- Look for authentication or certificate errors in the log.
- Re-enter the password if the DPAPI secret cannot be decrypted.

If no outbound channel is configured, alerts still appear in the app. There is no Windows notification channel and no telemetry fallback.

## DNS

| Symptom | Check |
|---|---|
| Timeout | UDP/TCP 53 blocked, wrong server, or unreachable VLAN. Compare with `nslookup`. |
| `No records returned` | The server answered `NOERROR` with no answers, or the name/type is wrong. |
| Wrong answer on one server | Use **Compare**. Split-horizon DNS and caches can differ. |
| Truncated response | TCP 53 may be blocked. Check the firewall. |
| Pi-hole returns no data | v6 API or wrong token/endpoint. This release targets the v5 legacy API. |
| Cloudflare zone not found | Token lacks Zone:Read or the zone is in another account. |
| AdGuard 401/403 | Re-enter the username/password; verify admin API access. |
| Windows DNS module missing | Install RSAT DNS Server tools. |

## DHCP

| Symptom | Check |
|---|---|
| File not found | Use a local path or a UNC share the current user can read. |
| ISC zero leases | Inspect the file; the parser expects standard `lease { hardware ethernet ... }` blocks. |
| Kea zero leases | Confirm the memfile CSV and its `address`/`hwaddr` header. |
| Windows DHCP PowerShell failed | Install the `DhcpServer` module, test `Get-DhcpServerv4Scope`, verify WinRM and permissions. |
| Reservations missing | ISC/Kea reservations are not imported; only leases are read for those providers. |
| Duplicate after import | Keep one source of truth and remove/merge the local duplicate. |

## Reports and exports

- Reports are written to `exports\` under the data folder. Check write permission and free space.
- CSV is UTF-8 with a BOM for Excel compatibility.
- PDF is A4 landscape; very long tables paginate.
- If a report is empty, check the date range and whether the underlying data exists.
- **Open folder** uses the Windows shell; if it fails, navigate to the path manually.
- Do not email a full export without reviewing device names, MACs, notes and integration endpoints.

## UI and theme

- If text is unreadable, switch themes once and back. The shipped styles bind text to theme brushes; a custom resource dictionary can override them.
- The dark title bar follows the theme on Windows 10/11. If the title bar stays light, the DWM call may be blocked by a remote session or a third-party window manager.
- At high DPI, use the Windows display scaling setting; the app is per-monitor DPI aware through the manifest. If a third-party scaler is active, disable it for `IPManager.exe`.

## Performance

| Area | Action |
|---|---|
| Slow startup | Check database size and antivirus scanning of the data folder. A huge `MonitoringResults` table slows queries; reduce retention. |
| Slow dashboard | Refresh after a scan completes, not during it. Large subnets are summarized, but device queries can still take time. |
| Slow map | The map is `/24` only; a smaller subnet does not render a map. Change to a `/24` in Subnet Manager. |
| Slow reports | Narrow the date range and avoid exporting all reports at once. |
| High memory | Close unused pages; they are singleton view models by design. Check monitoring result count and restart after retention cleanup. |

## Resetting data (last resort)

1. Close the application.
2. Copy `%LOCALAPPDATA%\IPManager` (or `<portable>\data`) to a safe location.
3. Rename `ipmanager.db` to `ipmanager.db.old` (keep the `-wal`/`-shm` files with it).
4. Start the app. It creates a new database and OUI seed.
5. Restore a backup if you need the old data.

Never delete the only copy. The application cannot recover a deleted database.

## Collecting information for a bug report

Include:

- IP Manager version (Settings → General),
- Windows version and build (`winver`),
- installed vs portable,
- exact steps to reproduce,
- expected vs actual behavior,
- the relevant log excerpt, with tokens, passwords, MAC addresses and internal names removed,
- whether the issue is reproducible after a backup/restore or with a fresh database.

Do not attach the database, raw logs or screenshots containing secrets to a public issue. Redact internal hostnames and public IPs if the report is public.
