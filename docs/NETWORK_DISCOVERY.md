# Network Discovery

IP Manager discovers hosts on IPv4 networks using several independent, mostly passive or low-impact methods. The goal is evidence, not guesswork: every host carries the methods that saw it, and a failed probe is treated differently from a negative observation.

## Safety model

| Rule | Behaviour |
|---|---|
| Default scope | Private IPv4 ranges only: `10/8`, `172.16/12`, `192.168/16`, `100.64/10` (CGNAT), `169.254/16` (link-local) and loopback. |
| Public or third-party range | The scan parser rejects it unless **Allow external ranges** is enabled in Settings. The UI then asks for explicit confirmation. |
| Maximum targets | `MaxAddresses` (default 8192) prevents an accidental `/8` sweep. |
| Probe rate | `Concurrency` (default 32) and `DelayBetweenProbesMs` (default 0) bound the load. |
| Ports | Only the configured TCP ports, and only the first four per host, are attempted. No full port scan. |
| Writes | Discovery never writes to routers, DHCP servers or DNS servers. It reads the local ARP/neighbour table and sends probes. |
| Persistence | Results are persisted only when you run discovery from the application. Diagnostics probes are not saved automatically. |

A scan of a public range can be intrusive or unlawful without authorization. Leave external scanning disabled unless you own the range or have written permission.

## Target syntax

The **Targets** box accepts:

```text
192.168.1.0/24
192.168.1.10-192.168.1.40
192.168.1.10
192.168.1.10, 192.168.1.20
```

Multiple entries can be separated by commas, semicolons, spaces or newlines. Host bits in a CIDR are normalized; the network and broadcast addresses are skipped for prefixes shorter than `/31`.

`Use this adapter` selects the active adapter's IPv4 address and prefix and produces a CIDR that covers that interface. Review it before scanning; a `/16` adapter produces 65,536 targets and will hit the address cap. Reduce the range deliberately.

## Detection methods

| Method | How it works | What it proves | Limits |
|---|---|---|---|
| ARP / neighbour table | Reads the Windows IP Helper neighbour table before and after the probe pass. | The IP was recently associated with a MAC on the local segment. | ARP entries can be stale; an ARP-only result is flagged. Not visible across routers. |
| ICMP echo | Windows ICMP echo with timeout and retries. | The host answered an echo request. | Many hosts block ICMP. A timeout alone is not proof the host is absent. |
| TCP connect | Attempts a connection to up to the first four configured ports. | At least one TCP port accepted a connection. | Closed ports and filtered ports look alike. The host may be up with all probed ports closed. |
| Reverse DNS | PTR lookup for a host that is already alive. | A name exists in DNS. | Optional, and only enriches; it never makes a host alive. |
| mDNS | Sends a PTR query for `_services._dns-sd._udp.local` to `224.0.0.251:5353` and reads announcements. | A device announced services over multicast DNS. | Mostly Apple, Linux and IoT devices; Windows often does not announce. |
| NetBIOS | Node-status query on UDP 137 to a host that is already alive. | A NetBIOS name and sometimes a MAC. | Legacy; often disabled or firewalled. |
| DNS resolver / table enrichment | System resolver and cached names. | A hostname for an address. | Name collisions and stale PTR records are possible. |

### How a host is considered found

A host is alive when **any** of these is true:

- an ICMP echo succeeded,
- a TCP connection succeeded,
- an mDNS announcement was received from the address,
- the address is present in the local ARP/neighbour table.

Reverse DNS, NetBIOS and OUI lookup are enrichment only. They do not make a host alive by themselves.

An **ARP-only** host is displayed with the warning:

> ARP/neighbor entry only - the host may be offline and the entry stale

This warning must be respected. It is common to see recently disconnected devices in the ARP cache.

## Scan lifecycle

1. The targets are parsed and the address count is checked against `MaxAddresses`.
2. Every target is tested for external scope. If any target is public, the scan is refused unless external ranges were explicitly allowed and confirmed.
3. The ARP/neighbour table is read and matching entries are seeded as candidates.
4. The mDNS listener starts for the configured window (default 2.2 s).
5. Targets are processed with bounded concurrency. Each host gets ICMP, then up to four TCP probes if ICMP did not answer. Reverse DNS, NetBIOS and mDNS enrichment run only for live hosts, subject to timeouts.
6. The ARP table is read again to catch neighbours that answered ARP but not IP probes.
7. Results are filtered to live hosts and returned to the page.
8. When saved by the application, the inventory service updates devices, addresses, observations, last-seen times and alerts.

