# Build and Release

This document describes how the Windows release is built, verified and published. The canonical script is [`tools/build-release.ps1`](../tools/build-release.ps1).

## Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| Windows | 10/11 x64 | The publish and installer are x64-only |
| .NET SDK | 10.0.403 (10.0 LTS) | `dotnet --version` |
| WiX Toolset | 5.0.2 | `wix --version`; the Bal extension is required for the bootstrapper |
| PowerShell | 5.1 or 7+ | The script is written for Windows PowerShell 5.1 compatibility |
| Git / GitHub CLI | current | Only for publishing; not needed to build |

The application targets `net10.0-windows` and is published self-contained for `win-x64`. No separate runtime is installed on the target.

### WiX Bal extension

`installer/Bundle.wxs` uses `WixStandardBootstrapperApplication`, provided by `WixToolset.Bal.wixext`. The build script looks for:

```
%USERPROFILE%\.wix\extensions\WixToolset.Bal.wixext\5.0.2\wixext5\WixToolset.BootstrapperApplications.wixext.dll
```

If it is absent, the script downloads the 5.0.2 NuGet package and copies the DLL into that cache path. `wix extension list` can report the extension as damaged on some installations even when the direct DLL path works; this is why the script passes `-ext <dll>` explicitly instead of relying only on the extension manager.

Manual alternative:

```powershell
$bal = "$env:USERPROFILE\.wix\extensions\WixToolset.Bal.wixext\5.0.2\wixext5\WixToolset.BootstrapperApplications.wixext.dll"
wix build installer/Bundle.wxs -d "MsiPath=$PWD\release\IPManager-Setup-x64.msi" -d "ProductVersion=1.0.0" -ext $bal -arch x64 -o release/IPManager-Setup-x64.exe
```

## One-command release build

```powershell
pwsh -File tools/build-release.ps1 -Version 1.0.0
```

The script:

1. validates `dotnet` and `wix`,
2. deletes old release binaries and publish folders (keeps `release/*.md` and other non-binaries),
3. builds the solution in Release,
4. runs the test project unless `-SkipTests` is specified,
5. publishes `src/IPManager.App` self-contained to `build/publish`,
6. copies the publish output to `build/portable`, adds `portable.flag`, and writes `release/IPManager-Portable-x64.zip`,
7. builds `release/IPManager-Setup-x64.msi`,
8. resolves/downloads the Bal extension and builds `release/IPManager-Setup-x64.exe`,
9. writes `release/checksums.txt` with SHA-256 for the MSI, EXE and ZIP,
10. prints sizes and the `gh release create` hint.

Useful switches:

```powershell
# Build the app and portable ZIP, no MSI/EXE
pwsh -File tools/build-release.ps1 -Version 1.0.0 -NoInstaller

# Reuse an existing build/test state while iterating on packaging
pwsh -File tools/build-release.ps1 -Version 1.0.0 -SkipTests
```

Do not use `-SkipTests` for the actual release.

## Manual build steps

### Build and test

```powershell
dotnet build IPManager.slnx -c Release --nologo -v minimal
dotnet test tests\IPManager.Tests\IPManager.Tests.csproj -c Release --nologo -v q
```

Tests use isolated temporary databases and `EnsureCreated`; they do not touch `%LOCALAPPDATA%\IPManager`.

### Publish

```powershell
dotnet publish src\IPManager.App\IPManager.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o build\publish --nologo -v minimal
```

Why not single-file: WPF, native SQLite assets and the app icon are easier to service and inspect as a normal folder. The MSI compresses the folder into a ~55 MB installer. The publish folder is roughly 145 MB uncompressed.

The app project copies `README.md`, `LICENSE`, `docs\` and release notes into the publish output. Those files are included in the installed application folder.

### MSI

```powershell
$publish = (Resolve-Path build\publish).Path
$icon = (Resolve-Path src\IPManager.App\Assets\app.ico).Path
wix build installer\Package.wxs `
  -d "PublishDir=$publish" `
  -d "ProductVersion=1.0.0" `
  -d "IconPath=$icon" `
  -arch x64 `
  -o release\IPManager-Setup-x64.msi
```

The MSI is:

