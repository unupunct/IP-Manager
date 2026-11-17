using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public enum ReportKind
{
    NetworkInventory,
    SubnetUtilization,
    Dhcp,
    IpConflicts,
    Dns,
    Availability
}

public enum ExportFormat
{
    Csv,
    Json,
    Pdf
}

public sealed class ReportFilter
{
    public int? SubnetId { get; set; }
    public DeviceCategory? Category { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public sealed class ReportsService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly InventoryService _inventory;
    private readonly AppPaths _paths;
    private readonly IClock _clock;

    public ReportsService(IDbContextFactory<IpManagerDbContext> factory, InventoryService inventory, AppPaths paths, IClock clock)
    {
        _factory = factory;
        _inventory = inventory;
        _paths = paths;
        _clock = clock;
    }

    public async Task<ReportTable> BuildAsync(ReportKind kind, ReportFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new ReportFilter();
        return kind switch
        {
            ReportKind.NetworkInventory => await BuildInventoryAsync(filter, ct),
            ReportKind.SubnetUtilization => await BuildSubnetUtilizationAsync(filter, ct),
            ReportKind.Dhcp => await BuildDhcpAsync(filter, ct),
            ReportKind.IpConflicts => await BuildConflictsAsync(filter, ct),
            ReportKind.Dns => await BuildDnsAsync(filter, ct),
            ReportKind.Availability => await BuildAvailabilityAsync(filter, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public async Task<List<ReportTable>> BuildAllAsync(ReportFilter? filter = null, CancellationToken ct = default)
    {
        var list = new List<ReportTable>();
        foreach (var kind in Enum.GetValues<ReportKind>())
            list.Add(await BuildAsync(kind, filter, ct));
        return list;
    }

    private async Task<ReportTable> BuildInventoryAsync(ReportFilter filter, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.Devices.AsNoTracking().Include(d => d.Subnet).AsQueryable();
        if (filter.SubnetId is not null) q = q.Where(d => d.SubnetId == filter.SubnetId);
        if (filter.Category is not null) q = q.Where(d => d.Category == filter.Category);
        if (filter.From is not null) q = q.Where(d => d.FirstSeen >= filter.From);
        if (filter.To is not null) q = q.Where(d => d.FirstSeen <= filter.To);
        var devices = await q.OrderBy(d => d.Name).ToListAsync(ct);

        return new ReportTable
        {
            Title = "Network inventory",
            Subtitle = "All documented and discovered devices",
            Headers = new[] { "Name", "IP address", "MAC address", "Manufacturer", "Category", "OS", "Hostname", "Subnet", "Status", "Trusted", "Last seen", "Location", "Notes" },
            Rows = devices.Select(d => new[]
            {
                d.Name, d.IpAddress ?? "", d.MacAddress ?? "", d.Manufacturer ?? "",
                DeviceClassifier.DisplayName(d.Category), d.OperatingSystem ?? "", d.Hostname ?? "",
                d.Subnet?.Cidr ?? "", d.Status.ToString(), d.IsTrusted ? "Yes" : "No",
                d.LastSeen.ToString("yyyy-MM-dd HH:mm"), d.Location ?? "", d.Notes ?? ""
            }).ToList(),
            Metadata = new Dictionary<string, string>
            {
                ["Devices"] = devices.Count.ToString(),
                ["Filter subnet"] = filter.SubnetId?.ToString() ?? "all",
                ["Filter category"] = filter.Category?.ToString() ?? "all"
            }
        };
    }

    private async Task<ReportTable> BuildSubnetUtilizationAsync(ReportFilter filter, CancellationToken ct)
    {
        var subnets = await _inventory.GetSubnetsAsync(ct);
        if (filter.SubnetId is not null) subnets = subnets.Where(s => s.Id == filter.SubnetId).ToList();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var subnet in subnets)
        {
            var u = await _inventory.GetUtilizationAsync(subnet, ct);
            rows.Add(new[]
            {
                subnet.Name, subnet.Cidr, u.TotalHosts.ToString(), u.Used.ToString(), u.Available.ToString(),
                $"{u.Percent:0.0}%", u.DhcpPoolSize.ToString(), $"{u.DhcpPercent:0.0}%",
                subnet.Gateway ?? "", subnet.DhcpStart is null ? "" : $"{subnet.DhcpStart}-{subnet.DhcpEnd}",
                subnet.Description ?? ""
            });
        }

        return new ReportTable
        {
            Title = "Subnet utilization",
            Subtitle = "Address usage per managed subnet",
            Headers = new[] { "Subnet", "CIDR", "Total hosts", "Used", "Available", "Used %", "DHCP pool", "DHCP used %", "Gateway", "DHCP range", "Description" },
            Rows = rows
        };
    }

    private async Task<ReportTable> BuildDhcpAsync(ReportFilter filter, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.DhcpScopes.AsNoTracking().Include(s => s.Subnet).Include(s => s.Reservations).AsQueryable();
        if (filter.SubnetId is not null) q = q.Where(s => s.SubnetId == filter.SubnetId);
        var scopes = await q.OrderBy(s => s.Name).ToListAsync(ct);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var scope in scopes)
        {
            var poolSize = 0L;
            if (Ipv4Address.TryParse(scope.StartAddress, out var s) && Ipv4Address.TryParse(scope.EndAddress, out var e) && e.Value >= s.Value)
                poolSize = e.Value - s.Value + 1;

            rows.Add(new[]
            {
                "Scope", scope.Name, scope.Provider, scope.ServerAddress ?? "", $"{scope.StartAddress}-{scope.EndAddress}",
                poolSize.ToString(), scope.Reservations.Count.ToString(), $"{scope.LeaseMinutes} min",
                scope.Subnet?.Cidr ?? "", scope.Notes ?? ""
            });

            foreach (var r in scope.Reservations.OrderBy(r => r.IpAddress))
            {
                rows.Add(new[]
                {
                    "Reservation", r.Hostname ?? "", r.Source, "", r.IpAddress, "", "", "", r.MacAddress, r.Description ?? ""
                });
            }
        }

        return new ReportTable
        {
            Title = "DHCP documentation",
            Subtitle = "Documented scopes and reservations (not live router configuration unless an integration is connected)",
            Headers = new[] { "Type", "Name / host", "Provider / source", "Server", "Range / IP", "Pool size", "Reservations", "Lease", "Subnet", "MAC / notes" },
            Rows = rows,
            Metadata = new Dictionary<string, string> { ["Scopes"] = scopes.Count.ToString() }
        };
    }

    private async Task<ReportTable> BuildConflictsAsync(ReportFilter filter, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.IpConflicts.AsNoTracking().AsQueryable();
        if (filter.SubnetId is not null) q = q.Where(c => c.SubnetId == filter.SubnetId);
        if (filter.From is not null) q = q.Where(c => c.DetectedAt >= filter.From);
        if (filter.To is not null) q = q.Where(c => c.DetectedAt <= filter.To);
        var conflicts = await q.OrderByDescending(c => c.LastSeenAt).ToListAsync(ct);

        return new ReportTable
        {
            Title = "IP conflict report",
            Subtitle = "Confirmed and suspected conflicts with evidence",
            Headers = new[] { "IP address", "State", "Confidence", "Method", "MAC addresses", "Detected", "Last seen", "Resolved", "Evidence" },
            Rows = conflicts.Select(c => new[]
            {
                c.IpAddress, c.State.ToString(), c.Confidence + "%", c.Method.ToString(), c.MacAddresses,
                c.DetectedAt.ToString("yyyy-MM-dd HH:mm"), c.LastSeenAt.ToString("yyyy-MM-dd HH:mm"),
                c.ResolvedAt?.ToString("yyyy-MM-dd HH:mm") ?? "", c.Evidence
            }).ToList(),
            Metadata = new Dictionary<string, string>
            {
                ["Confirmed"] = conflicts.Count(c => c.State == ConflictState.Confirmed).ToString(),
                ["Suspected"] = conflicts.Count(c => c.State == ConflictState.Suspected).ToString()
            }
        };
    }

    private async Task<ReportTable> BuildDnsAsync(ReportFilter filter, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var docs = await db.DnsRecords.AsNoTracking().OrderBy(r => r.Zone).ThenBy(r => r.Name).ToListAsync(ct);
        var servers = NetworkInterfaceService.GetSystemDnsServers();

        var rows = new List<IReadOnlyList<string>>();
        foreach (var s in servers)
            rows.Add(new[] { "System resolver", s, "", "", "", "", "DNS server detected on this computer" });
        foreach (var d in docs)
            rows.Add(new[] { "Documented record", d.Server ?? "", d.Zone, d.Name, d.Type, d.Value, d.Notes ?? "" });

        return new ReportTable
        {
            Title = "DNS configuration",
            Subtitle = "Detected DNS servers and locally documented records",
            Headers = new[] { "Source", "Server", "Zone", "Name", "Type", "Value", "Notes" },
            Rows = rows,
            Metadata = new Dictionary<string, string> { ["Documented records"] = docs.Count.ToString() }
        };
    }

    private async Task<ReportTable> BuildAvailabilityAsync(ReportFilter filter, CancellationToken ct)
    {
        var to = filter.To ?? _clock.UtcNow;
        var from = filter.From ?? to.AddDays(-7);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var devicesQuery = db.Devices.AsNoTracking().Where(d => !d.ExcludeFromMonitoring);
        if (filter.SubnetId is not null) devicesQuery = devicesQuery.Where(d => d.SubnetId == filter.SubnetId);
        if (filter.Category is not null) devicesQuery = devicesQuery.Where(d => d.Category == filter.Category);
        var devices = await devicesQuery.OrderBy(d => d.Name).ToListAsync(ct);
        var deviceIds = devices.Select(d => d.Id).ToList();
        var results = await db.MonitoringResults.AsNoTracking()
            .Where(r => deviceIds.Contains(r.DeviceId) && r.Timestamp >= from && r.Timestamp <= to)
            .ToListAsync(ct);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var device in devices)
        {
            var deviceResults = results.Where(r => r.DeviceId == device.Id).ToList();
            var success = deviceResults.Count(r => r.Success);
            var percent = deviceResults.Count == 0 ? "no data" : $"{success * 100.0 / deviceResults.Count:0.0}%";
            var avg = deviceResults.Where(r => r.Success && r.ResponseMs is not null).Select(r => r.ResponseMs!.Value).DefaultIfEmpty(0).Average();
            rows.Add(new[]
            {
                device.Name, device.IpAddress ?? "", DeviceClassifier.DisplayName(device.Category), device.Status.ToString(),
                deviceResults.Count.ToString(), success.ToString(), (deviceResults.Count - success).ToString(),
                percent, deviceResults.Count == 0 ? "" : $"{avg:0} ms",
                deviceResults.Count == 0 ? "" : deviceResults.Max(r => r.Timestamp).ToString("yyyy-MM-dd HH:mm")
            });
        }

        return new ReportTable
        {
            Title = "Network availability",
            Subtitle = $"Monitoring observations from {from:yyyy-MM-dd} to {to:yyyy-MM-dd} (missing data is not counted as downtime)",
            Headers = new[] { "Device", "IP address", "Category", "State", "Checks", "Success", "Failures", "Uptime", "Avg response", "Last check" },
            Rows = rows,
            Metadata = new Dictionary<string, string>
            {
                ["From"] = from.ToString("yyyy-MM-dd HH:mm"),
                ["To"] = to.ToString("yyyy-MM-dd HH:mm"),
                ["Observations"] = results.Count.ToString()
            }
        };
    }

    public async Task<string> ExportAsync(ReportTable table, ExportFormat format, string? path = null, CancellationToken ct = default)
    {
        var name = SafeName(table.Title) + "-" + _clock.UtcNow.ToString("yyyyMMdd-HHmmss");
        path ??= Path.Combine(_paths.ExportDirectory, name + format switch
        {
            ExportFormat.Csv => ".csv",
            ExportFormat.Json => ".json",
            _ => ".pdf"
        });

        switch (format)
        {
            case ExportFormat.Csv: ReportExporter.WriteCsv(table, path); break;
            case ExportFormat.Json: ReportExporter.WriteJson(table, path); break;
            case ExportFormat.Pdf: ReportExporter.WritePdf(table, path); break;
        }
        return path;
    }

    private static string SafeName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clipped = new string(input.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Replace(' ', '-');
        return string.IsNullOrWhiteSpace(clipped) ? "report" : clipped;
    }
}
