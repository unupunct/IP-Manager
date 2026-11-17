using System.Diagnostics;
using System.Text.RegularExpressions;
using IPManager.Core;

namespace IPManager.Network;

public sealed record ArpEntry(string IpAddress, string MacAddress, bool IsStatic, string InterfaceAddress);

/// <summary>
/// Reads the Windows ARP/neighbor cache. ARP entries can be stale; callers must not treat
/// an entry alone as proof that a device is currently online.
/// </summary>
public static partial class ArpTableReader
{
    [GeneratedRegex(@"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>(?:[0-9a-fA-F]{2}[-:]){5}[0-9a-fA-F]{2})\s+(?<type>\S+)", RegexOptions.Multiline)]
    private static partial Regex ArpLineRegex();

    [GeneratedRegex(@"Interface:\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})")]
    private static partial Regex InterfaceRegex();

    public static async Task<IReadOnlyList<ArpEntry>> ReadAsync(string? preferredInterfaceIp = null, int timeoutMs = 4000, CancellationToken ct = default)
    {
        string output;
        try
        {
            output = await RunArpAsync(timeoutMs, ct);
        }
        catch
        {
            return Array.Empty<ArpEntry>();
        }

        return Parse(output, preferredInterfaceIp);
    }

    public static IReadOnlyList<ArpEntry> Parse(string output, string? preferredInterfaceIp = null)
    {
        var entries = new List<ArpEntry>();
        var currentInterface = "";
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var ifMatch = InterfaceRegex().Match(line);
            if (ifMatch.Success)
            {
                currentInterface = ifMatch.Groups["ip"].Value;
                continue;
            }

            var m = ArpLineRegex().Match(line);
            if (!m.Success) continue;

            var mac = MacUtil.Normalize(m.Groups["mac"].Value);
            if (mac is null) continue;
            var ip = m.Groups["ip"].Value;
            if (ip.EndsWith(".255") || ip == "255.255.255.255" || ip.StartsWith("224.") || ip.StartsWith("239.")) continue;
            if (mac == "FF:FF:FF:FF:FF:FF") continue;

            var type = m.Groups["type"].Value;
            entries.Add(new ArpEntry(ip, mac, type.Contains("static", StringComparison.OrdinalIgnoreCase), currentInterface));
        }

        if (!string.IsNullOrWhiteSpace(preferredInterfaceIp))
        {
            var matching = entries.Where(e => e.InterfaceAddress == preferredInterfaceIp).ToList();
            if (matching.Count > 0) return matching;
        }

        return entries.GroupBy(e => e.IpAddress).Select(g => g.First()).ToList();
    }

    private static async Task<string> RunArpAsync(int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "arp.exe",
            Arguments = "-a",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start arp.exe.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return await outputTask;
    }
}
