# DNS Integrations

IP Manager has three DNS surfaces:

1. **Live lookups** through a built-in DNS wire client.
2. **Local DNS documentation** stored in the IP Manager database.
3. **Provider integrations** for reading and, where supported, writing records.

The built-in resolver is not a DNS server. Documented records are not served to the network; they are documentation.

## Live lookups

### Supported record types

`A`, `AAAA`, `CNAME`, `PTR`, `MX`, `TXT`, `NS`, `SRV` (the wire layer also understands `SOA` and `ANY`).

### Behaviour

- Queries are built by hand and sent over UDP to port 53.
- If the UDP response has the truncated bit set, the same query is retried over TCP.
- A server can be `system` (the first configured system DNS server), an IPv4/IPv6 address, `address:port`, or a hostname that resolves to an address.
- The default timeout is 3000 ms.
- A response code other than `NOERROR` is shown as a failure with the RCODE text.
- The lookup result includes the server actually used, duration, TTL, answers and the raw record value.
- Recent queries are kept for the page's history (server, type, success, duration, time).
- **Reverse** converts an IPv4 or IPv6 address to its PTR name and queries it.
- **Compare** runs the same query against the system resolver and explicit servers, so split-horizon and stale-cache differences are visible.

No query is sent anywhere until you press the lookup button. The page does not perform background DNS surveillance.

### What a lookup proves

A successful lookup proves that the queried server returned the record at that moment. It does not prove that the address belongs to the device you think it does, that the record is current, or that every client uses the same resolver. Compare servers when a name looks wrong.

## Local DNS documentation

The **DNS → Local records** table stores zone, name, type, value, TTL and notes. It is useful for:

- documenting static records that live on a router or registrar,
- recording the intended value before a change,
- keeping a per-zone inventory when the DNS server is read-only or inaccessible,
- reporting.

Local records are editable regardless of provider support. They are never pushed to a provider automatically.

## Provider matrix

| Provider | Read | Create | Delete | Authentication | Default endpoint |
|---|---|---|---|---|---|
| Pi-hole (API v5 legacy) | Yes | No | No | API token in the URL, stored with DPAPI | The Pi-hole base URL you enter |
| AdGuard Home | Yes | Yes (rewrite) | Yes (rewrite) | HTTP Basic username/password, password DPAPI-protected | The AdGuard Home base URL you enter |
| Cloudflare | Yes | Yes | Yes | Bearer API token, DPAPI-protected | `https://api.cloudflare.com/client/v4` |
| Windows DNS Server | Yes | No | No | Current Windows user via PowerShell | Server name; `DnsServer` module required |

### Pi-hole

**Requirements:** Pi-hole web interface reachable over HTTP(S), and an API token from **Settings → API / Web interface**. The current implementation targets the legacy v5 API.

**Test:** calls `GET /admin/api.php?summaryRaw&auth=<token>`.

**Read:** calls `GET /admin/api.php?customdns&auth=<token>` and converts each local DNS entry into a record. An IP answer becomes `A` or `AAAA`; a non-IP answer becomes `CNAME`.

**Limitations:**

- Read-only. Use the Pi-hole UI to change local DNS records.
- Pi-hole v6 uses a different API and is not supported by this provider; the page reports that no custom DNS data was returned.
- The token is sent as a query parameter because that is the v5 API design. Use HTTPS if the Pi-hole is not on a trusted segment.
- Zone is not used by the provider.

### AdGuard Home

**Requirements:** AdGuard Home reachable over HTTP(S), and a username/password with admin API access.

**Test:** `GET /control/status` with Basic auth.

**Read:** `GET /control/rewrite/list`. Each rewrite has a domain and an answer; an IP answer is typed `A`, otherwise `CNAME`.

**Create:** `POST /control/rewrite/add` with `{ "domain": "...", "answer": "..." }`.

**Delete:** `POST /control/rewrite/delete` with the same domain/answer pair.

**Limitations:**

- Only the rewrite list is managed. Filtering rules, upstreams and blocklists are not touched.
- AdGuard rewrites are not a full authoritative zone. The record types are effectively address/CNAME-style answers.
- Deleting matches the domain and answer exactly; a changed answer is a different rewrite.
- Basic auth over plain HTTP exposes the password on the wire. Use HTTPS or a trusted management network.

