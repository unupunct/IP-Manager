using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using IPManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPManager.Tests;

public sealed class InventoryDatabaseTests
{
    private static (TempWorkspace Ws, InventoryService Inventory, TestClock Clock) Create()
    {
        var ws = new TempWorkspace();
        var clock = new TestClock();
        var inventory = new InventoryService(ws.Factory, clock, new NullAudit(), OuiDatabase.CreateWithSeed());
        return (ws, inventory, clock);
    }

    private static Subnet ExampleSubnet() => new()
    {
        Name = "Home LAN",
        NetworkAddress = "192.168.1.0",
        PrefixLength = 24,
        Gateway = "192.168.1.1",
        DhcpStart = "192.168.1.100",
        DhcpEnd = "192.168.1.200",
        StaticStart = "192.168.1.2",
        StaticEnd = "192.168.1.99",
        DnsServers = "192.168.1.1",
        Location = "Home"
    };

    [Fact]
    public async Task Subnet_RoundTrip_Persists()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var saved = await inventory.SaveSubnetAsync(ExampleSubnet());
        Assert.True(saved.Subnet.Id > 0);
        Assert.Empty(saved.Warnings);

        var loaded = await inventory.GetSubnetsAsync();
        var subnet = Assert.Single(loaded);
        Assert.Equal("Home LAN", subnet.Name);
        Assert.Equal("192.168.1.0/24", subnet.Cidr);
        Assert.Equal(254, subnet.NetworkOrNull!.Value.UsableHosts);
    }

    [Fact]
    public async Task Subnet_NormalizesNetworkAddressAndWarns()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var input = ExampleSubnet();
        input.NetworkAddress = "192.168.1.77";
        var result = await inventory.SaveSubnetAsync(input);
        Assert.Equal("192.168.1.0", result.Subnet.NetworkAddress);
        Assert.Contains(result.Warnings, w => w.Contains("normalized"));
    }

    [Fact]
    public async Task Subnet_ValidatesGatewayAndRanges()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;

        var badGateway = ExampleSubnet();
        badGateway.Gateway = "10.0.0.1";
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveSubnetAsync(badGateway));

        var badRange = ExampleSubnet();
        badRange.DhcpStart = "192.168.2.1";
        badRange.DhcpEnd = "192.168.2.50";
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveSubnetAsync(badRange));

        var reversed = ExampleSubnet();
        reversed.StaticStart = "192.168.1.99";
        reversed.StaticEnd = "192.168.1.2";
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveSubnetAsync(reversed));

        var overlappingPool = ExampleSubnet();
        overlappingPool.DhcpStart = "192.168.1.50";
        overlappingPool.DhcpEnd = "192.168.1.150";
        var saved = await inventory.SaveSubnetAsync(overlappingPool);
        Assert.Contains(saved.Warnings, w => w.Contains("overlaps"));
    }

    [Fact]
    public async Task Subnet_RejectsDuplicateCidr()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        await inventory.SaveSubnetAsync(ExampleSubnet());
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveSubnetAsync(ExampleSubnet()));
    }

    [Fact]
    public async Task Device_SaveCreatesAddressEntryAndHistory()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;

        var device = await inventory.SaveDeviceAsync(new Device
        {
            Name = "NAS",
            IpAddress = "192.168.1.10",
            MacAddress = "00:11:32:33:44:55",
            Category = DeviceCategory.Nas,
            SubnetId = subnet.Id,
            IsTrusted = true
        });

        Assert.True(device.Id > 0);
        Assert.Equal("00:11:32:33:44:55", device.MacAddress);
        Assert.Equal("Synology Incorporated", device.Manufacturer);

        var addresses = await inventory.GetAddressesAsync(subnet.Id);
        var entry = Assert.Single(addresses);
        Assert.Equal("192.168.1.10", entry.Address);
        Assert.Equal(IpStatus.Assigned, entry.Status);
        Assert.True(entry.IsManuallyManaged);

        device.Name = "NAS (renamed)";
        device.Category = DeviceCategory.Server;
        var updated = await inventory.SaveDeviceAsync(device);
        Assert.Equal("NAS (renamed)", updated.Name);

        var history = await inventory.GetDeviceHistoryAsync(device.Id);
        Assert.Contains(history, h => h.Field == "Name");
        Assert.Contains(history, h => h.Field == "Category");
    }

    [Fact]
    public async Task Device_DuplicateIpIsRejected()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;
        await inventory.SaveDeviceAsync(new Device { Name = "A", IpAddress = "192.168.1.20", SubnetId = subnet.Id });

        await Assert.ThrowsAsync<DuplicateAssignmentException>(() =>
            inventory.SaveDeviceAsync(new Device { Name = "B", IpAddress = "192.168.1.20", SubnetId = subnet.Id }));
    }

    [Fact]
    public async Task Device_ValidationRejectsBadInput()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveDeviceAsync(new Device { Name = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveDeviceAsync(new Device { Name = "x", IpAddress = "999.1.1.1" }));
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveDeviceAsync(new Device { Name = "x", MacAddress = "zz:zz" }));
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveDeviceAsync(new Device { Name = "x", MacAddress = "FF:FF:FF:FF:FF:FF" }));
    }

    [Fact]
    public async Task AddressMap_SynthesizesStatuses()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;
        await inventory.SaveDeviceAsync(new Device { Name = "Laptop", IpAddress = "192.168.1.10", SubnetId = subnet.Id });
        await inventory.SaveAddressAsync(new IpAddressEntry
        {
            SubnetId = subnet.Id,
            Address = "192.168.1.50",
            Status = IpStatus.Reserved,
            AllocationType = AllocationType.Reserved,
            Description = "Reserved for printer"
        });

        var map = await inventory.GetAddressMapAsync(subnet);
        Assert.Equal(254, map.TotalHosts);
        Assert.False(map.Truncated);
        Assert.Equal(254, map.Rows.Count);
        Assert.Equal(IpStatus.Gateway, map.Rows.Single(r => r.Address == "192.168.1.1").Status);
        Assert.Equal(IpStatus.Assigned, map.Rows.Single(r => r.Address == "192.168.1.10").Status);
        Assert.Equal(IpStatus.Reserved, map.Rows.Single(r => r.Address == "192.168.1.50").Status);
        Assert.Equal(IpStatus.Available, map.Rows.Single(r => r.Address == "192.168.1.60").Status);
        Assert.Equal(1, map.GatewayCount);
        Assert.Equal(1, map.AssignedCount);
        Assert.Equal(1, map.ReservedCount);
    }

    [Fact]
    public async Task Address_ValidationAndDuplicates()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;

        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveAddressAsync(new IpAddressEntry
        {
            SubnetId = subnet.Id, Address = "192.168.2.5", Status = IpStatus.Assigned
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.SaveAddressAsync(new IpAddressEntry
        {
            SubnetId = subnet.Id, Address = "192.168.1.255", Status = IpStatus.Assigned
        }));

        await inventory.SaveAddressAsync(new IpAddressEntry
        {
            SubnetId = subnet.Id, Address = "192.168.1.60", Status = IpStatus.Assigned, Hostname = "srv"
        });
        await Assert.ThrowsAsync<DuplicateAssignmentException>(() => inventory.SaveAddressAsync(new IpAddressEntry
        {
            SubnetId = subnet.Id, Address = "192.168.1.60", Status = IpStatus.Reserved
        }));

        // Available and excluded are documentation statuses and do not block a future assignment.
        await inventory.SaveAddressAsync(new IpAddressEntry
        {
            SubnetId = subnet.Id, Address = "192.168.1.61", Status = IpStatus.Excluded
        });
        var assigned = await inventory.SaveAddressAsync(new IpAddressEntry
        {
            SubnetId = subnet.Id, Address = "192.168.1.61", Status = IpStatus.Assigned
        });
        Assert.Equal(IpStatus.Assigned, assigned.Status);
    }

    [Fact]
    public async Task Utilization_ReportsWarnings()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;
        for (var i = 2; i < 30; i++)
        {
            await inventory.SaveAddressAsync(new IpAddressEntry
            {
                SubnetId = subnet.Id, Address = $"192.168.1.{i}", Status = IpStatus.Assigned
            });
        }

        var utilization = await inventory.GetUtilizationAsync(subnet);
        Assert.Equal(254, utilization.TotalHosts);
        Assert.Equal(29, utilization.Used);
        Assert.Equal(101, utilization.DhcpPoolSize); // .100 through .200 inclusive
        Assert.Equal(0, utilization.DhcpUsed);
        Assert.True(utilization.Percent < 20);
    }

    [Fact]
    public async Task DeleteDevice_KeepsAddressDocumentation()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;
        var device = await inventory.SaveDeviceAsync(new Device { Name = "Camera", IpAddress = "192.168.1.30", SubnetId = subnet.Id });
        await inventory.DeleteDeviceAsync(device.Id);

        var address = await inventory.FindAddressAsync("192.168.1.30");
        Assert.NotNull(address);
        Assert.Null(address!.DeviceId);
        Assert.Empty(await inventory.GetDevicesAsync());
    }

    [Fact]
    public async Task ApplyScanResult_CreatesDevicesAndObservations()
    {
        var (ws, inventory, _) = Create();
        using var _ws = ws;
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;

        var scan = new ScanResult
        {
            Hosts = new List<DiscoveredHost>
            {
                new()
                {
                    IpAddress = "192.168.1.40",
                    MacAddress = "00:11:22:33:44:66",
                    Hostname = "printer",
                    Vendor = "Seiko Epson Corporation",
                    RespondedIcmp = true,
                    SeenInArp = true,
                    OpenPorts = new[] { 9100 },
                    Sources = new[] { DiscoveryMethod.Icmp, DiscoveryMethod.Arp }
                }
            },
            AddressesChecked = 1,
            StartedAt = DateTime.UtcNow.AddSeconds(-2),
            CompletedAt = DateTime.UtcNow
        };

        var applied = await inventory.ApplyScanResultAsync(scan);
        Assert.Equal(1, applied.NewDevices);
        Assert.Equal(1, applied.ObservationsRecorded);

        var device = Assert.Single(await inventory.GetDevicesAsync());
        Assert.Equal("printer", device.Name);
        Assert.Equal(DeviceCategory.Printer, device.Category);
        Assert.Equal(subnet.Id, device.SubnetId);
        Assert.Equal(DeviceStatus.Online, device.Status);

        using var db = ws.Factory.CreateDbContext();
        Assert.Equal(1, db.MacObservations.Count());
        Assert.Equal(1, db.IpAddresses.Count());

        // A second scan must update, not duplicate, the device.
        var applied2 = await inventory.ApplyScanResultAsync(scan);
        Assert.Equal(0, applied2.NewDevices);
        Assert.Equal(1, applied2.UpdatedDevices);
        Assert.Single(await inventory.GetDevicesAsync());
    }

    [Fact]
    public async Task Settings_PersistAndSanitize()
    {
        using var ws = new TempWorkspace();
        var service = new SettingsService(ws.Factory);
        await service.LoadAsync();
        Assert.Equal("Dark", service.Current.Theme);
        Assert.Equal(32, service.Current.ScanConcurrency);

        await service.SetAsync(s =>
        {
            s.Theme = "Light";
            s.ScanConcurrency = 99999;
            s.ScanTimeoutMs = 1;
            s.SubnetUtilizationThreshold = 999;
        });

        var reloaded = new SettingsService(ws.Factory);
        await reloaded.LoadAsync();
        Assert.Equal("Light", reloaded.Current.Theme);
        Assert.Equal(512, reloaded.Current.ScanConcurrency);
        Assert.Equal(100, reloaded.Current.ScanTimeoutMs);
        Assert.Equal(100, reloaded.Current.SubnetUtilizationThreshold);

        await reloaded.SetAsync(s => s.Theme = "Purple");
        var again = new SettingsService(ws.Factory);
        again.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal("Dark", again.Current.Theme);
    }

    [Fact]
    public async Task Backup_CreateAndRestorePreservesData()
    {
        using var ws = new TempWorkspace();
        var clock = new TestClock();
        var inventory = new InventoryService(ws.Factory, clock, new NullAudit(), OuiDatabase.CreateWithSeed());
        var backup = new BackupService(ws.Paths, ws.Factory, clock);

        await inventory.SaveSubnetAsync(ExampleSubnet());
        var info = await backup.CreateBackupAsync("test");
        Assert.True(File.Exists(info.Path));
        Assert.True(info.SizeBytes > 0);

        var second = ExampleSubnet();
        second.Name = "Guest";
        second.NetworkAddress = "192.168.2.0";
        second.Gateway = "192.168.2.1";
        second.DhcpStart = null;
        second.DhcpEnd = null;
        second.StaticStart = null;
        second.StaticEnd = null;
        await inventory.SaveSubnetAsync(second);
        Assert.Equal(2, (await inventory.GetSubnetsAsync()).Count);

        await backup.RestoreBackupAsync(info.Path);
        var restored = await inventory.GetSubnetsAsync();
        Assert.Single(restored);
        Assert.Equal("Home LAN", restored[0].Name);
        Assert.Contains(backup.ListBackups(), b => b.Path == info.Path);
    }

    [Fact]
    public void Backup_RejectsNonSqliteFile()
    {
        using var ws = new TempWorkspace();
        var service = new BackupService(ws.Paths, ws.Factory, new TestClock());
        var fake = Path.Combine(ws.Directory, "fake.db");
        File.WriteAllText(fake, "not a database");
        Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreBackupAsync(fake)).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Retention_CleanupRemovesOldData()
    {
        using var ws = new TempWorkspace();
        var clock = new TestClock();
        var settings = new FakeSettings(new AppSettings { MonitoringRetentionDays = 7, AuditRetentionDays = 30 });
        var monitoring = new MonitoringService(ws.Factory, settings, new FakeAlerts(), clock, NullLogger<MonitoringService>.Instance);
        var maintenance = new MaintenanceService(ws.Factory, settings, new BackupService(ws.Paths, ws.Factory, clock), clock, new NullAudit());

        using (var db = ws.Factory.CreateDbContext())
        {
            db.Devices.Add(new Device { Name = "d", IpAddress = "192.168.1.5", FirstSeen = clock.UtcNow, LastSeen = clock.UtcNow });
            db.SaveChanges();
            var id = db.Devices.First().Id;
            db.MonitoringResults.AddRange(
                new MonitoringResult { DeviceId = id, Timestamp = clock.UtcNow.AddDays(-30), Success = true, Method = MonitoringMethod.Icmp },
                new MonitoringResult { DeviceId = id, Timestamp = clock.UtcNow.AddHours(-1), Success = true, Method = MonitoringMethod.Icmp });
            db.AuditEvents.Add(new AuditEvent { Timestamp = clock.UtcNow.AddDays(-100), Category = "x", Action = "y" });
            db.SaveChanges();
        }

        var message = await maintenance.RunAsync();
        Assert.Contains("Removed", message);
        using var verify = ws.Factory.CreateDbContext();
        Assert.Equal(1, verify.MonitoringResults.Count());
        Assert.Empty(verify.AuditEvents);
    }

    [Fact]
    public async Task Monitoring_UptimeIgnoresMissingData()
    {
        using var ws = new TempWorkspace();
        var clock = new TestClock();
        var service = new MonitoringService(ws.Factory, new FakeSettings(), new FakeAlerts(), clock, NullLogger<MonitoringService>.Instance);

        int deviceId;
        using (var db = ws.Factory.CreateDbContext())
        {
            var device = new Device { Name = "server", IpAddress = "192.168.1.5", FirstSeen = clock.UtcNow, LastSeen = clock.UtcNow };
            db.Devices.Add(device);
            db.SaveChanges();
            deviceId = device.Id;
            db.MonitoringResults.AddRange(
                new MonitoringResult { DeviceId = deviceId, Timestamp = clock.UtcNow.AddHours(-1), Success = true, Method = MonitoringMethod.Icmp, ResponseMs = 5 },
                new MonitoringResult { DeviceId = deviceId, Timestamp = clock.UtcNow.AddHours(-2), Success = true, Method = MonitoringMethod.Icmp, ResponseMs = 7 },
                new MonitoringResult { DeviceId = deviceId, Timestamp = clock.UtcNow.AddHours(-3), Success = false, Method = MonitoringMethod.Icmp });
            db.SaveChanges();
        }

        var stats = await service.GetUptimeAsync(deviceId, 2);
        Assert.Equal(3, stats.TotalChecks);
        Assert.Equal(2, stats.Successes);
        Assert.Equal(1, stats.Failures);
        Assert.Equal(66.67, stats.UptimePercent!.Value, 2);
        Assert.Equal(2, stats.Buckets.Count);
        // Days without observations have no percentage; they must not be treated as downtime.
        Assert.All(stats.Buckets.Where(b => b.Total > 0), b => Assert.NotNull(b.Percent));
        Assert.All(stats.Buckets.Where(b => b.Total == 0), b => Assert.Null(b.Percent));

        var empty = await service.GetUptimeAsync(deviceId, 30);
        Assert.NotNull(empty.UptimePercent); // same device still has data in the last 30 days
        var unknown = await service.GetUptimeAsync(999999, 7);
        Assert.Null(unknown.UptimePercent);
        Assert.Empty(unknown.Buckets.Where(b => b.Percent is not null));
    }

    [Fact]
    public async Task Dhcp_LocalDocumentationCrud()
    {
        using var ws = new TempWorkspace();
        var clock = new TestClock();
        var inventory = new InventoryService(ws.Factory, clock, new NullAudit(), OuiDatabase.CreateWithSeed());
        var subnet = (await inventory.SaveSubnetAsync(ExampleSubnet())).Subnet;
        var service = new DhcpService(ws.Factory, clock, new NullAudit(), new IDhcpProvider[] { new LocalDocumentationDhcpProvider(ws.Factory) });

        var scope = await service.SaveScopeAsync(new DhcpScope
        {
            Name = "Home pool",
            SubnetId = subnet.Id,
            ServerAddress = "192.168.1.1",
            StartAddress = "192.168.1.100",
            EndAddress = "192.168.1.200",
            LeaseMinutes = 1440
        });
        Assert.True(scope.Scope.Id > 0);

        var reservation = await service.SaveReservationAsync(new DhcpReservation
        {
            DhcpScopeId = scope.Scope.Id,
            IpAddress = "192.168.1.120",
            MacAddress = "00:11:22:33:44:77",
            Hostname = "printer"
        });
        Assert.Equal("00:11:22:33:44:77", reservation.MacAddress);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveReservationAsync(new DhcpReservation
        {
            DhcpScopeId = scope.Scope.Id,
            IpAddress = "192.168.1.250",
            MacAddress = "00:11:22:33:44:88"
        }));

        var scopes = await service.GetScopesAsync();
        Assert.Single(scopes);
        Assert.Single(scopes[0].Reservations);
    }

    [Fact]
    public async Task Dns_LocalDocumentationAndHistory()
    {
        using var ws = new TempWorkspace();
        var clock = new TestClock();
        var resolver = new DnsResolver(() => new[] { "127.0.0.1" });
        var service = new DnsManagerService(ws.Factory, resolver, clock, new NullAudit(),
            new DpapiSecretProtector(), Array.Empty<IDnsProvider>());

        var record = await service.SaveLocalRecordAsync(new DnsRecordDoc
        {
            Zone = "home.lan", Name = "nas.home.lan", Type = "A", Value = "192.168.1.10", Ttl = 300
        });
        Assert.True(record.Id > 0);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveLocalRecordAsync(new DnsRecordDoc
        {
            Zone = "home.lan", Name = "bad.home.lan", Type = "A", Value = "not-an-ip"
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveLocalRecordAsync(new DnsRecordDoc
        {
            Zone = "home.lan", Name = "bad.home.lan", Type = "SPF", Value = "x"
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveLocalRecordAsync(new DnsRecordDoc
        {
            Zone = "home.lan", Name = "nas2.home.lan", Type = "AAAA", Value = "192.168.1.2"
        }));

        Assert.Single(await service.GetLocalRecordsAsync());
        await service.DeleteLocalRecordAsync(record.Id);
        Assert.Empty(await service.GetLocalRecordsAsync());
        Assert.Empty(await service.GetHistoryAsync());
    }
}

public sealed class ExportTests
{
    private static ReportTable Table() => new()
    {
        Title = "Test report",
        Subtitle = "Subtitle",
        Headers = new[] { "Name", "Value", "Notes" },
        Rows = new List<IReadOnlyList<string>>
        {
            new[] { "router", "192.168.1.1", "has, comma" },
            new[] { "quoted", "a\"b", "line\nbreak" }
        },
        Metadata = new Dictionary<string, string> { ["Rows"] = "2" }
    };

    [Fact]
    public void Csv_EscapesCommasQuotesAndNewlines()
    {
        var csv = ReportExporter.ToCsv(Table());
        Assert.Contains("Name,Value,Notes", csv);
        Assert.Contains("\"has, comma\"", csv);
        Assert.Contains("\"a\"\"b\"", csv);
        Assert.Contains("\"line\nbreak\"", csv);
    }

    [Fact]
    public void Json_IsValidAndContainsRows()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Directory, "report.json");
        ReportExporter.WriteJson(Table(), path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Test report", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal("router", doc.RootElement.GetProperty("rows")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public void Pdf_HasValidStructure()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Directory, "report.pdf");
        ReportExporter.WritePdf(Table(), path);
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("/Type /Page", text);
        Assert.Contains("startxref", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.True(bytes.Length > 500);
    }

    [Fact]
    public async Task ReportsService_BuildsAllReports()
    {
        using var ws = new TempWorkspace();
        var clock = new TestClock();
        var inventory = new InventoryService(ws.Factory, clock, new NullAudit(), OuiDatabase.CreateWithSeed());
        await inventory.SaveSubnetAsync(new Subnet
        {
            Name = "LAN", NetworkAddress = "10.0.0.0", PrefixLength = 24, Gateway = "10.0.0.1",
            DhcpStart = "10.0.0.100", DhcpEnd = "10.0.0.200"
        });
        var reports = new ReportsService(ws.Factory, inventory, ws.Paths, clock);
        var all = await reports.BuildAllAsync();
        Assert.Equal(Enum.GetValues<ReportKind>().Length, all.Count);
        Assert.All(all, r => Assert.NotEmpty(r.Headers));

        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var path = await reports.ExportAsync(all[0], format);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 100);
        }
    }
}

