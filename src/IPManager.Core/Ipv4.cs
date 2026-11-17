using System.Globalization;

namespace IPManager.Core;

/// <summary>Immutable IPv4 address stored as a big-endian 32-bit unsigned value.</summary>
public readonly record struct Ipv4Address(uint Value) : IComparable<Ipv4Address>
{
    public static readonly Ipv4Address None = new(0);
    public static readonly Ipv4Address Broadcast = new(uint.MaxValue);

    public byte A => (byte)(Value >> 24);
    public byte B => (byte)(Value >> 16);
    public byte C => (byte)(Value >> 8);
    public byte D => (byte)Value;

    public static Ipv4Address Parse(string? text)
    {
        if (!TryParse(text, out var addr))
            throw new FormatException($"'{text}' is not a valid IPv4 address.");
        return addr;
    }

    public static bool TryParse(string? text, out Ipv4Address address)
    {
        address = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        uint value = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3) return false;
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var b)) return false;
            value = (value << 8) | b;
        }
        address = new Ipv4Address(value);
        return true;
    }

    public static Ipv4Address FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4) throw new ArgumentException("IPv4 requires 4 bytes.", nameof(bytes));
        return new Ipv4Address(((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3]);
    }

    public byte[] ToBytes() => new[] { A, B, C, D };

    public bool IsPrivate => IsPrivateAddress(Value);

    public static bool IsPrivateAddress(uint v) =>
        (v & 0xFF000000) == 0x0A000000 ||          // 10.0.0.0/8
        (v & 0xFFF00000) == 0xAC100000 ||          // 172.16.0.0/12
        (v & 0xFFFF0000) == 0xC0A80000 ||          // 192.168.0.0/16
        (v & 0xFFC00000) == 0x64400000 ||          // 100.64.0.0/10 (CGNAT)
        (v & 0xFFFF0000) == 0xA9FE0000 ||          // 169.254.0.0/16 (link-local)
        (v & 0xFF000000) == 0x7F000000;            // loopback

    public bool IsMulticast => (Value & 0xF0000000) == 0xE0000000;
    public bool IsLinkLocal => (Value & 0xFFFF0000) == 0xA9FE0000;
    public bool IsLoopback => A == 127;

    public static Ipv4Address operator +(Ipv4Address a, uint n) => new(unchecked(a.Value + n));
    public static Ipv4Address operator -(Ipv4Address a, uint n) => new(unchecked(a.Value - n));

    public int CompareTo(Ipv4Address other) => Value.CompareTo(other.Value);
    public override string ToString() => $"{A}.{B}.{C}.{D}";
    public string ToBinaryString() =>
        string.Join('.', Convert.ToString(A, 2).PadLeft(8, '0'), Convert.ToString(B, 2).PadLeft(8, '0'),
            Convert.ToString(C, 2).PadLeft(8, '0'), Convert.ToString(D, 2).PadLeft(8, '0'));
}

