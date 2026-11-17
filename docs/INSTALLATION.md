# Installation

## Supported systems

| Item | Requirement |
|---|---|
| OS | Windows 10 or Windows 11, x64 |
| Runtime | None; the release is self-contained .NET 10 |
| Install scope | Per-user by default; no administrator rights required |
| Disk | About 150 MB installed, plus SQLite data and backups |
| Network | IPv4 LAN access; optional DHCP/DNS endpoints and SMTP/Telegram only if enabled |

IP Manager is a desktop application, not a Windows service. Closing the window stops discovery and monitoring.

## Installer (recommended)

1. Download `IPManager-Setup-x64.exe` from the [latest release](https://github.com/unupunct/IP-Manager/releases/latest).
2. Download `checksums.txt` from the same release.
3. Verify the download:

   ```powershell
   Get-FileHash .\IPManager-Setup-x64.exe -Algorithm SHA256
   ```

   Compare the value with the matching line in `checksums.txt`. Do not continue if it differs.
4. Run `IPManager-Setup-x64.exe`.
5. The bundle installs the MSI for the current user and creates:
   - `%LOCALAPPDATA%\Programs\IP Manager\IPManager.exe`
   - a Start Menu shortcut under **IP Manager**
   - a desktop shortcut
6. Launch **IP Manager** from the Start Menu.

The installer does not request elevation. It writes only to the current user's profile and HKCU registry.

### Silent install and uninstall

```powershell
# Install without UI
.\IPManager-Setup-x64.exe /install /quiet

# Uninstall without UI
.\IPManager-Setup-x64.exe /uninstall /quiet
```

The bundle also supports `/passive` for a progress-only install. Exit code `0` means success; `1603` is a fatal MSI error (see logs below).

### Upgrade

Run a newer `IPManager-Setup-x64.exe`. The bundle and MSI use a stable upgrade code, so the old version is replaced. The database and settings are not removed by an upgrade.

### Uninstall

Use **Settings → Apps → Installed apps → IP Manager → Uninstall**, or run:

```powershell
.\IPManager-Setup-x64.exe /uninstall
```

Uninstalling removes the application files and shortcuts. It deliberately leaves `%LOCALAPPDATA%\IPManager` (database, backups, logs, exports) in place. Delete that folder manually only if you want to remove all data.

## Portable mode

The portable ZIP contains the same self-contained application plus a `portable.flag` file.

1. Extract `IPManager-Portable-x64.zip` to a writable folder, for example `D:\Tools\IPManager`.
2. Run `IPManager.exe` from that folder.
3. Data is stored in `D:\Tools\IPManager\data`:
   - `ipmanager.db`
   - `backups\`
   - `logs\`
   - `exports\`
   - `oui\`

Do not run the portable build from a read-only folder. To switch a portable copy back to normal per-user data, delete `portable.flag` and restart the application.

## Where data lives

Normal install:

```
%LOCALAPPDATA%\IPManager\
  ipmanager.db
  config.json
  backups\
  logs\
  exports\
  oui\
```

Portable install:

```
<application folder>\data\
  ipmanager.db
  ...
```

`config.json` stores the optional data-directory override. A damaged or missing config falls back to the default LocalAppData location. The data-location override is changed in **Settings → General → Change data folder** and requires a restart. See [Database](DATABASE.md) for the schema and backup/restore details.

## Unsigned build

No Authenticode certificate is available for this release, so the EXE and MSI are unsigned:

- Windows SmartScreen may show **Unknown publisher**.
- Some organizations block unsigned installers by policy.
- The SHA-256 checksums in the release are the integrity check.
- The application does not auto-update or download code at runtime. The optional OUI vendor database download is a data file only and is triggered explicitly.

If you need a signed build, sign the MSI and bundle with your organization's certificate after building, then verify with `signtool verify /pa`.

## Firewall and antivirus

IP Manager uses outbound ICMP, TCP probes, DNS, mDNS multicast and optional HTTPS. It does not open an inbound listening port. If a firewall prompts on first run, allow **Private networks** only. Some endpoint-protection products rate-limit or block ARP/TCP probing; see [Troubleshooting](TROUBLESHOOTING.md).

## Installation logs

The bundle and MSI write logs to `%TEMP%`:

```
%TEMP%\IP_Manager_<timestamp>.log
%TEMP%\IP_Manager_<timestamp>_000_IPManager_Setup_x64.msi.log
```

For a verbose manual install:

```powershell
msiexec /i IPManager-Setup-x64.msi /qn /l*v "$env:TEMP\ipmanager-msi.log"
```

Common causes of a failed install:

| Symptom | Check |
|---|---|
| Exit code 1603 | Read the MSI log; look for `Return value 3` and the preceding action. |
| "requires 64-bit Windows" | The build is x64; check `PROCESSOR_ARCHITECTURE`. |
| SmartScreen blocks the file | Verify the checksum and use **More info → Run anyway** only if the hash matches. |
| Antivirus quarantines the installer | Restore from quarantine or use the portable ZIP in an allowed folder. |

## Verifying the installed build

```powershell
(Get-Item "$env:LOCALAPPDATA\Programs\IP Manager\IPManager.exe").VersionInfo |
  Select-Object ProductVersion, FileVersion
```

The current release is version `1.0.0`.
