using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IPManager.Core;

namespace IPManager.Network;

public sealed record NetworkInterfaceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? MacAddress { get; init; }
    public string? Ipv4Address { get; init; }
    public string? Ipv6Address { get; init; }
    public string? SubnetMask { get; init; }
    public int? PrefixLength { get; init; }
    public string? Gateway { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();
    public long? SpeedMbps { get; init; }
    public bool IsUp { get; init; }
    public InterfaceKind Kind { get; init; }
    public bool HasUsableIpv4 => !string.IsNullOrWhiteSpace(Ipv4Address) && PrefixLength is not null;

    public string? SuggestedCidr
    {
        get
        {
            if (!Core.Ipv4Address.TryParse(Ipv4Address, out var ip) || PrefixLength is null) return null;
            return new Ipv4Network(ip, PrefixLength.Value).Cidr;
        }
    }

    public string Display => $"{Name} ({Ipv4Address ?? "no IPv4"}{(PrefixLength is null ? "" : $"/{PrefixLength}")})";
}

public static class NetworkInterfaceService
{
    public static IReadOnlyList<NetworkInterfaceInfo> GetActive()
    {
        var list = new List<NetworkInterfaceInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch { continue; }

            var ipv4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address));
            var ipv6 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                                                   !a.Address.IsIPv6LinkLocal &&
                                                                   !IPAddress.IsLoopback(a.Address));
            var gateway = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                .Select(g => g.Address.ToString())
                .FirstOrDefault();
            var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();

            int? prefix = null;
            string? mask = null;
            if (ipv4 is not null && ipv4.PrefixLength is > 0 and <= 32)
            {
                prefix = ipv4.PrefixLength;
                mask = ipv4.IPv4Mask?.ToString();
            }

            list.Add(new NetworkInterfaceInfo
            {
                Id = ni.Id,
                Name = ni.Name,
                Description = ni.Description,
                MacAddress = MacUtil.Normalize(ni.GetPhysicalAddress()?.ToString()),
                Ipv4Address = ipv4?.Address.ToString(),
                Ipv6Address = ipv6?.Address.ToString(),
                SubnetMask = mask,
                PrefixLength = prefix,
                Gateway = gateway,
                DnsServers = dns,
                SpeedMbps = ni.Speed > 0 ? ni.Speed / 1_000_000 : null,
                IsUp = ni.OperationalStatus == OperationalStatus.Up,
                Kind = Classify(ni.NetworkInterfaceType, ni.Name, ni.Description)
            });
        }

        return list.OrderByDescending(x => x.IsUp).ThenBy(x => x.Kind).ThenBy(x => x.Name).ToList();
    }

    public static IReadOnlyList<string> GetSystemDnsServers()
    {
        var servers = new List<string>();
        foreach (var ni in GetActive().Where(x => x.IsUp))
            servers.AddRange(ni.DnsServers);
        return servers.Distinct().ToList();
    }

    public static NetworkInterfaceInfo? FindByAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        return GetActive().FirstOrDefault(x => string.Equals(x.Ipv4Address, address, StringComparison.OrdinalIgnoreCase));
    }

    private static InterfaceKind Classify(NetworkInterfaceType type, string name, string? description)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        if (text.Contains("vpn") || text.Contains("tunnel") || text.Contains("tap-") || text.Contains("wintun") ||
            text.Contains("virtual") || text.Contains("vethernet") || text.Contains("hyper-v") ||
            text.Contains("vmware") || text.Contains("virtualbox") || text.Contains("loopback"))
            return InterfaceKind.Virtual;

        return type switch
        {
            NetworkInterfaceType.Wireless80211 => InterfaceKind.WiFi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => InterfaceKind.Ethernet,
            _ => InterfaceKind.Other
        };
    }
}