- **per-user** (`Scope="perUser"`), installed under `%LOCALAPPDATA%\Programs\IP Manager`,
- UI-less; the EXE bundle provides the user experience,
- x64-only via the `VersionNT64` launch condition,
- using `InstallerVersion="500"` and `MajorUpgrade`,
- creating Start Menu and desktop shortcuts with HKCU key paths,
- not setting `ALLUSERS` or a machine-wide service.

There is deliberately no `VersionNT >= 1000` launch condition. On some Windows 11 systems the MSI engine can report a compatibility shim as `VersionNT=603`; a Windows 10/11 condition based on that value falsely rejects a valid OS. The x64 check is sufficient for this build.

### Burn bundle

```powershell
$bal = "$env:USERPROFILE\.wix\extensions\WixToolset.Bal.wixext\5.0.2\wixext5\WixToolset.BootstrapperApplications.wixext.dll"
wix build installer\Bundle.wxs `
  -d "MsiPath=$PWD\release\IPManager-Setup-x64.msi" `
  -d "ProductVersion=1.0.0" `
  -ext $bal `
  -arch x64 `
  -o release\IPManager-Setup-x64.exe
```

The bundle:

- uses `hyperlinkLicense` and links to the repository `LICENSE`,
- chains the MSI with `Vital="yes"`,
- supports `/install`, `/uninstall`, `/quiet` and `/passive`,
- has its own stable upgrade code (`4F8FE395-E8F6-4F1A-AE66-030AD646C12A`).

### Portable ZIP

```powershell
Remove-Item build\portable -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item build\publish build\portable -Recurse
Set-Content build\portable\portable.flag "Portable mode" -Encoding ASCII
Compress-Archive -Path build\portable\* -DestinationPath release\IPManager-Portable-x64.zip -CompressionLevel Optimal
```

The flag must not be present in the MSI payload. The portable folder is a separate copy for this reason.

### Checksums

```powershell
Get-ChildItem release\IPManager-Setup-x64.exe, release\IPManager-Setup-x64.msi, release\IPManager-Portable-x64.zip |
  ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
  } | Set-Content release\checksums.txt -Encoding ASCII
```

## Installer verification

Test both paths on a clean user profile or a VM. Keep a snapshot if possible.

### Direct MSI

```powershell
$msi = (Resolve-Path release\IPManager-Setup-x64.msi).Path
$log = "$env:TEMP\ipmanager-msi.log"
Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /l*v `"$log`"" -Wait
```

Verify:

- exit code `0`,
- `%LOCALAPPDATA%\Programs\IP Manager\IPManager.exe` exists,
- the Start Menu shortcut and desktop shortcut exist,
- the HKCU uninstall entry is present,
- the app launches and opens its database,
- uninstall removes the files/shortcuts but keeps `%LOCALAPPDATA%\IPManager`.

### Bundle

```powershell
Start-Process .\release\IPManager-Setup-x64.exe -ArgumentList "/install","/quiet" -Wait
Start-Process .\release\IPManager-Setup-x64.exe -ArgumentList "/uninstall","/quiet" -Wait
```

Verify the same points, plus the Burn ARP entry and the `%LOCALAPPDATA%\Package Cache` copy. A clean uninstall should remove both the bundle and MSI registrations.

### Automated smoke checks

```powershell
$install = Join-Path $env:LOCALAPPDATA "Programs\IP Manager"
Test-Path (Join-Path $install "IPManager.exe")
Test-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\IP Manager\IP Manager.lnk")
Test-Path (Join-Path ([Environment]::GetFolderPath("Desktop")) "IP Manager.lnk")
(Get-Item (Join-Path $install "IPManager.exe")).VersionInfo.ProductVersion
```

The current release is `1.0.0`. Do not ship a build that fails install or launch.

## Screenshots

Page screenshots live in `screenshots/pages` and are referenced by the README and release notes.

```powershell
# Start the built app, then capture all 16 pages
pwsh -File tools/Capture-AllPages.ps1
```

The capture script uses UI Automation to select navigation items and `PrintWindow` to capture the window at a stable size. Recapture after UI changes. Do not include the app in a state that shows credentials or secrets.

## Versioning

- Product/assembly/version: `1.0.0`.
- WiX `ProductVersion` receives the same value.
- The MSI `UpgradeCode` must **never** change for a product line.
- The bundle `UpgradeCode` must **never** change either.
- Component GUIDs are auto-generated by the WiX v4+ `Files` element; keep the installer source under version control.
- Assembly version is `1.0.0.0`; update it deliberately for a servicing release.

## Publishing to GitHub

The repository is expected at `https://github.com/unupunct/IP-Manager`.

