using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace IPManager.Network;

public enum DnsRecordType : ushort
{
    A = 1,
    NS = 2,
    CNAME = 5,
    SOA = 6,
    PTR = 12,
    MX = 15,
    TXT = 16,
    AAAA = 28,
    SRV = 33,
    ANY = 255
}

public sealed record DnsAnswer(string Name, DnsRecordType Type, uint Ttl, string Value, ushort Priority = 0, ushort Weight = 0, ushort Port = 0, ushort Preference = 0);

public sealed record DnsMessage(bool IsResponse, int ResponseCode, bool Truncated, IReadOnlyList<DnsAnswer> Answers, IReadOnlyList<DnsAnswer> Additional, string? Error)
{
    public static DnsMessage Failed(string error) => new(false, -1, false, Array.Empty<DnsAnswer>(), Array.Empty<DnsAnswer>(), error);
}

/// <summary>Minimal RFC 1035 wire-format encoder/decoder used for custom-server queries and mDNS.</summary>
public static class DnsWire
{
    public const int HeaderLength = 12;

    public static byte[] BuildQuery(string name, DnsRecordType type, ushort id = 0, bool recursionDesired = true, bool unicastResponse = false)
        => BuildQuery(new[] { (name, type) }, id, recursionDesired, unicastResponse);

    public static byte[] BuildQuery(IEnumerable<(string Name, DnsRecordType Type)> questions, ushort id = 0, bool recursionDesired = true, bool unicastResponse = false)
    {
        var list = questions.ToList();
        using var ms = new MemoryStream();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(recursionDesired ? 0x0100 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)list.Count);
        ms.Write(header);

        foreach (var (name, type) in list)
        {
            WriteName(ms, name);
            WriteU16(ms, (ushort)type);
            WriteU16(ms, (ushort)(unicastResponse ? 0x8001 : 0x0001));
        }
        return ms.ToArray();
    }

    public static void WriteName(Stream stream, string name)
    {
        var normalized = name.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(normalized))
        {
            stream.WriteByte(0);
            return;
        }

        foreach (var label in normalized.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new ArgumentException($"Invalid DNS label '{label}'.");
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static void WriteU16(Stream s, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        s.Write(b);
    }

    public static DnsMessage Parse(byte[] data, int? expectedId = null, bool requireResponse = true)
    {
        if (data.Length < HeaderLength) return DnsMessage.Failed("DNS response was too short.");

        var id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0));
        if (expectedId is not null && id != expectedId.Value)
            return DnsMessage.Failed("DNS response ID did not match the query.");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        var isResponse = (flags & 0x8000) != 0;
        if (requireResponse && !isResponse) return DnsMessage.Failed("Packet is not a DNS response.");
        var rcode = flags & 0x000F;
        var truncated = (flags & 0x0200) != 0;

        var qd = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        var an = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        var ns = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8));
        var ar = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));

        var offset = HeaderLength;
        for (var i = 0; i < qd; i++)
        {
            ReadName(data, ref offset);
            if (offset + 4 > data.Length) break;
            offset += 4;
        }

        var answers = ReadRecords(data, ref offset, an);
        for (var i = 0; i < ns; i++)
        {
            SkipRecord(data, ref offset);
        }
        var additional = ReadRecords(data, ref offset, ar);

        return new DnsMessage(isResponse, rcode, truncated, answers, additional, null);
    }

    private static void SkipRecord(byte[] data, ref int offset)
    {
        ReadName(data, ref offset);
        if (offset + 10 > data.Length) { offset = data.Length; return; }
        var rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
        offset += 10 + rdLength;
    }

    private static List<DnsAnswer> ReadRecords(byte[] data, ref int offset, int count)
    {
        var list = new List<DnsAnswer>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            if (offset >= data.Length) break;
            var name = ReadName(data, ref offset);
            if (offset + 10 > data.Length) break;

            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
            var rdOffset = offset + 10;
            if (rdOffset + rdLength > data.Length) break;

            DnsAnswer answer;
            try
            {
                answer = ParseRecord(name, type, ttl, data, rdOffset, rdLength);
            }
            catch
            {
                answer = new DnsAnswer(name, type, ttl, "(unparsed)");
            }

            list.Add(answer);
            offset = rdOffset + rdLength;
        }
        return list;
    }

    private static DnsAnswer ParseRecord(string name, DnsRecordType type, uint ttl, byte[] data, int rdOffset, int rdLength)
    {
        switch (type)
        {
            case DnsRecordType.A when rdLength == 4:
                return new DnsAnswer(name, type, ttl, new IPAddress(data.AsSpan(rdOffset, 4)).ToString());

            case DnsRecordType.AAAA when rdLength == 16:
                return new DnsAnswer(name, type, ttl, new IPAddress(data.AsSpan(rdOffset, 16)).ToString());

            case DnsRecordType.CNAME:
            case DnsRecordType.NS:
            case DnsRecordType.PTR:
            {
                var pos = rdOffset;
                return new DnsAnswer(name, type, ttl, ReadName(data, ref pos));
            }

            case DnsRecordType.MX:
            {
                var pref = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rdOffset));
                var pos = rdOffset + 2;
                return new DnsAnswer(name, type, ttl, $"{pref} {ReadName(data, ref pos)}", Preference: pref);
            }

            case DnsRecordType.TXT:
            {
                var pos = rdOffset;
                var end = rdOffset + rdLength;
                var parts = new List<string>();
                while (pos < end)
                {
                    var len = data[pos++];
                    if (pos + len > end) break;
                    parts.Add(Encoding.UTF8.GetString(data, pos, len));
                    pos += len;
                }
                return new DnsAnswer(name, type, ttl, string.Join("", parts));
            }

            case DnsRecordType.SRV when rdLength >= 6:
            {
                var priority = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rdOffset));
                var weight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rdOffset + 2));
                var port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rdOffset + 4));
                var pos = rdOffset + 6;
                var target = ReadName(data, ref pos);
                return new DnsAnswer(name, type, ttl, $"{priority} {weight} {port} {target}", priority, weight, port);
            }

            case DnsRecordType.SOA:
            {
                var pos = rdOffset;
                var mname = ReadName(data, ref pos);
                var rname = ReadName(data, ref pos);
                return new DnsAnswer(name, type, ttl, $"{mname} {rname}");
            }

            default:
                return new DnsAnswer(name, type, ttl, $"({rdLength} bytes)");
        }
    }

    public static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        var pos = offset;
        var jumped = false;
        var safety = 0;

        while (pos >= 0 && pos < data.Length)
        {
            if (++safety > 128) break;
            var len = data[pos];
            if (len == 0)
            {
                pos++;
                if (!jumped) offset = pos;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= data.Length) break;
                var pointer = ((len & 0x3F) << 8) | data[pos + 1];
                if (!jumped) offset = pos + 2;
                jumped = true;
                if (pointer >= pos) break; // pointers must point backwards; avoids loops
                pos = pointer;
                continue;
            }

            if (pos + 1 + len > data.Length) break;
            labels.Add(Encoding.ASCII.GetString(data, pos + 1, len));
            pos += 1 + len;
            if (!jumped) offset = pos;
        }

        return string.Join('.', labels);
    }

    public static string ReverseName(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        }

        var bytes = address.GetAddressBytes();
        var sb = new StringBuilder();
        for (var i = bytes.Length - 1; i >= 0; i--) sb.Append($"{bytes[i]:x}.{bytes[i] >> 4:x}.");
        sb.Append("ip6.arpa");
        return sb.ToString();
    }

    public static string RcodeText(int rcode) => rcode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN (name does not exist)",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE {rcode}"
    };
}