/// <summary>IPv4 network (CIDR). All calculations use 32-bit unsigned arithmetic.</summary>
public readonly struct Ipv4Network : IEquatable<Ipv4Network>
{
    public Ipv4Address Network { get; }
    public int PrefixLength { get; }
    public Ipv4Address Mask { get; }
    public Ipv4Address Broadcast { get; }
    public Ipv4Address Wildcard { get; }

    public Ipv4Network(Ipv4Address network, int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), "Prefix must be between 0 and 32.");
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var masked = network.Value & mask;
        Network = new Ipv4Address(masked);
        PrefixLength = prefixLength;
        Mask = new Ipv4Address(mask);
        Broadcast = new Ipv4Address(masked | ~mask);
        Wildcard = new Ipv4Address(~mask);
    }

    public static Ipv4Network Parse(string network, int prefixLength) => new(Ipv4Address.Parse(network), prefixLength);

    public static bool TryParseCidr(string? cidr, out Ipv4Network network, out string error)
    {
        network = default;
        error = "";
        if (string.IsNullOrWhiteSpace(cidr))
        {
            error = "Enter an address in CIDR form (for example 192.168.1.0/24).";
            return false;
        }

        var s = cidr.Trim();
        var slash = s.LastIndexOf('/');
        if (slash < 0)
        {
            error = "CIDR notation requires a prefix length, for example 192.168.1.0/24.";
            return false;
        }

        var addressPart = s[..slash].Trim();
        var prefixPart = s[(slash + 1)..].Trim();
        if (!Ipv4Address.TryParse(addressPart, out var address))
        {
            error = $"'{addressPart}' is not a valid IPv4 address.";
            return false;
        }

        if (!int.TryParse(prefixPart, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 0 or > 32)
        {
            error = $"'{prefixPart}' is not a valid prefix length (0-32).";
            return false;
        }

        network = new Ipv4Network(address, prefix);
        return true;
    }

    /// <summary>Total addresses including network and broadcast.</summary>
    public long TotalAddresses => 1L << (32 - PrefixLength);

    /// <summary>Host addresses per RFC 3021 (/31 point-to-point) and /32 host route.</summary>
    public long UsableHosts => PrefixLength switch
    {
        32 => 1,
        31 => 2,
        _ => TotalAddresses - 2
    };

    public Ipv4Address FirstUsable => PrefixLength >= 31 ? Network : Network + 1;
    public Ipv4Address LastUsable => PrefixLength switch
    {
        32 => Network,
        31 => Broadcast,
        _ => Broadcast - 1
    };

    public bool Contains(Ipv4Address address) => (address.Value & Mask.Value) == Network.Value;

    public bool Contains(string address) => Ipv4Address.TryParse(address, out var a) && Contains(a);

    public bool Overlaps(Ipv4Network other)
    {
        int shorter = Math.Min(PrefixLength, other.PrefixLength);
        uint mask = shorter == 0 ? 0u : uint.MaxValue << (32 - shorter);
        return (Network.Value & mask) == (other.Network.Value & mask);
    }

    public IEnumerable<Ipv4Address> Hosts(long max = 65536)
    {
        var count = Math.Min(UsableHosts, max);
        if (PrefixLength >= 31)
        {
            yield return Network;
            if (PrefixLength == 31) yield return Broadcast;
            yield break;
        }
        for (long i = 1; i <= count; i++)
            yield return new Ipv4Address(Network.Value + (uint)i);
    }

    /// <summary>Number of subnets that an equal division would actually produce for a (non power of two) request.</summary>
    public static int NormalizeDivisionCount(int requested)
    {
        if (requested <= 1) return 1;
        var bits = 0;
        var n = 1;
        while (n < requested)
        {
            n <<= 1;
            bits++;
        }
        return n;
    }

    /// <summary>Divide into equal subnets. The count is rounded up to the next power of two.</summary>
    public IReadOnlyList<Ipv4Network> Divide(int requested)
    {
        if (requested < 1) throw new ArgumentOutOfRangeException(nameof(requested));
        if (requested == 1) return new[] { this };

        var count = NormalizeDivisionCount(requested);
        var bits = (int)Math.Log2(count);
        var newPrefix = PrefixLength + bits;
        if (newPrefix > 32)
            throw new InvalidOperationException($"A /{PrefixLength} network cannot be divided into {count} subnets.");

        var step = 1u << (32 - newPrefix);
        var list = new List<Ipv4Network>(count);
        for (var i = 0; i < count; i++)
            list.Add(new Ipv4Network(new Ipv4Address(Network.Value + (uint)i * step), newPrefix));
        return list;
    }

    public string Cidr => $"{Network}/{PrefixLength}";
    public string WildcardText => Wildcard.ToString();
    public string MaskText => Mask.ToString();

    /// <summary>Informational legacy class based on the first octet.</summary>
    public string LegacyClass
    {
        get
        {
            var first = Network.A;
            if (first == 0) return "This network (reserved)";
            if (first < 128) return "Class A";
            if (first < 192) return "Class B";
            if (first < 224) return "Class C";
            if (first < 240) return "Class D (multicast)";
            return "Class E (reserved)";
        }
    }

    public string RangeText => $"{Network} - {Broadcast}";

    public bool Equals(Ipv4Network other) => Network == other.Network && PrefixLength == other.PrefixLength;
    public override bool Equals(object? obj) => obj is Ipv4Network other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Network, PrefixLength);
    public override string ToString() => Cidr;

    /// <summary>Returns true when the network is within the private, loopback or link-local space.</summary>
    public bool IsLocalOrPrivate => Network.IsPrivate || Network.IsLinkLocal;

    public static IReadOnlyList<(string Label, Ipv4Address Network, int Prefix)> PrivateRanges { get; } = new[]
    {
        ("RFC1918 10/8", new Ipv4Address(0x0A000000), 8),
        ("RFC1918 172.16/12", new Ipv4Address(0xAC100000), 12),
        ("RFC1918 192.168/16", new Ipv4Address(0xC0A80000), 16),
        ("CGNAT 100.64/10", new Ipv4Address(0x64400000), 10),
        ("Link-local 169.254/16", new Ipv4Address(0xA9FE0000), 16),
        ("Loopback 127/8", new Ipv4Address(0x7F000000), 8)
    };
}
