using System.Text;

namespace IPManager.Core;

public sealed record SubnetCalculation
{
    public required string InputAddress { get; init; }
    public required Ipv4Network Network { get; init; }
    public string NetworkAddress => Network.Network.ToString();
    public string BroadcastAddress => Network.Broadcast.ToString();
    public string SubnetMask => Network.MaskText;
    public string WildcardMask => Network.WildcardText;
    public string FirstUsable => Network.FirstUsable.ToString();
    public string LastUsable => Network.LastUsable.ToString();
    public long TotalAddresses => Network.TotalAddresses;
    public long UsableHosts => Network.UsableHosts;
    public string LegacyClass => Network.LegacyClass;
    public string BinaryAddress => Network.Network.ToBinaryString();
    public string BinaryMask => Network.Mask.ToBinaryString();
    public string Range => Network.RangeText;
    public string Cidr => Network.Cidr;
    public string Notes { get; init; } = "";
}

public sealed record SubnetDivisionRow
{
    public int Index { get; init; }
    public string Network { get; init; } = "";
    public int PrefixLength { get; init; }
    public string Cidr { get; init; } = "";
    public string Mask { get; init; } = "";
    public string FirstUsable { get; init; } = "";
    public string LastUsable { get; init; } = "";
    public string Broadcast { get; init; } = "";
    public long UsableHosts { get; init; }
    public string Range { get; init; } = "";
}

public static class SubnetCalculator
{
    public static SubnetCalculation Calculate(string address, int prefixLength)
    {
        var ip = Ipv4Address.Parse(address);
        if (prefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), "Prefix length must be 0-32.");

        var network = new Ipv4Network(ip, prefixLength);
        var notes = prefixLength switch
        {
            31 => "RFC 3021 point-to-point link: both addresses are usable; no broadcast address is reserved.",
            32 => "Single host route: one usable address (/32).",
            30 => "Four addresses: two usable hosts, one network and one broadcast address.",
            0 => "Default route (0.0.0.0/0) - represents the entire IPv4 address space.",
            _ => ""
        };

        return new SubnetCalculation
        {
            InputAddress = ip.ToString(),
            Network = network,
            Notes = notes
        };
    }

    public static (IReadOnlyList<SubnetDivisionRow> Rows, int ActualCount, int NewPrefix) Divide(Ipv4Network network, int requested)
    {
        if (requested < 1)
            throw new ArgumentOutOfRangeException(nameof(requested), "The number of subnets must be at least 1.");

        var actual = Ipv4Network.NormalizeDivisionCount(requested);
        var parts = network.Divide(actual);
        var rows = new List<SubnetDivisionRow>(parts.Count);
        var i = 1;
        foreach (var part in parts)
        {
            rows.Add(new SubnetDivisionRow
            {
                Index = i++,
                Network = part.Network.ToString(),
                PrefixLength = part.PrefixLength,
                Cidr = part.Cidr,
                Mask = part.MaskText,
                FirstUsable = part.FirstUsable.ToString(),
                LastUsable = part.LastUsable.ToString(),
                Broadcast = part.Broadcast.ToString(),
                UsableHosts = part.UsableHosts,
                Range = part.RangeText
            });
        }

        return (rows, actual, parts.Count > 0 ? parts[0].PrefixLength : network.PrefixLength);
    }

    /// <summary>Validates a host assignment and explains why it is invalid.</summary>
    public static bool ValidateHostAssignment(string address, Ipv4Network network, out string error)
    {
        if (!Ipv4Address.TryParse(address, out var ip))
        {
            error = "The address is not a valid IPv4 address.";
            return false;
        }

        if (!network.Contains(ip))
        {
            error = $"{ip} is outside {network.Cidr}.";
            return false;
        }

        if (network.PrefixLength <= 30)
        {
            if (ip == network.Network)
            {
                error = $"{ip} is the network address and cannot be assigned to a host.";
                return false;
            }
            if (ip == network.Broadcast)
            {
                error = $"{ip} is the broadcast address and cannot be assigned to a host.";
                return false;
            }
        }

        error = "";
        return true;
    }
}

public static class Ipv6Utils
{
    public static bool TryParse(string? text, out System.Net.IPAddress address, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            address = System.Net.IPAddress.None;
            error = "Enter an IPv6 address.";
            return false;
        }

