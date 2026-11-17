using System.Net;
using System.Net.Sockets;

namespace IPManager.Network;

public sealed record MdnsHost
{
    public string HostName { get; init; } = "";
    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> Ports { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Multicast DNS (RFC 6762) discovery using unicast-response queries for common service types.
/// Only records actually returned by responders are reported.
/// </summary>
public static class MdnsDiscovery
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    public const int MdnsPort = 5353;

    private static readonly string[] ServiceTypes =
    {
        "_services._dns-sd._udp.local",
        "_http._tcp.local",
        "_https._tcp.local",
        "_workstation._tcp.local",
        "_ipp._tcp.local",
        "_printer._tcp.local",
        "_airplay._tcp.local",
        "_googlecast._tcp.local",
        "_smb._tcp.local",
        "_ssh._tcp.local",
        "_raop._tcp.local",
        "_device-info._tcp.local"
    };

    public static async Task<IReadOnlyList<MdnsHost>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var hosts = new Dictionary<string, MdnsHostBuilder>(StringComparer.OrdinalIgnoreCase);
        var services = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var observedAt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            try { udp.JoinMulticastGroup(MulticastAddress); } catch { /* some adapters do not support multicast */ }
            try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1); } catch { }

            var queries = ServiceTypes.Select(s => (s, DnsRecordType.PTR)).ToList();
            var packet = DnsWire.BuildQuery(queries, 0, recursionDesired: false, unicastResponse: true);
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(MulticastAddress, MdnsPort));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                var message = DnsWire.Parse(result.Buffer, null, requireResponse: true);
                if (message.Error is not null) continue;
                var sourceIp = result.RemoteEndPoint.Address.ToString();

                var records = message.Answers.Concat(message.Additional).ToList();

                // SRV -> host target and port; PTR -> service instance names.
                foreach (var srv in records.Where(r => r.Type == DnsRecordType.SRV))
                {
                    var target = srv.Value.Split(' ').LastOrDefault()?.TrimEnd('.');
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    var builder = GetBuilder(hosts, target);
                    builder.Ports.Add(srv.Port);
                    builder.Services.Add(srv.Name);
                }

                foreach (var ptr in records.Where(r => r.Type == DnsRecordType.PTR))
                {
                    if (!services.TryGetValue(ptr.Name, out var set))
                        services[ptr.Name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(ptr.Value);
                }

                foreach (var a in records.Where(r => r.Type == DnsRecordType.A))
                {
                    if (!IPAddress.TryParse(a.Value, out _)) continue;
                    var builder = GetBuilder(hosts, a.Name);
                    builder.Addresses.Add(a.Value);
                }

                foreach (var aaaa in records.Where(r => r.Type == DnsRecordType.AAAA))
                {
                    var builder = GetBuilder(hosts, aaaa.Name);
                    builder.Addresses.Add(aaaa.Value);
                }

                // A response without usable A records: attribute the source address to the SRV target.
                if (!records.Any(r => r.Type is DnsRecordType.A or DnsRecordType.AAAA))
                {
                    var srv = records.FirstOrDefault(r => r.Type == DnsRecordType.SRV);
                    if (srv is not null)
                    {
                        var target = srv.Value.Split(' ').LastOrDefault()?.TrimEnd('.');
                        if (!string.IsNullOrWhiteSpace(target))
                            GetBuilder(hosts, target).Addresses.Add(sourceIp);
                    }
                }

                if (!observedAt.TryGetValue(sourceIp, out var hostNames))
                    observedAt[sourceIp] = hostNames = new List<string>();
                foreach (var srv in records.Where(r => r.Type == DnsRecordType.SRV))
                {
                    var target = srv.Value.Split(' ').LastOrDefault()?.TrimEnd('.');
                    if (!string.IsNullOrWhiteSpace(target) && !hostNames.Contains(target)) hostNames.Add(target);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<MdnsHost>();
        }

        var resultList = new List<MdnsHost>();
        foreach (var (name, builder) in hosts)
        {
            if (builder.Addresses.Count == 0) continue;
            resultList.Add(new MdnsHost
            {
                HostName = name,
                Addresses = builder.Addresses.Distinct().ToList(),
                Services = builder.Services.Concat(ServicesForHost(name, services)).Distinct().ToList(),
                Ports = builder.Ports.Distinct().ToList()
            });
        }

        return resultList;
    }

    private static IEnumerable<string> ServicesForHost(string host, Dictionary<string, HashSet<string>> services)
    {
        // Service instance names normally start with the host label ("Living Room TV._airplay._tcp.local").
        var label = host.Split('.')[0];
        return services.Values.SelectMany(v => v).Where(v => v.StartsWith(label, StringComparison.OrdinalIgnoreCase));
    }

    private static MdnsHostBuilder GetBuilder(Dictionary<string, MdnsHostBuilder> hosts, string name)
    {
        var key = name.TrimEnd('.');
        if (!hosts.TryGetValue(key, out var builder))
            hosts[key] = builder = new MdnsHostBuilder();
        return builder;
    }

    private sealed class MdnsHostBuilder
    {
        public HashSet<string> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> Ports { get; } = new();
    }
}
