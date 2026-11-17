using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IPManager.Network;

public sealed record DnsQueryResult
{
    public string QueryName { get; init; } = "";
    public string RecordType { get; init; } = "A";
    public string Server { get; init; } = "";
    public bool Success { get; init; }
    public int ResponseCode { get; init; }
    public IReadOnlyList<DnsAnswer> Answers { get; init; } = Array.Empty<DnsAnswer>();
    public double DurationMs { get; init; }
    public string? Error { get; init; }
    public DateTime Timestamp { get; init; }

    public string ResponseText => Answers.Count == 0
        ? (Error ?? "No records returned")
        : string.Join(Environment.NewLine, Answers.Select(a => $"{a.Type} {a.Name} TTL {a.Ttl} -> {Describe(a)}"));

    private static string Describe(DnsAnswer a) => a.Type switch
    {
        DnsRecordType.MX => $"preference {a.Preference} -> {a.Value}",
        DnsRecordType.SRV => $"priority {a.Priority} weight {a.Weight} port {a.Port} -> {a.Value}",
        _ => a.Value
    };
}

public interface IDnsResolver
{
    Task<DnsQueryResult> QueryAsync(string name, string recordType, string? server = null,
        int timeoutMs = 3000, CancellationToken ct = default);

    Task<DnsQueryResult> ReverseAsync(string ipAddress, string? server = null,
        int timeoutMs = 3000, CancellationToken ct = default);

    Task<string?> ResolveNameAsync(string ipAddress, int timeoutMs = 1500, CancellationToken ct = default);
}

public sealed class DnsResolver : IDnsResolver
{
    private readonly Func<IReadOnlyList<string>> _systemServers;

    public DnsResolver() : this(NetworkInterfaceService.GetSystemDnsServers) { }

    public DnsResolver(Func<IReadOnlyList<string>> systemServers)
    {
        _systemServers = systemServers;
    }

    public Task<DnsQueryResult> QueryAsync(string name, string recordType, string? server = null,
        int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (!Enum.TryParse<DnsRecordType>(recordType, true, out var type))
            type = DnsRecordType.A;
        return QueryAsyncInternal(name, type, server, timeoutMs, ct);
    }

