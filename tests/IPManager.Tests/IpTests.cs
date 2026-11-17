using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPManager.Tests;

public sealed class Ipv4Tests
{
    [Theory]
    [InlineData("192.168.1.10", 0xC0A8010A)]
    [InlineData("0.0.0.0", 0u)]
    [InlineData("255.255.255.255", 0xFFFFFFFFu)]
    [InlineData("10.0.0.1", 0x0A000001u)]
    public void TryParse_ValidAddresses(string text, uint expected)
    {
        Assert.True(Ipv4Address.TryParse(text, out var address));
        Assert.Equal(expected, address.Value);
        Assert.Equal(text, address.ToString());
    }

    [Theory]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("a.b.c.d")]
    [InlineData("")]
    [InlineData(" 1.2.3.4 ")]
    public void TryParse_RejectsInvalid(string text)
    {
        if (text == " 1.2.3.4 ")
        {
            Assert.True(Ipv4Address.TryParse(text, out var trimmed));
            Assert.Equal("1.2.3.4", trimmed.ToString());
            return;
        }
        Assert.False(Ipv4Address.TryParse(text, out _));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void IsPrivate_ClassifiesLocalRanges(string text, bool expected)
    {
        Assert.Equal(expected, Ipv4Address.Parse(text).IsPrivate);
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0", "192.168.1.255", "255.255.255.0", 254)]
    [InlineData("10.0.0.5/8", "10.0.0.0", "10.255.255.255", "255.0.0.0", 16777214)]
    [InlineData("172.16.4.9/12", "172.16.0.0", "172.31.255.255", "255.240.0.0", 1048574)]
    [InlineData("192.168.1.1/30", "192.168.1.0", "192.168.1.3", "255.255.255.252", 2)]
    [InlineData("192.168.1.0/31", "192.168.1.0", "192.168.1.1", "255.255.255.254", 2)]
    [InlineData("192.168.1.7/32", "192.168.1.7", "192.168.1.7", "255.255.255.255", 1)]
    public void Network_ComputesBoundaries(string cidr, string network, string broadcast, string mask, long usable)
    {
        var n = Ipv4Network.Parse(cidr.Split('/')[0], int.Parse(cidr.Split('/')[1]));
        Assert.Equal(network, n.Network.ToString());
        Assert.Equal(broadcast, n.Broadcast.ToString());
        Assert.Equal(mask, n.MaskText);
        Assert.Equal(usable, n.UsableHosts);
    }

    [Fact]
    public void Network_SlashZeroAndSlashOne()
    {
        var zero = new Ipv4Network(Ipv4Address.Parse("1.2.3.4"), 0);
        Assert.Equal("0.0.0.0", zero.Network.ToString());
        Assert.Equal("255.255.255.255", zero.Broadcast.ToString());
        Assert.Equal(uint.MaxValue, zero.Wildcard.Value);
        Assert.Equal(4294967294L, zero.UsableHosts);
        Assert.Equal("This network (reserved)", zero.LegacyClass);

        var one = new Ipv4Network(Ipv4Address.Parse("1.2.3.4"), 1);
        Assert.Equal("0.0.0.0", one.Network.ToString());
        Assert.Equal("127.255.255.255", one.Broadcast.ToString());
        Assert.Equal(2147483646L, one.UsableHosts);
    }

    [Fact]
    public void Network_FirstAndLastUsable()
    {
        var n = Ipv4Network.Parse("192.168.1.0", 24);
        Assert.Equal("192.168.1.1", n.FirstUsable.ToString());
        Assert.Equal("192.168.1.254", n.LastUsable.ToString());

        var p2p = Ipv4Network.Parse("10.1.1.0", 31);
        Assert.Equal("10.1.1.0", p2p.FirstUsable.ToString());
        Assert.Equal("10.1.1.1", p2p.LastUsable.ToString());

        var host = Ipv4Network.Parse("10.1.1.9", 32);
        Assert.Equal("10.1.1.9", host.FirstUsable.ToString());
        Assert.Equal("10.1.1.9", host.LastUsable.ToString());
    }

    [Fact]
    public void Network_ContainsAndOverlaps()
    {
        var n = Ipv4Network.Parse("192.168.1.0", 24);
        Assert.True(n.Contains("192.168.1.1"));
        Assert.True(n.Contains("192.168.1.255"));
        Assert.False(n.Contains("192.168.2.1"));
        Assert.False(n.Contains("not-an-ip"));

        Assert.True(n.Overlaps(Ipv4Network.Parse("192.168.1.128", 25)));
        Assert.False(n.Overlaps(Ipv4Network.Parse("192.168.2.0", 24)));
        Assert.True(Ipv4Network.Parse("10.0.0.0", 8).Overlaps(Ipv4Network.Parse("10.9.0.0", 16)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Divide_ProducesEqualPowerOfTwoSubnets(int count)
    {
        var parent = Ipv4Network.Parse("192.168.1.0", 24);
        var parts = parent.Divide(count);
        Assert.Equal(count, parts.Count);
        Assert.All(parts, p => Assert.Equal(24 + Math.Log2(count), p.PrefixLength));
        Assert.Equal(parent.Network, parts[0].Network);
        Assert.Equal(parent.Broadcast, parts[^1].Broadcast);
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.Equal(parent.Network.Value + (uint)(i * (parent.TotalAddresses / count)), parts[i].Network.Value);
        }
    }

    [Fact]
    public void Divide_NonPowerOfTwoRoundsUp()
    {
        var parent = Ipv4Network.Parse("192.168.1.0", 24);
        var parts = parent.Divide(3);
        Assert.Equal(4, parts.Count);
        Assert.Equal(Ipv4Network.NormalizeDivisionCount(3), parts.Count);
        Assert.Equal(Ipv4Network.NormalizeDivisionCount(5), 8);
        Assert.Equal(Ipv4Network.NormalizeDivisionCount(17), 32);
    }

    [Fact]
    public void Divide_RejectsImpossibleDivision()
    {
        Assert.Throws<InvalidOperationException>(() => Ipv4Network.Parse("192.168.1.0", 32).Divide(2));
        Assert.Throws<InvalidOperationException>(() => Ipv4Network.Parse("192.168.1.0", 31).Divide(4));
    }

    [Fact]
    public void Divide_Slash0Into256Networks()
    {
        var parent = Ipv4Network.Parse("0.0.0.0", 0);
        var parts = parent.Divide(256);
        Assert.Equal(256, parts.Count);
        Assert.Equal("0.0.0.0", parts[0].Network.ToString());
        Assert.Equal(8, parts[0].PrefixLength);
        Assert.Equal("255.0.0.0", parts[^1].Network.ToString());
    }

    [Fact]
    public void Hosts_RespectsLimitAndExcludesNetworkBroadcast()
    {
        var n = Ipv4Network.Parse("192.168.1.0", 24);
        var hosts = n.Hosts(10).ToList();
        Assert.Equal(10, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0].ToString());
        Assert.Equal("192.168.1.10", hosts[^1].ToString());
        Assert.DoesNotContain(n.Network, hosts);
        Assert.DoesNotContain(n.Broadcast, hosts);
    }

    [Fact]
    public void ParseCidr_ReportsErrors()
    {
        Assert.False(Ipv4Network.TryParseCidr("192.168.1.0", out _, out var error));
        Assert.Contains("prefix", error);
        Assert.False(Ipv4Network.TryParseCidr("192.168.1.0/33", out _, out error));
        Assert.Contains("0-32", error);
        Assert.False(Ipv4Network.TryParseCidr("300.1.1.1/24", out _, out error));
        Assert.True(Ipv4Network.TryParseCidr("10.0.0.0/8", out var net, out _));
        Assert.Equal("10.0.0.0/8", net.Cidr);
    }

    [Fact]
    public void ValidateHostAssignment_RejectsNetworkAndBroadcast()
    {
        var n = Ipv4Network.Parse("192.168.1.0", 24);
        Assert.False(SubnetCalculator.ValidateHostAssignment("192.168.1.0", n, out var e1));
        Assert.Contains("network address", e1);
        Assert.False(SubnetCalculator.ValidateHostAssignment("192.168.1.255", n, out var e2));
        Assert.Contains("broadcast", e2);
        Assert.False(SubnetCalculator.ValidateHostAssignment("192.168.2.1", n, out var e3));
        Assert.Contains("outside", e3);
        Assert.True(SubnetCalculator.ValidateHostAssignment("192.168.1.10", n, out _));

        var p2p = Ipv4Network.Parse("10.0.0.0", 31);
        Assert.True(SubnetCalculator.ValidateHostAssignment("10.0.0.0", p2p, out _));
        Assert.True(SubnetCalculator.ValidateHostAssignment("10.0.0.1", p2p, out _));
    }

    [Fact]
    public void PrivateAddressDetection()
    {
        Assert.True(Ipv4Address.Parse("192.168.1.1").IsPrivate);
        Assert.True(Ipv4Address.Parse("10.200.3.4").IsPrivate);
        Assert.True(Ipv4Address.Parse("172.31.255.254").IsPrivate);
        Assert.True(Ipv4Address.Parse("169.254.1.1").IsLinkLocal);
        Assert.False(Ipv4Address.Parse("8.8.8.8").IsPrivate);
        Assert.False(Ipv4Address.Parse("172.32.0.1").IsPrivate);
        Assert.False(Ipv4Address.Parse("192.169.1.1").IsPrivate);
    }

    [Fact]
    public void BinaryRepresentation_IsDottedBinary()
    {
        var n = Ipv4Network.Parse("192.168.1.0", 24);
        Assert.Equal("11000000.10101000.00000001.00000000", n.Network.ToBinaryString());
        Assert.Equal("11111111.11111111.11111111.00000000", n.Mask.ToBinaryString());
    }
}

public sealed class SubnetCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsAllFields()
    {
        var calc = SubnetCalculator.Calculate("192.168.1.42", 24);
        Assert.Equal("192.168.1.0", calc.NetworkAddress);
        Assert.Equal("192.168.1.255", calc.BroadcastAddress);
        Assert.Equal("255.255.255.0", calc.SubnetMask);
        Assert.Equal("0.0.0.255", calc.WildcardMask);
        Assert.Equal("192.168.1.1", calc.FirstUsable);
        Assert.Equal("192.168.1.254", calc.LastUsable);
        Assert.Equal(256, calc.TotalAddresses);
        Assert.Equal(254, calc.UsableHosts);
        Assert.Equal("Class C", calc.LegacyClass);
        Assert.Contains("11000000", calc.BinaryAddress);
        Assert.Equal("192.168.1.0 - 192.168.1.255", calc.Range);
    }

    [Theory]
    [InlineData(31, "RFC 3021")]
    [InlineData(32, "host route")]
    [InlineData(0, "Default route")]
    [InlineData(30, "two usable hosts")]
    public void Calculate_EdgeCaseNotes(int prefix, string fragment)
    {
        var calc = SubnetCalculator.Calculate("10.0.0.1", prefix);
        Assert.Contains(fragment, calc.Notes);
    }

    [Fact]
    public void Divide_ReturnsRowsWithCidr()
    {
        var parent = Ipv4Network.Parse("192.168.1.0", 24);
        var (rows, actual, newPrefix) = SubnetCalculator.Divide(parent, 4);
        Assert.Equal(4, actual);
        Assert.Equal(26, newPrefix);
        Assert.Equal(4, rows.Count);
        Assert.Equal("192.168.1.0/26", rows[0].Cidr);
        Assert.Equal("192.168.1.64/26", rows[1].Network + "/26");
        Assert.Equal(62, rows[0].UsableHosts);
        Assert.Equal("192.168.1.63", rows[0].Broadcast);
    }

    [Fact]
    public void Calculate_RejectsInvalidPrefix()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SubnetCalculator.Calculate("10.0.0.1", 33));
        Assert.Throws<FormatException>(() => SubnetCalculator.Calculate("999.0.0.1", 24));
    }
}