        var s = text.Trim();
        if (s.Contains('/'))
        {
            error = "Enter the address without a prefix; set the prefix length separately.";
            address = System.Net.IPAddress.None;
            return false;
        }

        if (!System.Net.IPAddress.TryParse(s, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            address = System.Net.IPAddress.None;
            error = "Not a valid IPv6 address.";
            return false;
        }

        address = parsed;
        return true;
    }

    public static string NetworkAddress(System.Net.IPAddress address, int prefixLength)
    {
        if (prefixLength is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < 16; i++)
        {
            var bitsInByte = Math.Clamp(prefixLength - i * 8, 0, 8);
            var mask = bitsInByte == 0 ? (byte)0 : (byte)(0xFF << (8 - bitsInByte));
            bytes[i] &= mask;
        }
        return new System.Net.IPAddress(bytes).ToString();
    }

    public static string Compress(System.Net.IPAddress address) => address.ToString();

    public static string TotalAddresses(int prefixLength)
    {
        if (prefixLength is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        var power = System.Numerics.BigInteger.Pow(2, 128 - prefixLength);
        return power.ToString("N0");
    }

    /// <summary>Usable host count: /127 and /128 follow IPv4 /31 and /32 semantics.</summary>
    public static string UsableHosts(int prefixLength)
    {
        if (prefixLength == 128) return "1";
        if (prefixLength == 127) return "2";
        var power = System.Numerics.BigInteger.Pow(2, 128 - prefixLength) - 2;
        return power < 0 ? "0" : power.ToString("N0");
    }

    public static IReadOnlyList<string> Divide(System.Net.IPAddress address, int prefixLength, int newPrefix, int max = 256)
    {
        if (newPrefix < prefixLength || newPrefix > 128)
            throw new ArgumentException("The new prefix must be longer than the original prefix and at most 128.");

        var count = System.Numerics.BigInteger.Pow(2, newPrefix - prefixLength);
        if (count > max)
            throw new ArgumentException($"Dividing /{prefixLength} into /{newPrefix} produces {count:N0} subnets; the display limit is {max}.");

        var baseNetwork = System.Net.IPAddress.Parse(NetworkAddress(address, prefixLength));
        var baseBytes = baseNetwork.GetAddressBytes();
        var list = new List<string>();
        for (var i = 0; i < (int)count; i++)
        {
            var bytes = (byte[])baseBytes.Clone();
            var increment = (System.Numerics.BigInteger)i << (128 - newPrefix);
            var incBytes = increment.ToByteArray(isUnsigned: true, isBigEndian: true);
            var offset = 16 - incBytes.Length;
            for (var b = 0; b < incBytes.Length; b++)
            {
                var idx = offset + b;
                var sum = bytes[idx] + incBytes[b];
                bytes[idx] = (byte)sum;
                var carry = sum >> 8;
                var j = idx - 1;
                while (carry > 0 && j >= 0)
                {
                    var s2 = bytes[j] + carry;
                    bytes[j] = (byte)s2;
                    carry = s2 >> 8;
                    j--;
                }
            }
            list.Add($"{new System.Net.IPAddress(bytes)}/{newPrefix}");
        }
        return list;
    }

    public static string ToBinaryString(System.Net.IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var sb = new StringBuilder();
        for (var i = 0; i < 16; i++)
        {
            sb.Append(Convert.ToString(bytes[i], 2).PadLeft(8, '0'));
            if (i % 2 == 1 && i != 15) sb.Append(':');
        }
        return sb.ToString();
    }

    public static string AddressType(System.Net.IPAddress address)
    {
        if (address.IsIPv6LinkLocal) return "Link-local (fe80::/10)";
        if (address.IsIPv6SiteLocal) return "Site-local";
        if (address.IsIPv6Multicast) return "Multicast";
        if (address.Equals(System.Net.IPAddress.IPv6Loopback)) return "Loopback";
        var b = address.GetAddressBytes();
        if (b[0] == 0xFC || b[0] == 0xFD) return "Unique local (fc00::/7)";
        if (b[0] == 0x20 && b[1] == 0x01) return "Global unicast (2001::/16)";
        return "Global / other unicast";
    }
}