    public Task<DnsQueryResult> ReverseAsync(string ipAddress, string? server = null,
        int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ipAddress.Trim(), out var ip))
        {
            return Task.FromResult(new DnsQueryResult
            {
                QueryName = ipAddress,
                RecordType = "PTR",
                Server = server ?? "system",
                Success = false,
                Error = "Not a valid IP address.",
                Timestamp = DateTime.UtcNow
            });
        }

        return QueryAsyncInternal(DnsWire.ReverseName(ip), DnsRecordType.PTR, server, timeoutMs, ct, displayName: ip.ToString());
    }

    public async Task<string?> ResolveNameAsync(string ipAddress, int timeoutMs = 1500, CancellationToken ct = default)
    {
        try
        {
            var task = Dns.GetHostEntryAsync(ipAddress, ct);
            var entry = await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            var name = entry.HostName;
            if (string.IsNullOrWhiteSpace(name) || name == ipAddress) return null;
            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<DnsQueryResult> QueryAsyncInternal(
        string name, DnsRecordType type, string? server, int timeoutMs, CancellationToken ct,
        string? displayName = null)
    {
        var sw = Stopwatch.StartNew();
        var queryName = displayName ?? name;
        var serverLabel = server ?? "system";

        try
        {
            var idn = new IdnMapping();
            var asciiName = type == DnsRecordType.PTR ? name.Trim() : idn.GetAscii(name.Trim().TrimEnd('.'));
            if (string.IsNullOrWhiteSpace(asciiName))
                return Fail(queryName, type, serverLabel, "Enter a DNS name to look up.", sw);

            var endpoint = await ResolveServerAsync(server, ct);
            if (endpoint is null)
                return Fail(queryName, type, serverLabel, "No DNS server is available. Configure a DNS server or select one manually.", sw);

            var id = (ushort)Random.Shared.Next(1, 65535);
            var query = DnsWire.BuildQuery(asciiName, type, id);
            var payload = await ExchangeUdpAsync(endpoint, query, timeoutMs, ct);

            if (payload is null)
                return Fail(queryName, type, serverLabel, $"No response from {endpoint} within {timeoutMs} ms.", sw);

            var message = DnsWire.Parse(payload, id);
            if (message.Error is not null)
                return Fail(queryName, type, serverLabel, message.Error, sw);

            if (message.Truncated)
            {
                var tcp = await ExchangeTcpAsync(endpoint, query, timeoutMs, ct);
                if (tcp is not null)
                {
                    var tcpMessage = DnsWire.Parse(tcp, id);
                    if (tcpMessage.Error is null) message = tcpMessage;
                }
            }

            sw.Stop();
            return new DnsQueryResult
            {
                QueryName = queryName,
                RecordType = type.ToString(),
                Server = endpoint.ToString(),
                Success = message.ResponseCode == 0,
                ResponseCode = message.ResponseCode,
                Answers = message.Answers,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Error = message.ResponseCode == 0 ? null : DnsWire.RcodeText(message.ResponseCode),
                Timestamp = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(queryName, type, serverLabel, "The DNS query timed out.", sw);
        }
        catch (Exception ex)
        {
            return Fail(queryName, type, serverLabel, ex.Message, sw);
        }
    }

    private async Task<IPEndPoint?> ResolveServerAsync(string? server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            var system = _systemServers().FirstOrDefault();
            if (system is null) return null;
            return IPAddress.TryParse(system, out var sysIp) ? new IPEndPoint(sysIp, 53) : null;
        }

        var text = server.Trim();
        if (text.Contains(':'))
        {
            var idx = text.LastIndexOf(':');
            if (int.TryParse(text[(idx + 1)..], out var port) && port is > 0 and <= 65535 && IPAddress.TryParse(text[..idx], out var ipWithPort))
                return new IPEndPoint(ipWithPort, port);
        }

        if (IPAddress.TryParse(text, out var ip))
            return new IPEndPoint(ip, 53);

        var addresses = await Dns.GetHostAddressesAsync(text, ct);
        var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        return v4 is null ? null : new IPEndPoint(v4, 53);
    }

    private static async Task<byte[]?> ExchangeUdpAsync(IPEndPoint endpoint, byte[] query, int timeoutMs, CancellationToken ct)
    {
        using var client = new UdpClient(endpoint.AddressFamily);
        client.Client.ReceiveTimeout = timeoutMs;
        await client.SendAsync(query, query.Length, endpoint);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            var result = await client.ReceiveAsync(cts.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ExchangeTcpAsync(IPEndPoint endpoint, byte[] query, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(endpoint.Address, endpoint.Port, cts.Token);
            await using var stream = client.GetStream();
            var length = new byte[2];
            length[0] = (byte)(query.Length >> 8);
            length[1] = (byte)(query.Length & 0xFF);
            await stream.WriteAsync(length, cts.Token);
            await stream.WriteAsync(query, cts.Token);

            var header = new byte[2];
            await stream.ReadExactlyAsync(header, cts.Token);
            var size = (header[0] << 8) | header[1];
            if (size <= 0 || size > 65535) return null;
            var response = new byte[size];
            await stream.ReadExactlyAsync(response, cts.Token);
            return response;
        }
        catch
        {
            return null;
        }
    }

    private static DnsQueryResult Fail(string name, DnsRecordType type, string server, string error, Stopwatch sw)
    {
        sw.Stop();
        return new DnsQueryResult
        {
            QueryName = name,
            RecordType = type.ToString(),
            Server = server,
            Success = false,
            Error = error,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            Timestamp = DateTime.UtcNow
        };
    }
}
