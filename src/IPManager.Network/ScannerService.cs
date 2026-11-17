using System.Collections.Concurrent;
using System.Diagnostics;
using IPManager.Core;

namespace IPManager.Network;

public sealed class ScanOptions
{
    public int Concurrency { get; set; } = 32;
    public int PingTimeoutMs { get; set; } = 900;
    public int PingRetries { get; set; } = 1;
    public int TcpTimeoutMs { get; set; } = 700;
    public int DelayBetweenProbesMs { get; set; }
    public int MaxAddresses { get; set; } = 8192;
    public bool EnableArp { get; set; } = true;
    public bool EnableIcmp { get; set; } = true;
    public bool EnableTcp { get; set; } = true;
    public bool EnableReverseDns { get; set; } = true;
    public bool EnableMdns { get; set; } = true;
    public bool EnableNetBios { get; set; } = true;
    public IReadOnlyList<int> TcpPorts { get; set; } = new[] { 80, 443, 22 };
    public bool AllowExternalRanges { get; set; }
    public string? ArpInterfaceIp { get; set; }
    public int MdnsTimeoutMs { get; set; } = 2200;

    public static IReadOnlyList<int> ParsePorts(string? text)
    {
        var ports = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return ports;
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var p) && p is > 0 and <= 65535 && !ports.Contains(p))
                ports.Add(p);
        }
        return ports;
    }
}

public sealed class ScanRequest
{
    public IReadOnlyList<string> Targets { get; set; } = Array.Empty<string>();
    public ScanOptions Options { get; set; } = new();
}

public sealed record DiscoveredHost
{
    public string IpAddress { get; init; } = "";
    public string? MacAddress { get; init; }
    public string? Hostname { get; init; }
    public string? DnsName { get; init; }
    public string? NetBiosName { get; init; }
    public string? Vendor { get; init; }
    public bool RespondedIcmp { get; init; }
    public bool RespondedTcp { get; init; }
    public bool SeenInArp { get; init; }
    public bool SeenInMdns { get; init; }
    public bool LocallyAdministeredMac { get; init; }
    public IReadOnlyList<int> OpenPorts { get; init; } = Array.Empty<int>();
    public IReadOnlyList<DiscoveryMethod> Sources { get; init; } = Array.Empty<DiscoveryMethod>();
    public long? ResponseMs { get; init; }

    public bool IsAlive => RespondedIcmp || RespondedTcp || SeenInMdns || SeenInArp;
    /// <summary>Only present in the ARP cache; the entry may be stale.</summary>
    public bool ArpOnly => IsAlive && !RespondedIcmp && !RespondedTcp && !SeenInMdns;
    public string BestName => Hostname ?? NetBiosName ?? DnsName ?? "";
    public string SourceText => string.Join(", ", Sources.Select(S => S.ToString()));
    public string OpenPortsText => OpenPorts.Count == 0 ? "none detected" : string.Join(", ", OpenPorts);
    public string EvidenceText => ArpOnly
        ? "ARP/neighbor entry only - the host may be offline and the entry stale"
        : string.Join(" · ", new[]
        {
            RespondedIcmp ? "ICMP reply" : null,
            RespondedTcp ? "TCP accept" : null,
            SeenInMdns ? "mDNS announcement" : null,
            SeenInArp ? "ARP/neighbor entry" : null
        }.Where(v => v is not null));
}