```powershell
git init
git add .
git commit -m "IP Manager 1.0.0"
git branch -M main
git remote add origin https://github.com/unupunct/IP-Manager.git
git push -u origin main
```

Set the About section and topics:

```powershell
gh repo edit unupunct/IP-Manager `
  --description "Local-first Windows LAN IP address manager: discovery, documentation, conflicts, DHCP/DNS, 256-square /24 map" `
  --homepage "https://github.com/unupunct/IP-Manager#readme" `
  --add-topic wpf --add-topic dotnet --add-topic ipam --add-topic network --add-topic lan --add-topic sqlite --add-topic windows
```

Create the release and attach the installer, portable ZIP and checksums:

```powershell
gh release create v1.0.0 `
  release/IPManager-Setup-x64.exe `
  release/IPManager-Setup-x64.msi `
  release/IPManager-Portable-x64.zip `
  release/checksums.txt `
  --title "IP Manager 1.0.0" `
  --notes-file release/RELEASE_NOTES.md
```

The release body should state the unsigned status. Do not upload the raw `build/publish` folder, PDBs, logs or a database.

### Git ignores

`release/*.exe`, `release/*.msi`, `release/*.zip` and `release/*.wixpdb` are ignored. `release/RELEASE_NOTES.md` and `release/checksums.txt` can be committed; checksums are also attached to the release. `build/`, `bin/`, `obj/`, logs, databases and `portable.flag` are ignored.

## Code signing (optional)

If a certificate becomes available:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 release\IPManager-Setup-x64.msi
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 release\IPManager-Setup-x64.exe
signtool verify /pa release\IPManager-Setup-x64.exe
```

Sign the MSI before building the bundle when possible, and sign the bundle after it is built. Signing does not change the application behavior. The release notes must be updated to state that the build is signed and by whom.

## Release checklist

- [ ] `dotnet build IPManager.slnx -c Release` — 0 errors.
- [ ] `dotnet test ... -c Release` — all tests pass.
- [ ] Version is updated in the project/assembly and `ProductVersion`.
- [ ] `release/RELEASE_NOTES.md` reflects the actual changes and known limitations.
- [ ] `pwsh -File tools/build-release.ps1 -Version <version>` completed.
- [ ] MSI install and uninstall verified.
- [ ] Bundle install and uninstall verified.
- [ ] Installed app launches and reads/writes its database.
- [ ] Portable ZIP extracted to a writable folder and launched.
- [ ] Checksums match the attached assets.
- [ ] Screenshots recaptured if the UI changed.
- [ ] README download links and docs point to the right release.
- [ ] Unsigned status is documented.
- [ ] Git commit pushed and GitHub About/topics set.
- [ ] GitHub Release created with EXE, MSI, ZIP and checksums.

## Troubleshooting the build

| Symptom | Cause/action |
|---|---|
| `wix` not found | Install WiX 5 as a global tool or add it to PATH. |
| Bal extension missing/damaged | Use the direct `-ext` path, or let the script download it. |
| MSI exit 1603 | Read the MSI log; check for locked files and antivirus. Do not add a `VersionNT` condition that misreads Windows 11. |
| Bundle build fails | Confirm the MSI exists at the `MsiPath` passed to `Bundle.wxs`. |
| Publish is huge | Self-contained WPF includes the runtime; that is expected. The MSI compresses it. |
| Tests fail with a database lock | Ensure no test run/app instance is using the same temp workspace. Tests should use isolated temp paths. |
| `Compress-Archive` path error | Re-run from the repository root; the script uses absolute paths. |
| `Get-FileHash` mismatch | Rebuild both the asset and checksums; never edit the checksum by hand. |

## CI notes

There is no CI workflow in this release. If adding one:

- use `windows-latest` with .NET 10,
- restore, build and test,
- cache the WiX/Bal extension or install WiX explicitly,
- publish artifacts and checksums,
- do not attempt Authenticode signing without a secret and a timestamp service,
- upload the EXE/MSI/ZIP as workflow artifacts before creating a release.
