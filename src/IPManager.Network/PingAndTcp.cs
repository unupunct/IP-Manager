using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IPManager.Network;

public sealed record PingResult(bool Success, long? RoundtripMs, string Status, string? Error = null);

public static class PingService
{
    private static async Task<IPAddress?> ResolveTargetAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static async Task<PingResult> PingAsync(string host, int timeoutMs = 1000, int retries = 0,
        int ttl = 128, CancellationToken ct = default)
    {
        var attempts = Math.Max(0, retries) + 1;
        PingResult last = new(false, null, "No response", "No attempt was made");
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            last = await PingOnceAsync(host, timeoutMs, ttl, ct);
            if (last.Success) return last;
        }
        return last;
    }

    public static async Task<PingResult> PingOnceAsync(string host, int timeoutMs, int ttl, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var buffer = new byte[32];
            Random.Shared.NextBytes(buffer);
            var options = new PingOptions(ttl, dontFragment: true);
            var target = await ResolveTargetAsync(host, ct);
            if (target is null) return new PingResult(false, null, "Unresolved", $"Unable to resolve '{host}'.");
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), buffer, options, ct);
            return reply.Status switch
            {
                IPStatus.Success => new PingResult(true, reply.RoundtripTime, "Success"),
                IPStatus.TtlExpired => new PingResult(false, reply.RoundtripTime, "TTL expired", "TTL expired"),
                IPStatus.TimedOut => new PingResult(false, null, "Timed out", "Timed out"),
                IPStatus.DestinationHostUnreachable => new PingResult(false, null, "Host unreachable", "Destination host unreachable"),
                _ => new PingResult(false, reply.RoundtripTime, reply.Status.ToString(), reply.Status.ToString())
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PingException ex)
        {
            return new PingResult(false, null, "Error", ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return new PingResult(false, null, "Error", ex.Message);
        }
    }

    /// <summary>Resolves an ICMP echo reply address for traceroute TTL handling.</summary>
    public static async Task<(PingResult Result, string? Responder)> TraceHopAsync(string host, int ttl, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var options = new PingOptions(ttl, dontFragment: true);
            var target = await ResolveTargetAsync(host, ct);
            if (target is null) return (new PingResult(false, null, "Unresolved", $"Unable to resolve '{host}'."), null);
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), new byte[32], options, ct);
            if (reply.Status is IPStatus.Success or IPStatus.TtlExpired)
                return (new PingResult(true, reply.RoundtripTime, reply.Status.ToString()), reply.Address?.ToString());
            return (new PingResult(false, null, reply.Status.ToString()), reply.Address?.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (new PingResult(false, null, "Error", ex.Message), null);
        }
    }
}

public sealed record TcpProbeResult(bool Open, long? ConnectMs, string Status, string? Error = null);

public static class TcpProbeService
{
    public static async Task<TcpProbeResult> ProbeAsync(string host, int port, int timeoutMs = 1500, CancellationToken ct = default)
    {
        if (port is < 1 or > 65535) return new TcpProbeResult(false, null, "Invalid port", "Port must be 1-65535.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return new TcpProbeResult(true, sw.ElapsedMilliseconds, "Open");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TcpProbeResult(false, null, "Timed out");
        }
        catch (SocketException ex)
        {
            var status = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "Closed (refused)",
                SocketError.TimedOut => "Timed out",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => "Unreachable",
                _ => ex.SocketErrorCode.ToString()
            };
            return new TcpProbeResult(false, null, status, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TcpProbeResult(false, null, "Error", ex.Message);
        }
    }
}
