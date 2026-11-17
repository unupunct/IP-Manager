using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IPManager.Network;

public sealed record NetBiosResult(bool Success, string? Name, string? Workgroup, string? MacAddress, string? Error)
{
    public static NetBiosResult Failed(string error) => new(false, null, null, null, error);
}

/// <summary>NetBIOS node status (NBSTAT) query over UDP 137.</summary>
public static class NetBiosResolver
{
    public static byte[] BuildQuery(ushort transactionId = 0x4E42)
    {
        var packet = new byte[50];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), transactionId);
        // flags 0, QDCOUNT 1
        packet[5] = 1;

        // Encoded wildcard name "*" padded with zeros to 16 bytes.
        packet[12] = 0x20;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var nameBytes = new byte[16];
        nameBytes[0] = (byte)'*';
        for (var i = 0; i < 16; i++)
        {
            packet[13 + i * 2] = (byte)alphabet[nameBytes[i] >> 4];
            packet[14 + i * 2] = (byte)alphabet[nameBytes[i] & 0x0F];
        }
        packet[45] = 0x00;
        packet[46] = 0x00;
        packet[47] = 0x21; // NBSTAT
        packet[48] = 0x00;
        packet[49] = 0x01; // IN
        return packet;
    }

    public static async Task<NetBiosResult> QueryAsync(string ipAddress, int timeoutMs = 800, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return NetBiosResult.Failed("Not an IPv4 address.");

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var query = BuildQuery();
            await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 137));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            var result = await udp.ReceiveAsync(cts.Token);
            return Parse(result.Buffer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return NetBiosResult.Failed("No NetBIOS response (timeout).");
        }
        catch (SocketException ex)
        {
            return NetBiosResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            return NetBiosResult.Failed(ex.Message);
        }
    }

    public static NetBiosResult Parse(byte[] data)
    {
        if (data.Length < 12) return NetBiosResult.Failed("Response too short.");

        var offset = 12;
        // Skip the answer name (compression pointer or label sequence).
        if (offset >= data.Length) return NetBiosResult.Failed("Malformed response.");
        if ((data[offset] & 0xC0) == 0xC0) offset += 2;
        else offset += 1 + data[offset];
        if (offset + 10 > data.Length) return NetBiosResult.Failed("Malformed response header.");

        var type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        if (type != 0x0021) return NetBiosResult.Failed("Unexpected record type.");
        offset += 2;
        offset += 2; // class
        offset += 4; // ttl
        var rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        offset += 2;

        if (offset >= data.Length) return NetBiosResult.Failed("Missing NBSTAT payload.");
        var nameCount = data[offset++];
        var payloadEnd = offset + nameCount * 18;
        if (payloadEnd > data.Length || payloadEnd > offset + rdLength - 7)
            return NetBiosResult.Failed("Malformed NBSTAT name table.");

        string? name = null;
        string? workgroup = null;
        for (var i = 0; i < nameCount; i++)
        {
            var rawName = Encoding.ASCII.GetString(data, offset, 15).TrimEnd('\0', ' ');
            var suffix = data[offset + 15];
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 16));
            var isGroup = (flags & 0x8000) != 0;
            offset += 18;

            if (suffix != 0x00 || rawName.Length == 0 || rawName.StartsWith('_')) continue;
            if (rawName is "MSBROWSE" or "WORKGROUP" or "HOME") { }
            if (isGroup)
            {
                workgroup ??= rawName;
            }
            else
            {
                name ??= rawName;
            }
        }

        string? mac = null;
        if (offset + 6 <= data.Length)
            mac = string.Join(':', data.Skip(offset).Take(6).Select(b => b.ToString("X2")));

        return new NetBiosResult(true, name, workgroup, mac, null);
    }
}