Progress reports include the current target, checked/found counts, errors and elapsed time. **Cancel** stops new probes and waits for in-flight operations to finish.

## Offline semantics

Discovery does **not** mark an address offline just because it was not found in a scan. Offline is a monitoring state:

- Monitoring performs checks at the configured interval.
- A device is marked **Offline** only after its last successful check is older than the **Offline threshold** (default 5 minutes).
- A single timeout, a blocked ICMP reply, or a scan that missed the host does not change the device to offline.
- A successful check restores **Online** and can generate a recovery alert.

This distinction avoids the common false-positive pattern where a firewall starts dropping ICMP and the whole inventory suddenly looks offline.

## Conflict evidence

Discovery feeds the conflict analyzer. Two kinds of problems are kept separate.

### Live network observations

An address is suspicious when the same IP is observed with more than one usable MAC address. The analyzer requires:

- at least two **fresh** MAC observations inside the fresh window (default 15 minutes), and
- at least two independent evidence sources (for example ARP + ICMP, or ARP + TCP), and
- at least two total observations.

Only then is the finding **Confirmed**. If the MACs are fresh but the independent-source or observation threshold is not met, the finding is **Suspected**. If the only competing MAC is older than the stale window (default 6 hours), the finding stays **Suspected** and the evidence says that stale ARP data cannot confirm a conflict.

An ARP-only observation is deliberately weak. A router that reuses an address can leave a stale ARP entry for hours; that is not proof that two devices are active at once.

### Documentation duplicates

The same address assigned to multiple records in the IP Manager database, or duplicate reservations, is reported as a **database duplicate** with 100% documentation confidence. It is not a live conflict. The evidence lists every record, device and MAC so you can clean up the documentation.

## What discovery does not do

- It does not scan IPv6 neighbours or build an IPv6 map.
- It does not query SNMP, LLDP or switch MAC tables.
- It does not log in to routers, DHCP servers or DNS servers.
- It does not brute-force DNS names or sweep ports outside the configured list.
- It does not send packets to public ranges by default.
- It does not prove that a host is offline; only monitoring can do that after the threshold.
- It does not prove that an unused address is safe to assign. Check the DHCP pool and the address map before allocating.

## Practical settings

| Environment | Suggested change |
|---|---|
| Small home LAN `/24` | Defaults are fine. `Concurrency 32`, `PingTimeout 900 ms`, `Retries 1`, TCP ports `80,443,22,445,3389,8080,9100,631,554,5000`. |
| Wi-Fi with client isolation | Expect ARP/ICMP gaps. Use TCP probes to common device ports where allowed. |
| VLANs or routed subnets | A single scan cannot see other broadcast domains. Document those subnets manually or run discovery from a host in each VLAN. |
| Very quiet or battery-powered IoT | Increase mDNS window and ping timeout slightly; keep concurrency modest. |
| Large flat network | Raise `MaxAddresses` only for an authorized range and reduce concurrency to avoid saturating the segment. |

## Troubleshooting a scan

| Symptom | Likely cause | Action |
|---|---|---|
| Fewer hosts than expected | ICMP blocked, client isolation, host sleeping | Enable TCP/mDNS, check the same VLAN, ping from the same host manually. |
| Host appears with no MAC | ARP entry expired, routed target, or MAC unavailable | The host is still evidence-backed; run another scan or query the local ARP cache. |
| MAC is locally administered | Virtual adapter, VM, privacy MAC, or randomized Wi-Fi MAC | Check the device and the source; randomized MACs are expected on modern phones. |
| Scan immediately refuses the range | Public range without external confirmation, or over `MaxAddresses` | Use the documented private range, or explicitly enable external scanning after authorization. |
| Scan is slow | Timeouts, retries, high target count, or a busy Wi-Fi link | Reduce targets or retries, or increase concurrency carefully. |
| mDNS finds names but not addresses | Announcements carry names and ports, not always an A/AAAA record | The address is associated when the packet source matches the target; otherwise it only enriches. |

See [Troubleshooting](TROUBLESHOOTING.md) for logs and network-level checks.
