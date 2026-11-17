using IPManager.Core;
using IPManager.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public sealed record ChartSlice(string Label, double Value, string? Detail = null);

public sealed record RecentActivityItem(DateTime Timestamp, string Category, string Title, string Detail, AlertSeverity Severity);

public sealed record DashboardSnapshot
{
    public DateTime GeneratedAt { get; init; }
    public int SubnetCount { get; init; }
    public int DeviceCount { get; init; }
    public int OnlineDevices { get; init; }
    public int OfflineDevices { get; init; }
    public int UnknownStatusDevices { get; init; }
    public int UnknownCategoryDevices { get; init; }
    public int ReservedAddresses { get; init; }
    public long TotalManagedAddresses { get; init; }
    public int AvailableAddresses { get; init; }
    public int ActiveConflicts { get; init; }
    public int RecentNewDevices { get; init; }
    public int UnacknowledgedAlerts { get; init; }
    public IReadOnlyList<ChartSlice> Allocation { get; init; } = Array.Empty<ChartSlice>();
    public IReadOnlyList<ChartSlice> DeviceStatus { get; init; } = Array.Empty<ChartSlice>();
    public IReadOnlyList<ChartSlice> Manufacturers { get; init; } = Array.Empty<ChartSlice>();
    public IReadOnlyList<ChartSlice> Categories { get; init; } = Array.Empty<ChartSlice>();
    public IReadOnlyList<ChartSlice> OperatingSystems { get; init; } = Array.Empty<ChartSlice>();
    public IReadOnlyList<ChartSlice> SubnetUtilization { get; init; } = Array.Empty<ChartSlice>();
    public IReadOnlyList<RecentActivityItem> Activity { get; init; } = Array.Empty<RecentActivityItem>();
    public IReadOnlyList<Device> RecentDevices { get; init; } = Array.Empty<Device>();

    public double OnlinePercent => DeviceCount == 0 ? 0 : Math.Round(OnlineDevices * 100.0 / DeviceCount, 1);
}

public sealed class DashboardService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly InventoryService _inventory;
    private readonly IClock _clock;

    public DashboardService(IDbContextFactory<IpManagerDbContext> factory, InventoryService inventory, IClock clock)
    {
        _factory = factory;
        _inventory = inventory;
        _clock = clock;
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);

        var subnets = await _inventory.GetSubnetsAsync(ct);
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        var conflicts = await db.IpConflicts.AsNoTracking().Where(c => c.State != ConflictState.Resolved).ToListAsync(ct);
        var entries = await db.IpAddresses.AsNoTracking().ToListAsync(ct);
        var alerts = await db.Alerts.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(20).ToListAsync(ct);
        var scans = await db.ScanSessions.AsNoTracking().OrderByDescending(s => s.StartedAt).Take(5).ToListAsync(ct);
        var history = await db.DeviceHistory.AsNoTracking().OrderByDescending(h => h.ChangedAt).Take(10).ToListAsync(ct);

        var utilizations = new List<SubnetUtilization>();
        foreach (var subnet in subnets)
            utilizations.Add(await _inventory.GetUtilizationAsync(subnet, ct));

        var totalHosts = utilizations.Sum(u => u.TotalHosts);
        var available = (int)Math.Min(int.MaxValue, utilizations.Sum(u => (long)u.Available));
        var assigned = utilizations.Sum(u => u.Used);
        var allocation = new List<ChartSlice>
        {
            new("Assigned", assigned),
            new("Available", available),
            new("Reserved", utilizations.Sum(u => u.Used) == 0 ? 0 : entries.Count(e => e.Status == IpStatus.Reserved)),
            new("Excluded", entries.Count(e => e.Status == IpStatus.Excluded)),
            new("Conflict", conflicts.Count)
        };

        var statusSlices = new List<ChartSlice>
        {
            new("Online", devices.Count(d => d.Status == DeviceStatus.Online)),
            new("Offline", devices.Count(d => d.Status == DeviceStatus.Offline)),
            new("Unknown", devices.Count(d => d.Status == DeviceStatus.Unknown))
        };

        var manufacturers = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.Manufacturer))
            .GroupBy(d => d.Manufacturer!)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => new ChartSlice(g.Key, g.Count()))
            .ToList();
        if (devices.Any(d => string.IsNullOrWhiteSpace(d.Manufacturer)))
            manufacturers.Add(new ChartSlice("Unknown vendor", devices.Count(d => string.IsNullOrWhiteSpace(d.Manufacturer))));

        var categories = devices
            .GroupBy(d => d.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new ChartSlice(DeviceClassifier.DisplayName(g.Key), g.Count()))
            .ToList();

        var os = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.OperatingSystem))
            .GroupBy(d => d.OperatingSystem!)
            .OrderByDescending(g => g.Count())
            .Select(g => new ChartSlice(g.Key, g.Count()))
            .ToList();
        os.Add(new ChartSlice("Not identified", devices.Count(d => string.IsNullOrWhiteSpace(d.OperatingSystem))));

        var subnetSlices = subnets.Select((s, i) => new ChartSlice(s.Name, utilizations.ElementAtOrDefault(i)?.Percent ?? 0,
            s.Cidr)).ToList();

        var activity = new List<RecentActivityItem>();
        activity.AddRange(alerts.Select(a => new RecentActivityItem(a.CreatedAt, "Alert", a.Title, a.Message, a.Severity)));
        activity.AddRange(scans.Select(s => new RecentActivityItem(s.StartedAt, "Scan",
            s.Status == ScanStatus.Completed ? "Network scan completed" : $"Network scan {s.Status}",
            $"{s.AddressesChecked:N0} addresses checked, {s.DevicesFound} devices found, {s.Targets}", s.Status == ScanStatus.Failed ? AlertSeverity.Warning : AlertSeverity.Info)));
        foreach (var h in history)
        {
            activity.Add(new RecentActivityItem(h.ChangedAt, "Change", $"{h.Field} changed",
                $"{h.OldValue ?? "(empty)"} -> {h.NewValue ?? "(empty)"}", AlertSeverity.Info));
        }

        return new DashboardSnapshot
        {
            GeneratedAt = now,
            SubnetCount = subnets.Count,
            DeviceCount = devices.Count,
            OnlineDevices = devices.Count(d => d.Status == DeviceStatus.Online),
            OfflineDevices = devices.Count(d => d.Status == DeviceStatus.Offline),
            UnknownStatusDevices = devices.Count(d => d.Status == DeviceStatus.Unknown),
            UnknownCategoryDevices = devices.Count(d => d.Category == DeviceCategory.Unknown),
            ReservedAddresses = entries.Count(e => e.Status == IpStatus.Reserved),
            TotalManagedAddresses = totalHosts,
            AvailableAddresses = available,
            ActiveConflicts = conflicts.Count,
            RecentNewDevices = devices.Count(d => d.FirstSeen >= now.AddDays(-7)),
            UnacknowledgedAlerts = await db.Alerts.CountAsync(a => !a.IsAcknowledged, ct),
            Allocation = allocation.Where(x => x.Value > 0).ToList(),
            DeviceStatus = statusSlices.Where(x => x.Value > 0).ToList(),
            Manufacturers = manufacturers,
            Categories = categories,
            OperatingSystems = os,
            SubnetUtilization = subnetSlices,
            Activity = activity.OrderByDescending(a => a.Timestamp).Take(25).ToList(),
            RecentDevices = devices.OrderByDescending(d => d.FirstSeen).Take(10).ToList()
        };
    }
}