public sealed class Ipv6Tests
{
    [Fact]
    public void Parse_ValidAndInvalid()
    {
        Assert.True(Ipv6Utils.TryParse("2001:db8::1", out var address, out _));
        Assert.Equal("2001:db8::1", address.ToString());
        Assert.False(Ipv6Utils.TryParse("192.168.1.1", out _, out var error));
        Assert.Contains("IPv6", error);
        Assert.False(Ipv6Utils.TryParse("2001:db8::1/64", out _, out var error2));
        Assert.Contains("prefix", error2);
    }

    [Theory]
    [InlineData("2001:db8:abcd:1234::1", 64, "2001:db8:abcd:1234::")]
    [InlineData("2001:db8::1", 32, "2001:db8::")]
    [InlineData("fd00::1234", 16, "fd00::")]
    [InlineData("2001:db8::1", 128, "2001:db8::1")]
    public void NetworkAddress_MasksCorrectly(string address, int prefix, string expected)
    {
        Assert.Equal(expected, Ipv6Utils.NetworkAddress(System.Net.IPAddress.Parse(address), prefix));
    }

    [Fact]
    public void Counts_AreCalculated()
    {
        Assert.Equal("1", Ipv6Utils.UsableHosts(128));
        Assert.Equal("2", Ipv6Utils.UsableHosts(127));
        Assert.StartsWith("18,446,744,073,709,551,614", Ipv6Utils.UsableHosts(64));
        Assert.Equal("18,446,744,073,709,551,616", Ipv6Utils.TotalAddresses(64));
    }

    [Fact]
    public void Divide_ProducesExpectedSubnets()
    {
        var parts = Ipv6Utils.Divide(System.Net.IPAddress.Parse("2001:db8::"), 48, 52, 64);
        Assert.Equal(16, parts.Count);
        Assert.Equal("2001:db8::/52", parts[0]);
        Assert.Equal("2001:db8:0:f000::/52", parts[^1]);
    }

    [Fact]
    public void Divide_RejectsTooManySubnets()
    {
        Assert.Throws<ArgumentException>(() =>
            Ipv6Utils.Divide(System.Net.IPAddress.Parse("2001:db8::"), 48, 64, 64));
    }

    [Fact]
    public void AddressType_IdentifiesLinkLocalAndUniqueLocal()
    {
        Assert.Contains("Link-local", Ipv6Utils.AddressType(System.Net.IPAddress.Parse("fe80::1")));
        Assert.Contains("Unique local", Ipv6Utils.AddressType(System.Net.IPAddress.Parse("fd00::1")));
        Assert.Contains("Loopback", Ipv6Utils.AddressType(System.Net.IPAddress.Parse("::1")));
    }
}