### Cloudflare

**Requirements:** a Cloudflare API token with **Zone:Read** and **DNS:Edit** for the target zone, and the zone name (for example `example.com`).

**Endpoint:** defaults to `https://api.cloudflare.com/client/v4`; can be overridden only if you intentionally use a proxy or test endpoint.

**Test:** `GET /user/tokens/verify` and checks for `"success": true`.

**Read:** resolves the zone id with `GET /zones?name=<zone>`, then lists up to 200 records with `GET /zones/<id>/dns_records?per_page=200`.

**Create:** `POST /zones/<id>/dns_records` with type, name, content and TTL. TTL `1` means automatic.

**Delete:** `DELETE /zones/<id>/dns_records/<record-id>`.

**Limitations:**

- The token must be scoped to the zone; a global key is not required and is not recommended.
- Record listing is capped at 200 records by the provider request. Use the Cloudflare dashboard for larger zones or paginate manually in a future version.
- Creating records can change public DNS. The app asks for confirmation before remote changes when **Confirm remote changes** is enabled in Settings.
- Proxied records and Cloudflare-specific flags are not edited; only type, name, content and TTL are sent on create.
- Deleting is permanent at Cloudflare. The confirmation and the record id in the table matter.

### Windows DNS Server

**Requirements:** the `DnsServer` PowerShell module (RSAT DNS Server tools) on the IP Manager computer and read permission on the target DNS server.

**Test:** checks whether the `DnsServer` module is available.

**Read:** runs `Get-DnsServerResourceRecord -ZoneName '<zone>'` (with `-ComputerName` when a server is set), flattens the record data, and converts `@` to the zone name.

**Limitations:**

- Read-only. Use the Windows DNS console to modify records.
- IPv4 and IPv6 records can be read; the value is a flattened `Format-List` string, so complex record types are harder to read than in the DNS console.
- The current Windows user must have permission. IP Manager does not store alternate credentials.
- Remote calls require WinRM/PowerShell remoting and firewall access.
- Only one zone is read at a time.

## Credentials and secrets

- Passwords and API tokens are protected with Windows DPAPI at current-user scope before being stored in the database.
- The UI never displays a saved secret back in clear text; re-enter it to replace it.
- A different Windows user or a copied database cannot decrypt another user's DPAPI secret. Re-enter the secret after a profile migration.
- Do not put tokens in the notes field, a report, or a screenshot.
- There is no cloud credential store and no telemetry that transmits secrets.

## Safety and change control

- Reads and tests are explicit actions.
- Writes are only attempted by providers with `CanWrite` (AdGuard Home and Cloudflare).
- The **Confirm remote changes** setting adds a confirmation dialog before a remote write. Leave it enabled.
- A write is not a backup. Record the previous value first or export the provider's records.
- Audit entries record tests, reads, creates and deletes with the provider and target where implemented.
- Deleting a record can affect every client that resolves through that server. Check the zone and record carefully.
- Do not point the app at a resolver or zone you do not administer.

## Troubleshooting

| Symptom | Check |
|---|---|
| `No DNS server is available` | The system has no DNS server, or the adapter has no configured resolver. Select an explicit server. |
| Timeout | UDP 53 blocked, wrong server, or an unreachable VLAN. Try the same server with `nslookup`. |
| Truncated answer still incomplete | TCP 53 may be blocked. Check the firewall and retry. |
| Pi-hole returns no custom DNS | Wrong token/endpoint, Pi-hole v6, or an empty local DNS list. |
| AdGuard 401/403 | Username/password or admin permission is wrong. Re-enter the password. |
| Cloudflare `Zone ... was not found` | Token lacks Zone:Read or the zone name is not in that account. |
| Cloudflare create fails | Token lacks DNS:Edit, the record is invalid for the zone, or a conflicting record exists. |
| Windows DNS module missing | Install RSAT DNS Server tools and reopen the page. |
| DPAPI decrypt error | The database was moved to another Windows user/profile. Re-enter the secret. |

See [Troubleshooting](TROUBLESHOOTING.md) for logs and connectivity checks.