public sealed class DnsResolverTests
{
    [Fact]
    public async Task QueryAsync_UsesCustomServerAndParsesAnswer()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var responder = Task.Run(async () =>
        {
            var received = await server.ReceiveAsync(cts.Token);
            var query = received.Buffer;
            var response = new List<byte>(query);
            response[2] = 0x81;
            response[3] = 0x80;
            response[7] = 1; // ANCOUNT
            response.AddRange(new byte[]
            {
                0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01,
                0x00, 0x00, 0x01, 0x2C,
                0x00, 0x04, 10, 20, 30, 40
            });
            await server.SendAsync(response.ToArray(), response.Count, received.RemoteEndPoint);
        }, cts.Token);

        var resolver = new DnsResolver(() => new[] { "127.0.0.1" });
        var result = await resolver.QueryAsync("test.local", "A", $"127.0.0.1:{port}", 3000);
        await responder;

        Assert.True(result.Success, result.Error);
        Assert.Equal("127.0.0.1", result.Server.Split(':')[0]);
        var answer = Assert.Single(result.Answers);
        Assert.Equal("10.20.30.40", answer.Value);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public async Task QueryAsync_ReportsTimeout()
    {
        // Port 1 on loopback has no DNS listener; the resolver must fail gracefully.
        var resolver = new DnsResolver(() => new[] { "127.0.0.1" });
        var result = await resolver.QueryAsync("test.local", "A", "127.0.0.1:1", 300);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ReverseAsync_RejectsInvalidAddress()
    {
        var resolver = new DnsResolver(() => Array.Empty<string>());
        var result = await resolver.ReverseAsync("not-an-ip", null);
        Assert.False(result.Success);
        Assert.Contains("valid IP", result.Error);
    }

    [Fact]
    public async Task ReverseAsync_BuildsPtrQueryAndParses()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var responder = Task.Run(async () =>
        {
            var received = await server.ReceiveAsync(cts.Token);
            var query = received.Buffer;
            // Verify the question encodes in-addr.arpa
            var text = Encoding.ASCII.GetString(query);
            Assert.Contains("in-addr", text);
            var response = new List<byte>(query);
            response[2] = 0x81;
            response[3] = 0x80;
            response[7] = 1;
            response.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x0C, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x08 });
            var target = Encoding.ASCII.GetBytes("\x03nas\x04home\x03lan\x00");
            response.AddRange(target);
            await server.SendAsync(response.ToArray(), response.Count, received.RemoteEndPoint);
        }, cts.Token);

        var resolver = new DnsResolver(() => new[] { "127.0.0.1" });
        var result = await resolver.ReverseAsync("192.168.1.10", $"127.0.0.1:{port}", 3000);
        await responder;
        Assert.True(result.Success, result.Error);
        Assert.Equal("nas.home.lan", Assert.Single(result.Answers).Value);
    }
}

