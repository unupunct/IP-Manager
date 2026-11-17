using System.Net;
using System.Net.Sockets;
using System.Text;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using IPManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPManager.Tests;

internal sealed class TempWorkspace : IDisposable
{
    public string Directory { get; }
    public AppPaths Paths { get; }
    public IDbContextFactory<IpManagerDbContext> Factory { get; }

    public TempWorkspace(bool migrate = false)
    {
        Directory = Path.Combine(Path.GetTempPath(), "ipmanager-tests", Guid.NewGuid().ToString("N"));
        Paths = AppPaths.Create(Directory);
        Factory = new TestDbContextFactory(Paths.DatabasePath);
        using var db = Factory.CreateDbContext();
        if (migrate) db.Database.Migrate();
        else db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, true); } catch { }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<IpManagerDbContext>
    {
        private readonly DbContextOptions<IpManagerDbContext> _options;
        public TestDbContextFactory(string path)
        {
            _options = new DbContextOptionsBuilder<IpManagerDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
        }
        public IpManagerDbContext CreateDbContext() => new(_options);
    }
}

internal sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
}

internal sealed class NullAudit : IAuditService
{
    public Task LogAsync(string category, string action, string? detail = null, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeSettings : ISettingsService
{
    public FakeSettings(AppSettings? settings = null) => Current = settings ?? new AppSettings();
    public AppSettings Current { get; private set; }
    public event EventHandler? Changed;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsync(CancellationToken ct = default) { Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public T Get<T>(Func<AppSettings, T> selector) => selector(Current);
    public Task SetAsync(Action<AppSettings> mutate, CancellationToken ct = default) { mutate(Current); return Task.CompletedTask; }
}

internal sealed class FakeAlerts : IAlertService
{
    public List<Alert> Raised { get; } = new();
    public event EventHandler<Alert>? AlertRaised;

    public Task<Alert?> RaiseAsync(AlertType type, AlertSeverity severity, string title, string message,
        string? entityType = null, int? entityId = null, string? ipAddress = null, string? cooldownKey = null,
        CancellationToken ct = default)
    {
        var alert = new Alert { Id = Raised.Count + 1, Type = type, Severity = severity, Title = title, Message = message, IpAddress = ipAddress, CreatedAt = DateTime.UtcNow };
        Raised.Add(alert);
        AlertRaised?.Invoke(this, alert);
        return Task.FromResult<Alert?>(alert);
    }

    public Task RecoverAsync(string cooldownKey, string title, string message, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> AcknowledgeAsync(int alertId, bool acknowledged = true, CancellationToken ct = default) => Task.FromResult(true);
    public Task<int> AcknowledgeAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<List<Alert>> GetAlertsAsync(bool includeAcknowledged = false, AlertSeverity? minSeverity = null,
        AlertType? type = null, int limit = 500, CancellationToken ct = default) => Task.FromResult(Raised);
    public Task<int> GetUnreadCountAsync(CancellationToken ct = default) => Task.FromResult(Raised.Count);
    public Task MarkAllReadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> PurgeOldAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
}

public sealed class ConflictAnalyzerTests
{
    private static MacObservation Obs(string ip, string mac, DateTime lastSeen, string source, int count = 2) => new()
    {
        IpAddress = ip,
        MacAddress = mac,
        LastSeen = lastSeen,
        FirstSeen = lastSeen.AddMinutes(-10),
        Source = source,
        ObservationCount = count
    };

    [Fact]
    public void FreshTwoMacsFromTwoSources_IsConfirmed()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var obs = new[]
        {
            Obs("192.168.1.50", "AA:BB:CC:DD:EE:01", now.AddMinutes(-1), "Arp"),
            Obs("192.168.1.50", "AA:BB:CC:DD:EE:02", now.AddMinutes(-2), "Icmp")
        };

        var findings = ConflictAnalyzer.AnalyzeNetwork(obs, now);
        var finding = Assert.Single(findings);
        Assert.Equal(ConflictState.Confirmed, finding.State);
        Assert.Equal(95, finding.Confidence);
        Assert.Equal(2, finding.MacAddresses.Count);
        Assert.Contains("confirmed", finding.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleSourceFastObservations_IsSuspected()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var obs = new[]
        {
            Obs("192.168.1.50", "AA:BB:CC:DD:EE:01", now.AddMinutes(-1), "Arp"),
            Obs("192.168.1.50", "AA:BB:CC:DD:EE:02", now.AddMinutes(-2), "Arp")
        };
        var finding = Assert.Single(ConflictAnalyzer.AnalyzeNetwork(obs, now));
        Assert.Equal(ConflictState.Suspected, finding.State);
        Assert.Equal(70, finding.Confidence);
    }

    [Fact]
    public void StaleArpOnly_IsSuspectedWithLowConfidence()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var obs = new[]
        {
            Obs("192.168.1.50", "AA:BB:CC:DD:EE:01", now.AddMinutes(-1), "Arp"),
            Obs("192.168.1.50", "AA:BB:CC:DD:EE:02", now.AddHours(-10), "Arp")
        };
        var finding = Assert.Single(ConflictAnalyzer.AnalyzeNetwork(obs, now));
        Assert.Equal(ConflictState.Suspected, finding.State);
        Assert.Equal(25, finding.Confidence);
        Assert.Contains("Stale", finding.Evidence);
    }

    [Fact]
    public void SingleMac_NoConflict()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var obs = new[] { Obs("192.168.1.50", "AA:BB:CC:DD:EE:01", now, "Arp", 5) };
        Assert.Empty(ConflictAnalyzer.AnalyzeNetwork(obs, now));
    }

    [Fact]
    public void DatabaseDuplicate_IsSeparateFromNetworkConflict()
    {
        var entries = new List<IpAddressEntry>
        {
            new() { Id = 1, Address = "192.168.1.10", Status = IpStatus.Assigned, Hostname = "nas", SubnetId = 1, UpdatedAt = DateTime.UtcNow },
            new() { Id = 2, Address = "192.168.1.10", Status = IpStatus.Reserved, Hostname = "printer", SubnetId = 1, UpdatedAt = DateTime.UtcNow }
        };
        var finding = Assert.Single(ConflictAnalyzer.AnalyzeDatabase(entries));
        Assert.Equal(ConflictMethod.DuplicateDatabaseAssignment, finding.Method);
        Assert.Equal(ConflictState.Suspected, finding.State);
        Assert.Equal(100, finding.Confidence);
        Assert.Contains("documentation duplicate", finding.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DatabaseDifferentAddresses_NoDuplicate()
    {
        var entries = new List<IpAddressEntry>
        {
            new() { Id = 1, Address = "192.168.1.10", Status = IpStatus.Assigned },
            new() { Id = 2, Address = "192.168.1.11", Status = IpStatus.Reserved }
        };
        Assert.Empty(ConflictAnalyzer.AnalyzeDatabase(entries));
    }
}

public sealed class NetworkParsingTests
{
    [Fact]
    public void ArpParser_ExtractsEntriesAndSkipsBroadcast()
    {
        const string output = """
        Interface: 192.168.1.10 --- 0x6
          Internet Address      Physical Address      Type
          192.168.1.1           00-1a-2b-3c-4d-5e     dynamic
          192.168.1.50          aa-bb-cc-dd-ee-ff     dynamic
          192.168.1.255         ff-ff-ff-ff-ff-ff     static
          224.0.0.22            01-00-5e-00-00-16     static
          192.168.1.77          00-11-22-33-44-55     static
        Interface: 10.0.0.5 --- 0x9
          10.0.0.1              00-aa-bb-cc-dd-ee     dynamic
        """;

        var entries = ArpTableReader.Parse(output, "192.168.1.10");
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.IpAddress == "192.168.1.1" && e.MacAddress == "00:1A:2B:3C:4D:5E");
        Assert.Contains(entries, e => e.IpAddress == "192.168.1.77" && e.IsStatic);
        Assert.DoesNotContain(entries, e => e.IpAddress == "192.168.1.255");
        Assert.DoesNotContain(entries, e => e.IpAddress == "224.0.0.22");
        Assert.DoesNotContain(entries, e => e.IpAddress == "10.0.0.1");
    }

    [Fact]
    public void MacNormalization()
    {
        Assert.Equal("AA:BB:CC:DD:EE:FF", MacUtil.Normalize("aabb.ccdd.eeff"));
        Assert.Equal("AA:BB:CC:DD:EE:FF", MacUtil.Normalize("AA-BB-CC-DD-EE-FF"));
        Assert.Null(MacUtil.Normalize("not-a-mac"));
        Assert.True(MacUtil.IsValid("001122334455"));
        Assert.True(MacUtil.IsLocallyAdministered("02:00:00:00:00:01"));
        Assert.False(MacUtil.IsLocallyAdministered("00:1A:2B:3C:4D:5E"));
        Assert.Equal("001A2B", MacUtil.OuiPrefix("00:1a:2b:3c:4d:5e"));
    }

    [Fact]
    public void OuiDatabase_LookupAndIeeeCsvParsing()
    {
        var db = OuiDatabase.CreateWithSeed();
        Assert.True(db.TryGetVendor("B8:27:EB:12:34:56", out var vendor));
        Assert.Contains("Raspberry", vendor);
        Assert.Null(db.Lookup("02:00:00:00:00:01"));

        db.ParseLine("MA-L,28-6F-B9,Test Vendor Inc.,28-6F-B9,address");
        Assert.Equal("Test Vendor Inc.", db.Lookup("28:6F:B9:00:00:01"));
        db.ParseLine("AABBCC,Simple Vendor");
        Assert.Equal("Simple Vendor", db.Lookup("AA:BB:CC:00:00:01"));
    }

    [Fact]
    public void DnsWire_BuildsQuery()
    {
        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A, 0x1234);
        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);
        Assert.Equal(1, query[5]); // QDCOUNT
        Assert.Contains((byte)7, query); // label length
        // Query ends with QTYPE (00 01 = A) and QCLASS (00 01 = IN).
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x01 }, query[^4..]);
    }

    [Fact]
    public void DnsWire_ParsesAAndPtrWithCompression()
    {
        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A, 0x4242);
        var response = new List<byte>(query);
        response[2] = 0x81;
        response[3] = 0x80;
        response[6] = 0;
        response[7] = 1; // ANCOUNT
        // answer: name pointer, type A, class IN, ttl 300, rdlen 4, 93.184.216.34
        response.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 0x2C, 0x00, 0x04, 93, 184, 216, 34 });

        var message = DnsWire.Parse(response.ToArray(), 0x4242);
        Assert.Null(message.Error);
        Assert.Equal(0, message.ResponseCode);
        var answer = Assert.Single(message.Answers);
        Assert.Equal(DnsRecordType.A, answer.Type);
        Assert.Equal("93.184.216.34", answer.Value);
        Assert.Equal(300u, answer.Ttl);
        Assert.StartsWith("example.com", answer.Name);
    }

    [Fact]
    public void DnsWire_ParsesTxtAndMx()
    {
        var query = DnsWire.BuildQuery("example.com", DnsRecordType.TXT, 7);
        var response = new List<byte>(query);
        response[2] = 0x81;
        response[3] = 0x80;
        response[7] = 2;
        // TXT "v=spf1 -all"
        var txt = Encoding.ASCII.GetBytes("v=spf1 -all");
        response.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, (byte)(txt.Length + 1), (byte)txt.Length });
        response.AddRange(txt);
        // MX 10 mail.example.com
        // MX RDATA: preference (2 bytes) + "mail" label (5 bytes) + compression pointer (2 bytes) = 9 bytes.
        response.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x09, 0x00, 0x0A });
        var name = Encoding.ASCII.GetBytes("mail");
        response.Add((byte)name.Length);
        response.AddRange(name);
        response.AddRange(new byte[] { 0xC0, 0x0C });

        var message = DnsWire.Parse(response.ToArray(), 7);
        Assert.Null(message.Error);
        Assert.Equal(2, message.Answers.Count);
        Assert.Equal("v=spf1 -all", message.Answers[0].Value);
        Assert.Equal("10", message.Answers[1].Value.Split(' ')[0]);
        Assert.Contains("mail", message.Answers[1].Value);
    }

    [Fact]
    public void DnsWire_RejectsMismatchedId()
    {
        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A, 1);
        var message = DnsWire.Parse(query, 2);
        Assert.NotNull(message.Error);
    }

    [Fact]
    public void ReverseName_IPv4AndIPv6()
    {
        Assert.Equal("1.2.0.192.in-addr.arpa", DnsWire.ReverseName(IPAddress.Parse("192.0.2.1")));
        var v6 = DnsWire.ReverseName(IPAddress.Parse("2001:db8::1"));
        Assert.EndsWith("ip6.arpa", v6);
        Assert.StartsWith("1.0.0.0", v6);
    }

    [Fact]
    public void NetBios_BuildQueryHasNbstatType()
    {
        var packet = NetBiosResolver.BuildQuery(0x1234);
        Assert.Equal(50, packet.Length);
        Assert.Equal(0x12, packet[0]);
        Assert.Equal(0x20, packet[12]);
        Assert.Equal(0x21, packet[47]);
        Assert.Equal(0x01, packet[49]);
    }

    [Fact]
    public void NetBios_ParsesFixtureResponse()
    {
        // Build a minimal NBSTAT response with one unique name and a MAC address.
        var packet = new List<byte>();
        packet.AddRange(new byte[] { 0x12, 0x34, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });
        packet.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C });
        var payload = new List<byte> { 2 };
        var name = Encoding.ASCII.GetBytes("MYPC".PadRight(15, '\0'));
        payload.AddRange(name);
        payload.Add(0x00);
        payload.AddRange(new byte[] { 0x00, 0x00 }); // unique
        var group = Encoding.ASCII.GetBytes("WORKGROUP".PadRight(15, '\0'));
        payload.AddRange(group);
        payload.Add(0x00);
        payload.AddRange(new byte[] { 0x80, 0x00 }); // group
        payload.AddRange(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

        packet.Add((byte)(payload.Count >> 8));
        packet.Add((byte)(payload.Count & 0xFF));
        packet.AddRange(payload);

        var result = NetBiosResolver.Parse(packet.ToArray());
        Assert.True(result.Success);
        Assert.Equal("MYPC", result.Name);
        Assert.Equal("WORKGROUP", result.Workgroup);
        Assert.Equal("00:11:22:33:44:55", result.MacAddress);
    }

    [Fact]
    public void ScanTargetParser_SingleCidrAndRange()
    {
        var result = ScanTargetParser.Parse(new[] { "192.168.1.10, 192.168.1.0/30, 10.0.0.10-12" }, 8192);
        Assert.Empty(result.Errors);
        Assert.False(result.ContainsPublicAddresses);
        Assert.Contains(Ipv4Address.Parse("192.168.1.10"), result.Addresses);
        Assert.Contains(Ipv4Address.Parse("192.168.1.1"), result.Addresses);
        Assert.Contains(Ipv4Address.Parse("192.168.1.2"), result.Addresses);
        Assert.DoesNotContain(Ipv4Address.Parse("192.168.1.3"), result.Addresses); // broadcast
        Assert.Contains(Ipv4Address.Parse("10.0.0.12"), result.Addresses);
        Assert.Equal(6, result.Addresses.Count);
    }

    [Fact]
    public void ScanTargetParser_DetectsPublicAndLimit()
    {
        var result = ScanTargetParser.Parse(new[] { "8.8.8.8" }, 8192);
        Assert.True(result.ContainsPublicAddresses);

        var limited = ScanTargetParser.Parse(new[] { "10.0.0.0/16" }, 256);
        Assert.True(limited.ExceededLimit);
        Assert.True(limited.Addresses.Count <= 256);
        Assert.NotEmpty(limited.Errors);
    }

    [Fact]
    public void ScanTargetParser_ReportsInvalidInput()
    {
        var result = ScanTargetParser.Parse(new[] { "not-an-ip", "192.168.1.0/99", "10.0.0.9-2" }, 100);
        Assert.Equal(0, result.Addresses.Count);
        Assert.True(result.Errors.Count >= 3);
    }

    [Fact]
    public void DhcpParsers_DnsmasqLeasesAndConfig()
    {
        const string leases = "1700000000 aa:bb:cc:dd:ee:ff 192.168.1.100 phone 01:aa:bb:cc:dd:ee:ff\n1700000100 00:11:22:33:44:55 192.168.1.101 * *\n";
        var parsed = DhcpFileParsers.ParseDnsmasqLeases(leases);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("AA:BB:CC:DD:EE:FF", parsed[0].MacAddress);
        Assert.Equal("phone", parsed[0].Hostname);
        Assert.NotNull(parsed[0].ExpiresUtc);

        const string conf = "dhcp-host=aa:bb:cc:dd:ee:ff,192.168.1.50,nas\n# comment\ndhcp-range=192.168.1.100,192.168.1.200,12h\n";
        var (reservations, ranges) = DhcpFileParsers.ParseDnsmasqConfig(conf);
        Assert.Single(reservations);
        Assert.Equal("192.168.1.50", reservations[0].IpAddress);
        Assert.Equal("nas", reservations[0].Hostname);
        var range = Assert.Single(ranges);
        Assert.Equal("192.168.1.100", range.Start);
        Assert.Equal("192.168.1.200", range.End);
    }

    [Fact]
    public void DhcpParsers_IscLeases()
    {
        const string content = """
        lease 192.168.1.120 {
          starts 1 2026/11/17 08:00:00;
          ends 1 2026/11/17 20:00:00;
          hardware ethernet 00:11:22:33:44:55;
          client-hostname "printer";
        }
        """;
        var lease = Assert.Single(DhcpFileParsers.ParseIscLeases(content));
        Assert.Equal("192.168.1.120", lease.IpAddress);
        Assert.Equal("00:11:22:33:44:55", lease.MacAddress);
        Assert.Equal("printer", lease.Hostname);
        Assert.NotNull(lease.ExpiresUtc);
    }

    [Fact]
    public void DhcpParsers_KeaCsv()
    {
        const string csv = "address,hwaddr,client_id,valid_lifetime,expire,subnet_id,fqdn_fwd,fqdn_rev,hostname,state\n" +
                           "192.168.1.130,00:11:22:33:44:66,,3600,2026-11-17 13:00:00,1,0,0,camera,0\n";
        var lease = Assert.Single(DhcpFileParsers.ParseKeaLeasesCsv(csv));
        Assert.Equal("192.168.1.130", lease.IpAddress);
        Assert.Equal("camera", lease.Hostname);
        Assert.NotNull(lease.ExpiresUtc);
    }

    [Fact]
    public void DeviceClassifier_UsesHostnameAndPorts()
    {
        Assert.Equal(DeviceCategory.Printer, DeviceClassifier.Guess("HP", "officejet-1", null, new[] { 9100 }));
        Assert.Equal(DeviceCategory.Nas, DeviceClassifier.Guess("Synology", "nas-01", null, new[] { 445 }));
        Assert.Equal(DeviceCategory.IpCamera, DeviceClassifier.Guess("Hikvision", "front-door-cam", null, null));
        Assert.Equal(DeviceCategory.VirtualMachine, DeviceClassifier.Guess("VMware, Inc.", "vm1", null, null));
        Assert.Equal(DeviceCategory.GamingConsole, DeviceClassifier.Guess("Sony", "playstation-5", null, null));
    }
}