public sealed record ScanProgress
{
    public string CurrentTarget { get; init; } = "";
    public int AddressesChecked { get; init; }
    public int AddressesTotal { get; init; }
    public int DevicesFound { get; init; }
    public int Errors { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Message { get; init; }
    public double Percent => AddressesTotal <= 0 ? 0 : Math.Round(AddressesChecked * 100.0 / AddressesTotal, 1);
}

public sealed record ScanResult
{
    public IReadOnlyList<DiscoveredHost> Hosts { get; init; } = Array.Empty<DiscoveredHost>();
    public IReadOnlyList<string> TargetDescriptions { get; init; } = Array.Empty<string>();
    public int AddressesChecked { get; init; }
    public int ErrorCount { get; init; }
    public bool Cancelled { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed record TargetParseResult
{
    public IReadOnlyList<Ipv4Address> Addresses { get; init; } = Array.Empty<Ipv4Address>();
    public IReadOnlyList<string> Descriptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool ContainsPublicAddresses { get; init; }
    public bool ExceededLimit { get; init; }
}

public static class ScanTargetParser
{
    public static TargetParseResult Parse(IEnumerable<string> specification, int maxAddresses = 8192)
    {
        var set = new HashSet<uint>();
        var descriptions = new List<string>();
        var errors = new List<string>();
        var publicFound = false;
        var exceeded = false;

        var tokens = specification
            .SelectMany(s => (s ?? "").Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        foreach (var token in tokens)
        {
            if (token.Contains('/'))
            {
                if (!Ipv4Network.TryParseCidr(token, out var network, out var error))
                {
                    errors.Add($"{token}: {error}");
                    continue;
                }

                if (network.PrefixLength is < 16 and > 8 && network.TotalAddresses > maxAddresses)
                {
                    // still enumerable below; the limit check reports a clear error
                }

                descriptions.Add(network.Cidr);
                var count = 0L;
                foreach (var host in network.Hosts(maxAddresses + 1))
                {
                    if (host.Value == 0 || host.Value == uint.MaxValue) continue;
                    if (!host.IsPrivate) publicFound = true;
                    if (set.Add(host.Value)) count++;
                    if (count > maxAddresses) { exceeded = true; break; }
                }
                continue;
            }

            if (token.Contains('-'))
            {
                var parts = token.Split('-', 2);
                if (!Ipv4Address.TryParse(parts[0], out var start))
                {
                    errors.Add($"{token}: the range start is not a valid IPv4 address.");
                    continue;
                }

                Ipv4Address end;
                var endText = parts[1].Trim();
                if (endText.Contains('.'))
                {
                    if (!Ipv4Address.TryParse(endText, out end))
                    {
                        errors.Add($"{token}: the range end is not a valid IPv4 address.");
                        continue;
                    }
                }
                else
                {
                    if (!byte.TryParse(endText, out var last))
                    {
                        errors.Add($"{token}: the range end must be a full address or the last octet.");
                        continue;
                    }
                    end = new Ipv4Address((start.Value & 0xFFFFFF00) | last);
                }

                if (end.Value < start.Value)
                {
                    errors.Add($"{token}: the range end is lower than the start.");
                    continue;
                }

                if (end.Value - start.Value + 1 > maxAddresses)
                {
                    exceeded = true;
                    continue;
                }

                descriptions.Add($"{start}-{end}");
                for (var v = start.Value; v <= end.Value; v++)
                {
                    if (set.Count > maxAddresses) { exceeded = true; break; }
                    if (!new Ipv4Address(v).IsPrivate) publicFound = true;
                    set.Add(v);
                }
                continue;
            }

            if (!Ipv4Address.TryParse(token, out var single))
            {
                errors.Add($"{token}: not a valid IPv4 address, CIDR range or address range.");
                continue;
            }

            if (!single.IsPrivate) publicFound = true;
            descriptions.Add(single.ToString());
            set.Add(single.Value);
            if (set.Count > maxAddresses) exceeded = true;
        }

        var addresses = set.OrderBy(v => v).Take(maxAddresses + 1).Select(v => new Ipv4Address(v)).ToList();
        if (addresses.Count > maxAddresses) exceeded = true;
        if (exceeded)
            errors.Add($"The target list exceeds the configured limit of {maxAddresses:N0} addresses. Narrow the range or raise the limit in Settings.");

        if (tokens.Count == 0) errors.Add("Enter at least one IP address, CIDR range or address range.");

        return new TargetParseResult
        {
            Addresses = addresses.Take(maxAddresses).ToList(),
            Descriptions = descriptions,
            Errors = errors,
            ContainsPublicAddresses = publicFound,
            ExceededLimit = exceeded
        };
    }
}

public sealed class ScannerService
{
    private readonly OuiDatabase _oui;
    private readonly IDnsResolver _dns;

    public ScannerService(OuiDatabase oui, IDnsResolver dns)
    {
        _oui = oui;
        _dns = dns;
    }

    public async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var options = request.Options;
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var parsed = ScanTargetParser.Parse(request.Targets, options.MaxAddresses);
        if (parsed.Errors.Count > 0 && parsed.Addresses.Count == 0)
            throw new ArgumentException(string.Join(Environment.NewLine, parsed.Errors));
        if (parsed.ContainsPublicAddresses && !options.AllowExternalRanges)
            throw new InvalidOperationException(
                "The target list contains addresses outside the private LAN ranges. Public or external scanning is disabled. " +
                "Confirm the target explicitly before enabling external ranges.");

        var addresses = parsed.Addresses;
        if (addresses.Count == 0)
            throw new ArgumentException("No addresses to scan.");

        var hosts = new ConcurrentDictionary<string, MutableHost>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentQueue<string>();
        var checkedCount = 0;
        var cancelled = false;

        progress?.Report(new ScanProgress
        {
            CurrentTarget = parsed.Descriptions.FirstOrDefault() ?? "",
            AddressesTotal = addresses.Count,
            Message = $"Starting scan of {addresses.Count:N0} addresses using {options.Concurrency} concurrent probes."
        });

        var arpEntries = options.EnableArp
            ? await ArpTableReader.ReadAsync(options.ArpInterfaceIp, ct: ct)
            : Array.Empty<ArpEntry>();
        foreach (var entry in arpEntries)
        {
            if (!Ipv4Address.TryParse(entry.IpAddress, out var addr)) continue;
            if (!addresses.Contains(addr)) continue;
            var h = hosts.GetOrAdd(entry.IpAddress, ip => new MutableHost(ip));
            h.MacAddress ??= entry.MacAddress;
            h.SeenInArp = true;
            h.Sources.Add(DiscoveryMethod.Arp);
        }

        var mdnsTask = options.EnableMdns
            ? MdnsDiscovery.DiscoverAsync(TimeSpan.FromMilliseconds(Math.Max(500, options.MdnsTimeoutMs)), ct)
            : Task.FromResult<IReadOnlyList<MdnsHost>>(Array.Empty<MdnsHost>());

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(options.Concurrency, 1, 512),
            CancellationToken = CancellationToken.None
        };

        try
        {
            await Parallel.ForEachAsync(addresses, parallelOptions, async (address, _) =>
            {
                if (ct.IsCancellationRequested) return;
                var ipText = address.ToString();
                var host = hosts.GetOrAdd(ipText, ip => new MutableHost(ip));

                try
                {
                    if (options.EnableIcmp)
                    {
                        var ping = await PingService.PingAsync(ipText, options.PingTimeoutMs, options.PingRetries, ct: ct);
                        if (ping.Success)
                        {
                            host.RespondedIcmp = true;
                            host.ResponseMs = ping.RoundtripMs;
                            host.Sources.Add(DiscoveryMethod.Icmp);
                        }
                        else if (ping.Error is not null && ping.Error is not ("Timed out" or "No response"))
                        {
                            errors.Enqueue($"{ipText}: ICMP {ping.Status} {ping.Error}");
                        }
                    }

                    if (options.EnableTcp && !host.RespondedIcmp)
                    {
                        // Only a small, configurable set of ports is probed so that blocked ICMP does not
                        // turn one /24 scan into thousands of connections.
                        foreach (var port in options.TcpPorts.Take(4))
                        {
                            ct.ThrowIfCancellationRequested();
                            var probe = await TcpProbeService.ProbeAsync(ipText, port, options.TcpTimeoutMs, ct);
                            if (!probe.Open) continue;
                            host.RespondedTcp = true;
                            host.OpenPorts.Add(port);
                            host.Sources.Add(DiscoveryMethod.Tcp);
                            host.ResponseMs ??= probe.ConnectMs;
                        }
                    }

                    if (host.IsAlive && options.EnableReverseDns)
                    {
                        var name = await _dns.ResolveNameAsync(ipText, Math.Min(1500, options.PingTimeoutMs + 400), ct);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            host.DnsName = name;
                            host.Hostname ??= name.Split('.')[0];
                            host.Sources.Add(DiscoveryMethod.Dns);
                        }
                    }

                    if (host.IsAlive && options.EnableNetBios)
                    {
                        var nb = await NetBiosResolver.QueryAsync(ipText, Math.Min(900, options.PingTimeoutMs), ct);
                        if (nb.Success)
                        {
                            host.NetBiosName = nb.Name;
                            host.Workgroup = nb.Workgroup;
                            host.Hostname ??= nb.Name;
                            if (host.MacAddress is null) host.MacAddress = nb.MacAddress;
                            host.Sources.Add(DiscoveryMethod.NetBios);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // cancellation is handled by the enclosing scope
                }
                catch (Exception ex)
                {
                    errors.Enqueue($"{ipText}: {ex.Message}");
                }
                finally
                {
                    var done = Interlocked.Increment(ref checkedCount);
                    if (options.DelayBetweenProbesMs > 0)
                    {
                        try { await Task.Delay(options.DelayBetweenProbesMs, ct); }
                        catch (OperationCanceledException) { }
                    }

                    if ((done % 4 == 0) || done == addresses.Count)
                    {
                        progress?.Report(new ScanProgress
                        {
                            CurrentTarget = ipText,
                            AddressesChecked = done,
                            AddressesTotal = addresses.Count,
                            DevicesFound = hosts.Count(kv => kv.Value.IsAlive),
                            Errors = errors.Count,
                            Elapsed = sw.Elapsed
                        });
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        // mDNS results are gathered after the probe loop so both run concurrently.
        IReadOnlyList<MdnsHost> mdnsHosts;
        try
        {
            mdnsHosts = await mdnsTask;
        }
        catch (OperationCanceledException)
        {
            mdnsHosts = Array.Empty<MdnsHost>();
            cancelled = cancelled || ct.IsCancellationRequested;
        }

        foreach (var mdns in mdnsHosts)
        {
            foreach (var ip in mdns.Addresses)
            {
                if (!Ipv4Address.TryParse(ip, out var addr) || !addresses.Contains(addr)) continue;
                var h = hosts.GetOrAdd(ip, x => new MutableHost(x));
                h.SeenInMdns = true;
                h.Hostname ??= mdns.HostName.Split('.')[0];
                h.MdnsName = mdns.HostName;
                h.Sources.Add(DiscoveryMethod.Mdns);
                foreach (var p in mdns.Ports) h.OpenPorts.Add(p);
            }
        }

        if (options.EnableArp && !cancelled)
        {
            try
            {
                var after = await ArpTableReader.ReadAsync(options.ArpInterfaceIp, ct: ct);
                foreach (var entry in after)
                {
                    if (!Ipv4Address.TryParse(entry.IpAddress, out var addr) || !addresses.Contains(addr)) continue;
                    var h = hosts.GetOrAdd(entry.IpAddress, ip => new MutableHost(ip));
                    h.MacAddress ??= entry.MacAddress;
                    h.SeenInArp = true;
                    h.Sources.Add(DiscoveryMethod.Arp);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue($"ARP refresh: {ex.Message}");
            }
        }

        var results = hosts.Values
            .Where(h => h.IsAlive)
            .Select(h =>
            {
                var mac = MacUtil.Normalize(h.MacAddress);
                return new DiscoveredHost
                {
                    IpAddress = h.IpAddress,
                    MacAddress = mac,
                    Hostname = h.Hostname,
                    DnsName = h.DnsName,
                    NetBiosName = h.NetBiosName,
                    Vendor = _oui.Lookup(mac),
                    RespondedIcmp = h.RespondedIcmp,
                    RespondedTcp = h.RespondedTcp,
                    SeenInArp = h.SeenInArp,
                    SeenInMdns = h.SeenInMdns,
                    LocallyAdministeredMac = MacUtil.IsLocallyAdministered(mac),
                    OpenPorts = h.OpenPorts.OrderBy(x => x).ToList(),
                    Sources = h.Sources.OrderBy(x => x).ToList(),
                    ResponseMs = h.ResponseMs
                };
            })
            .OrderBy(h => Ipv4Address.Parse(h.IpAddress).Value)
            .ToList();

        sw.Stop();
        return new ScanResult
        {
            Hosts = results,
            TargetDescriptions = parsed.Descriptions,
            AddressesChecked = checkedCount,
            ErrorCount = errors.Count,
            Errors = errors.Take(50).ToList(),
            Cancelled = cancelled || ct.IsCancellationRequested,
            Duration = sw.Elapsed,
            StartedAt = started,
            CompletedAt = DateTime.UtcNow
        };
    }

    private sealed class MutableHost
    {
        public MutableHost(string ip) => IpAddress = ip;
        public string IpAddress { get; }
        public string? MacAddress { get; set; }
        public string? Hostname { get; set; }
        public string? DnsName { get; set; }
        public string? NetBiosName { get; set; }
        public string? MdnsName { get; set; }
        public string? Workgroup { get; set; }
        public bool RespondedIcmp { get; set; }
        public bool RespondedTcp { get; set; }
        public bool SeenInArp { get; set; }
        public bool SeenInMdns { get; set; }
        public long? ResponseMs { get; set; }
        public HashSet<int> OpenPorts { get; } = new();
        public HashSet<DiscoveryMethod> Sources { get; } = new();
        public bool IsAlive => RespondedIcmp || RespondedTcp || SeenInArp || SeenInMdns;
    }
}

public sealed record TracerouteHop(int Hop, string? Address, long? RoundtripMs, string Status);

public static class DiagnosticsService
{
    public static async Task<IReadOnlyList<PingResult>> PingSeriesAsync(
        string host, int count, int intervalMs, int timeoutMs,
        IProgress<PingResult>? progress, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 100);
        var results = new List<PingResult>(count);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await PingService.PingAsync(host, timeoutMs, 0, ct: ct);
            results.Add(result);
            progress?.Report(result);
            if (i < count - 1) await Task.Delay(Math.Max(0, intervalMs), ct);
        }
        return results;
    }

    public static async Task<IReadOnlyList<TracerouteHop>> TracerouteAsync(
        string host, int maxHops, int timeoutMs, IProgress<TracerouteHop>? progress, CancellationToken ct)
    {
        var hops = new List<TracerouteHop>();
        maxHops = Math.Clamp(maxHops, 1, 64);
        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var (result, responder) = await PingService.TraceHopAsync(host, ttl, timeoutMs, ct);
            var hop = new TracerouteHop(ttl, responder, result.RoundtripMs,
                result.Success ? (result.Status == "TtlExpired" ? "Hop" : "Destination reached") : result.Status);
            hops.Add(hop);
            progress?.Report(hop);

            if (result.Success && result.Status != "TtlExpired") break;
            if (result.Error is "Destination host unreachable" or "Destination network unreachable") break;
        }
        return hops;
    }

    public static Task<TcpProbeResult> PortCheckAsync(string host, int port, int timeoutMs, CancellationToken ct)
        => TcpProbeService.ProbeAsync(host, port, timeoutMs, ct);
}