public sealed class ScannerSafetyTests
{
    [Fact]
    public async Task Scan_RejectsPublicRangeByDefault()
    {
        var scanner = new ScannerService(OuiDatabase.CreateWithSeed(), new DnsResolver(() => Array.Empty<string>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync(new ScanRequest
        {
            Targets = new[] { "8.8.8.8" },
            Options = new ScanOptions { EnableArp = false, EnableIcmp = true, EnableMdns = false, EnableTcp = false, EnableNetBios = false }
        }));
    }

    [Fact]
    public async Task Scan_CancelledTokenReturnsCancelledResult()
    {
        var scanner = new ScannerService(OuiDatabase.CreateWithSeed(), new DnsResolver(() => Array.Empty<string>()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await scanner.ScanAsync(new ScanRequest
        {
            Targets = new[] { "192.168.250.0/28" },
            Options = new ScanOptions
            {
                EnableArp = false, EnableIcmp = true, EnableTcp = false, EnableMdns = false,
                EnableNetBios = false, EnableReverseDns = false, Concurrency = 4
            }
        }, null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Empty(result.Hosts);
    }

    [Fact]
    public async Task Scan_RejectsEmptyTargets()
    {
        var scanner = new ScannerService(OuiDatabase.CreateWithSeed(), new DnsResolver(() => Array.Empty<string>()));
        await Assert.ThrowsAsync<ArgumentException>(() => scanner.ScanAsync(new ScanRequest { Targets = Array.Empty<string>() }));
    }

    [Fact]
    public void ScanOptions_ParsesPorts()
    {
        Assert.Equal(new[] { 80, 443, 22 }, ScanOptions.ParsePorts("80, 443;22,80,70000,bad"));
        Assert.Empty(ScanOptions.ParsePorts(null));
    }

    [Fact]
    public async Task Scan_LocalhostPingWorks()
    {
        var ping = await PingService.PingAsync("127.0.0.1", 2000, 1);
        Assert.True(ping.Success, ping.Error);
        Assert.NotNull(ping.RoundtripMs);
    }

    [Fact]
    public void NetworkInterfaces_ReturnsAtLeastOne()
    {
        var interfaces = NetworkInterfaceService.GetActive();
        Assert.NotEmpty(interfaces);
        Assert.All(interfaces, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }
}
