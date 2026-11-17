using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public sealed class SubnetSaveResult
{
    public required Subnet Subnet { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class AddressMapRow
{
    public int? EntryId { get; init; }
    public int? DeviceId { get; init; }
    public required string Address { get; init; }
    public IpStatus Status { get; init; }
    public AllocationType Allocation { get; init; }
    public string? DeviceName { get; init; }
    public DeviceStatus? DeviceStatus { get; init; }
    public string? MacAddress { get; init; }
    public string? Hostname { get; init; }
    public string? Description { get; init; }
    public string? Notes { get; init; }
    public bool IsManuallyManaged { get; init; }
    public DateTime? LastSeen { get; init; }
    public DateTime? LastModified { get; init; }
    public bool HasConflict { get; init; }
    public string? ConflictState { get; init; }
    public bool InDhcpRange { get; init; }
    public bool InStaticRange { get; init; }
    public string Source { get; init; } = "";
}

public sealed class AddressMapResult
{
    public IReadOnlyList<AddressMapRow> Rows { get; init; } = Array.Empty<AddressMapRow>();
    public long TotalHosts { get; init; }
    public bool Truncated { get; init; }
    public int AssignedCount { get; init; }
    public int ReservedCount { get; init; }
    public int ExcludedCount { get; init; }
    public int AvailableCount { get; init; }
    public int ConflictCount { get; init; }
    public int GatewayCount { get; init; }
    public int UnknownCount { get; init; }
    public double UtilizationPercent => TotalHosts <= 0 ? 0 : Math.Round((AssignedCount + ReservedCount + GatewayCount + ConflictCount) * 100.0 / TotalHosts, 1);
}

public sealed class SubnetUtilization
{
    public int SubnetId { get; init; }
    public string Name { get; init; } = "";
    public string Cidr { get; init; } = "";
    public long TotalHosts { get; init; }
    public int Used { get; init; }
    public int Available { get; init; }
    public double Percent { get; init; }
    public int DhcpPoolSize { get; init; }
    public int DhcpUsed { get; init; }
    public double DhcpPercent { get; init; }
    public bool DhcpPoolOverlapsStatic { get; init; }
    public bool DhcpPoolExhausted { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class DiscoveryApplyResult
{
    public int NewDevices { get; init; }
    public int UpdatedDevices { get; init; }
    public int NewAddresses { get; init; }
    public int ObservationsRecorded { get; init; }
    public IReadOnlyList<Device> NewDeviceList { get; init; } = Array.Empty<Device>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class DuplicateAssignmentException : Exception
{
    public DuplicateAssignmentException(string message) : base(message) { }
}

public sealed class InventoryService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly IAlertService? _alerts;
    private readonly OuiDatabase _oui;

    public InventoryService(IDbContextFactory<IpManagerDbContext> factory, IClock clock, IAuditService audit,
        OuiDatabase oui, IAlertService? alerts = null)
    {
        _factory = factory;
        _clock = clock;
        _audit = audit;
        _oui = oui;
        _alerts = alerts;
    }

    // ---------------------------------------------------------------- subnets

    public async Task<List<Subnet>> GetSubnetsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Subnets.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<Subnet?> GetSubnetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Subnets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<SubnetSaveResult> SaveSubnetAsync(Subnet input, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (!Ipv4Network.TryParseCidr($"{input.NetworkAddress}/{input.PrefixLength}", out var network, out var parseError))
            throw new ArgumentException(string.IsNullOrWhiteSpace(parseError) ? "The network address or prefix is invalid." : parseError);

        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("A subnet name is required.");

        if (Ipv4Address.TryParse(input.NetworkAddress, out var raw) && raw != network.Network)
        {
            warnings.Add($"The network address was normalized from {input.NetworkAddress}/{input.PrefixLength} to {network.Cidr}.");
            input.NetworkAddress = network.Network.ToString();
        }

        if (network.PrefixLength > 30 && string.IsNullOrWhiteSpace(input.Gateway))
            warnings.Add($"A /{network.PrefixLength} network has no conventional broadcast address; verify the gateway.");

        if (!string.IsNullOrWhiteSpace(input.Gateway))
        {
            if (!Ipv4Address.TryParse(input.Gateway, out var gw))
                throw new ArgumentException("The gateway is not a valid IPv4 address.");
            if (!network.Contains(gw))
                throw new ArgumentException($"The gateway {input.Gateway} is outside the subnet {network.Cidr}.");
            if (network.PrefixLength <= 30 && (gw == network.Network || gw == network.Broadcast))
                throw new ArgumentException("The gateway cannot be the network or broadcast address.");
        }

        ValidateRange(input.DhcpStart, input.DhcpEnd, network, "DHCP", warnings);
        ValidateRange(input.StaticStart, input.StaticEnd, network, "static", warnings);

        if (!string.IsNullOrWhiteSpace(input.DhcpStart) && !string.IsNullOrWhiteSpace(input.DhcpEnd) &&
            !string.IsNullOrWhiteSpace(input.StaticStart) && !string.IsNullOrWhiteSpace(input.StaticEnd))
        {
            var dhcp = new Ipv4Range(Ipv4Address.Parse(input.DhcpStart), Ipv4Address.Parse(input.DhcpEnd));
            var stat = new Ipv4Range(Ipv4Address.Parse(input.StaticStart), Ipv4Address.Parse(input.StaticEnd));
            if (dhcp.Overlaps(stat))
                warnings.Add("The DHCP range overlaps the static allocation range.");
        }

        foreach (var excluded in ParseAddressList(input.ExcludedAddresses))
        {
            if (!network.Contains(excluded))
                throw new ArgumentException($"Excluded address {excluded} is outside {network.Cidr}.");
        }

        await using var db = await _factory.CreateDbContextAsync(ct);

        var overlap = await db.Subnets.AsNoTracking()
            .Where(s => s.Id != input.Id)
            .ToListAsync(ct);
        foreach (var other in overlap)
        {
            if (other.NetworkOrNull is { } otherNet && otherNet.Overlaps(network))
                warnings.Add($"The subnet overlaps '{other.Name}' ({other.Cidr}). Overlapping networks are legal but assignments may be ambiguous.");
        }

        var duplicate = await db.Subnets.AsNoTracking()
            .AnyAsync(s => s.Id != input.Id && s.NetworkAddress == input.NetworkAddress && s.PrefixLength == input.PrefixLength, ct);
        if (duplicate)
            throw new ArgumentException($"A subnet with {network.Cidr} already exists.");

        var now = _clock.UtcNow;
        Subnet entity;
        if (input.Id == 0)
        {
            input.CreatedAt = now;
            input.UpdatedAt = now;
            db.Subnets.Add(input);
            entity = input;
        }
        else
        {
            entity = await db.Subnets.FirstOrDefaultAsync(s => s.Id == input.Id, ct)
                     ?? throw new InvalidOperationException("The subnet no longer exists.");
            entity.Name = input.Name;
            entity.NetworkAddress = input.NetworkAddress;
            entity.PrefixLength = input.PrefixLength;
            entity.Gateway = input.Gateway;
            entity.DnsServers = input.DnsServers;
            entity.VlanId = input.VlanId;
            entity.Location = input.Location;
            entity.Description = input.Description;
            entity.DhcpStart = input.DhcpStart;
            entity.DhcpEnd = input.DhcpEnd;
            entity.StaticStart = input.StaticStart;
            entity.StaticEnd = input.StaticEnd;
            entity.ExcludedAddresses = input.ExcludedAddresses;
            entity.MonitoringEnabled = input.MonitoringEnabled;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("Subnet", input.Id == 0 ? "Created subnet" : "Updated subnet", $"{entity.Name} {network.Cidr}", ct);
        return new SubnetSaveResult { Subnet = entity, Warnings = warnings };
    }

    public async Task DeleteSubnetAsync(int subnetId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var subnet = await db.Subnets.FirstOrDefaultAsync(s => s.Id == subnetId, ct)
                     ?? throw new InvalidOperationException("The subnet no longer exists.");

        var addresses = await db.IpAddresses.Where(a => a.SubnetId == subnetId).ToListAsync(ct);
        db.IpAddresses.RemoveRange(addresses);
        var devices = await db.Devices.Where(d => d.SubnetId == subnetId).ToListAsync(ct);
        foreach (var d in devices) d.SubnetId = null;
        db.Subnets.Remove(subnet);
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("Subnet", "Deleted subnet", subnet.Cidr, ct);
    }

    public async Task<List<SubnetUtilization>> GetUtilizationAsync(CancellationToken ct = default)
    {
        var subnets = await GetSubnetsAsync(ct);
        var result = new List<SubnetUtilization>();
        foreach (var subnet in subnets)
            result.Add(await GetUtilizationAsync(subnet, ct));
        return result;
    }

    public async Task<SubnetUtilization> GetUtilizationAsync(Subnet subnet, CancellationToken ct = default)
    {
        var map = await GetAddressMapAsync(subnet, ct: ct);
        var warnings = new List<string>();
        long poolSize = 0;
        var poolUsed = 0;
        var overlapsStatic = false;

        if (subnet.NetworkOrNull is { } network)
        {
            if (Ipv4Address.TryParse(subnet.DhcpStart, out var ds) && Ipv4Address.TryParse(subnet.DhcpEnd, out var de) && ds.Value <= de.Value)
            {
                poolSize = de.Value - ds.Value + 1;
                poolUsed = map.Rows.Count(r => Ipv4Address.TryParse(r.Address, out var a) && a.Value >= ds.Value && a.Value <= de.Value &&
                                               r.Status is IpStatus.Assigned or IpStatus.Reserved or IpStatus.Conflict);
            }

            if (!string.IsNullOrWhiteSpace(subnet.StaticStart) && !string.IsNullOrWhiteSpace(subnet.StaticEnd) &&
                Ipv4Address.TryParse(subnet.StaticStart, out var ss) && Ipv4Address.TryParse(subnet.StaticEnd, out var se) &&
                Ipv4Address.TryParse(subnet.DhcpStart, out var ds2) && Ipv4Address.TryParse(subnet.DhcpEnd, out var de2))
            {
                overlapsStatic = new Ipv4Range(ds2, de2).Overlaps(new Ipv4Range(ss, se));
            }
        }

        var dhcpPercent = poolSize > 0 ? Math.Round(poolUsed * 100.0 / poolSize, 1) : 0;
        if (map.UtilizationPercent >= 85) warnings.Add($"Subnet utilization is {map.UtilizationPercent}%.");
        if (overlapsStatic) warnings.Add("The DHCP pool overlaps the static allocation range.");
        if (poolSize > 0 && dhcpPercent >= 90) warnings.Add($"The DHCP pool is {dhcpPercent}% used.");
        if (subnet.Gateway is not null && subnet.NetworkOrNull is { } n && Ipv4Address.TryParse(subnet.Gateway, out var g) && !n.Contains(g))
            warnings.Add("The gateway is outside the subnet.");

        return new SubnetUtilization
        {
            SubnetId = subnet.Id,
            Name = subnet.Name,
            Cidr = subnet.Cidr,
            TotalHosts = map.TotalHosts,
            Used = map.AssignedCount + map.ReservedCount + map.GatewayCount + map.ConflictCount,
            Available = map.AvailableCount,
            Percent = map.UtilizationPercent,
            DhcpPoolSize = (int)poolSize,
            DhcpUsed = poolUsed,
            DhcpPercent = dhcpPercent,
            DhcpPoolOverlapsStatic = overlapsStatic,
            DhcpPoolExhausted = poolSize > 0 && dhcpPercent >= 100,
            Warnings = warnings
        };
    }

    // ---------------------------------------------------------------- address map

    public async Task<AddressMapResult> GetAddressMapAsync(Subnet subnet, int maxRows = 4096, CancellationToken ct = default)
    {
        if (subnet.NetworkOrNull is not { } network)
            return new AddressMapResult();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var entries = await db.IpAddresses.AsNoTracking()
            .Where(a => a.SubnetId == subnet.Id)
            .ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.SubnetId == subnet.Id && d.IpAddress != null)
            .ToListAsync(ct);
        var conflicts = await db.IpConflicts.AsNoTracking()
            .Where(c => c.SubnetId == subnet.Id && c.State != ConflictState.Resolved)
            .ToListAsync(ct);

        var entryByIp = entries.GroupBy(e => e.Address).ToDictionary(g => g.Key, g => g.First());
        var deviceByIp = devices.Where(d => d.IpAddress is not null)
            .GroupBy(d => d.IpAddress!)
            .ToDictionary(g => g.Key, g => g.First());
        var conflictByIp = conflicts.GroupBy(c => c.IpAddress).ToDictionary(g => g.Key, g => g.First());
        var excluded = ParseAddressList(subnet.ExcludedAddresses).Select(a => a.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = new List<AddressMapRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRow(Ipv4Address address, int? entryId = null, IpStatus? forced = null)
        {
            var ip = address.ToString();
            if (!seen.Add(ip)) return;
            entryByIp.TryGetValue(ip, out var entry);
            deviceByIp.TryGetValue(ip, out var device);
            conflictByIp.TryGetValue(ip, out var conflict);

            var status = forced ?? entry?.Status ?? IpStatus.Available;
            if (conflict is not null && conflict.State != ConflictState.Resolved) status = IpStatus.Conflict;
            else if (excluded.Contains(ip)) status = IpStatus.Excluded;
            else if (subnet.Gateway == ip) status = IpStatus.Gateway;
            else if (entry is not null) status = entry.Status;
            else if (device is not null) status = IpStatus.Assigned;

            rows.Add(new AddressMapRow
            {
                EntryId = entry?.Id,
                DeviceId = device?.Id ?? entry?.DeviceId,
                Address = ip,
                Status = status,
                Allocation = entry?.AllocationType ?? AllocationType.Unknown,
                DeviceName = device?.Name ?? entry?.Hostname,
                DeviceStatus = device?.Status,
                MacAddress = device?.MacAddress ?? entry?.MacAddress,
                Hostname = device?.IpAddress is null ? entry?.Hostname : device.Hostname ?? device.IpAddress,
                Description = entry?.Description,
                Notes = entry?.Notes,
                IsManuallyManaged = entry?.IsManuallyManaged ?? device?.IsManuallyManaged ?? false,
                LastSeen = device?.LastSeen,
                LastModified = entry?.UpdatedAt,
                HasConflict = conflict is not null,
                ConflictState = conflict?.State.ToString(),
                InDhcpRange = InRange(ip, subnet.DhcpStart, subnet.DhcpEnd),
                InStaticRange = InRange(ip, subnet.StaticStart, subnet.StaticEnd),
                Source = entry?.IsManuallyManaged == true ? "Manual" : device?.DiscoverySource ?? (entry is null ? "" : "Documented")
            });
        }

        if (network.UsableHosts <= maxRows)
        {
            foreach (var host in network.Hosts(maxRows + 1)) AddRow(host);
        }
        else
        {
            if (subnet.Gateway is not null && Ipv4Address.TryParse(subnet.Gateway, out var g) && network.Contains(g)) AddRow(g);
            foreach (var entry in entries) if (Ipv4Address.TryParse(entry.Address, out var a)) AddRow(a);
            foreach (var device in devices) if (Ipv4Address.TryParse(device.IpAddress, out var a)) AddRow(a);
            foreach (var conflict in conflicts) if (Ipv4Address.TryParse(conflict.IpAddress, out var a)) AddRow(a);
        }

        var truncated = network.UsableHosts > maxRows;
        var ordered = rows.OrderBy(r => Ipv4Address.TryParse(r.Address, out var a) ? a.Value : 0).ToList();
        return new AddressMapResult
        {
            Rows = ordered,
            TotalHosts = network.UsableHosts,
            Truncated = truncated,
            AssignedCount = ordered.Count(r => r.Status == IpStatus.Assigned),
            ReservedCount = ordered.Count(r => r.Status == IpStatus.Reserved),
            ExcludedCount = ordered.Count(r => r.Status == IpStatus.Excluded),
            AvailableCount = ordered.Count(r => r.Status == IpStatus.Available),
            ConflictCount = ordered.Count(r => r.Status == IpStatus.Conflict),
            GatewayCount = ordered.Count(r => r.Status == IpStatus.Gateway),
            UnknownCount = ordered.Count(r => r.Status == IpStatus.Unknown)
        };
    }

    // ---------------------------------------------------------------- addresses

    public async Task<List<IpAddressEntry>> GetAddressesAsync(int? subnetId = null, string? search = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.IpAddresses.AsNoTracking().Include(a => a.Device).Include(a => a.Subnet).AsQueryable();
        if (subnetId is not null) q = q.Where(a => a.SubnetId == subnetId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(a => a.Address.Contains(s) || (a.Hostname != null && a.Hostname.Contains(s)) ||
                             (a.MacAddress != null && a.MacAddress.Contains(s)) ||
                             (a.Description != null && a.Description.Contains(s)) ||
                             (a.Device != null && a.Device.Name.Contains(s)));
        }
        return await q.OrderBy(a => a.Address).ToListAsync(ct);
    }

    public async Task<IpAddressEntry> SaveAddressAsync(IpAddressEntry input, CancellationToken ct = default)
    {
        if (!Ipv4Address.TryParse(input.Address, out var address))
            throw new ArgumentException("Enter a valid IPv4 address.");

        if (input.SubnetId is null)
            throw new ArgumentException("Select a subnet for this address.");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var subnet = await db.Subnets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == input.SubnetId, ct)
                     ?? throw new InvalidOperationException("The selected subnet no longer exists.");
        if (subnet.NetworkOrNull is not { } network)
            throw new InvalidOperationException("The selected subnet has an invalid network definition.");
        if (!network.Contains(address))
            throw new ArgumentException($"{input.Address} is outside {network.Cidr}.");

        if (input.Status is IpStatus.Assigned or IpStatus.Reserved or IpStatus.Gateway &&
            network.PrefixLength <= 30 && (address == network.Network || address == network.Broadcast))
            throw new ArgumentException($"{input.Address} is the network or broadcast address of {network.Cidr} and cannot be assigned.");

        if (input.Status is IpStatus.Assigned or IpStatus.Reserved or IpStatus.Gateway)
        {
            var clash = await db.IpAddresses.AsNoTracking()
                .Where(a => a.Id != input.Id && a.Address == input.Address &&
                            (a.Status == IpStatus.Assigned || a.Status == IpStatus.Reserved || a.Status == IpStatus.Gateway))
                .Join(db.Subnets, a => a.SubnetId, s => s.Id, (a, s) => new { a, s })
                .FirstOrDefaultAsync(ct);
            if (clash is not null)
                throw new DuplicateAssignmentException(
                    $"{input.Address} is already {(clash.a.Status == IpStatus.Gateway ? "the gateway" : clash.a.Status.ToString().ToLowerInvariant())} of subnet '{clash.s.Name}'. " +
                    "Assigning the same address twice is not allowed. Resolve the existing record first.");
        }

        var now = _clock.UtcNow;
        IpAddressEntry entity;
        if (input.Id == 0)
        {
            input.CreatedAt = now;
            input.UpdatedAt = now;
            input.IsManuallyManaged = true;
            db.IpAddresses.Add(input);
            entity = input;
        }
        else
        {
            entity = await db.IpAddresses.FirstOrDefaultAsync(a => a.Id == input.Id, ct)
                     ?? throw new InvalidOperationException("The address record no longer exists.");
            entity.Address = input.Address;
            entity.SubnetId = input.SubnetId;
            entity.DeviceId = input.DeviceId;
            entity.MacAddress = MacUtil.Normalize(input.MacAddress);
            entity.Hostname = input.Hostname;
            entity.AllocationType = input.AllocationType;
            entity.Status = input.Status;
            entity.Description = input.Description;
            entity.Notes = input.Notes;
            entity.UpdatedAt = now;
            entity.IsManuallyManaged = true;
        }

        await db.SaveChangesAsync(ct);

        if (entity.DeviceId is not null)
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == entity.DeviceId, ct);
            if (device is not null)
            {
                device.IpAddress = entity.Address;
                device.MacAddress ??= entity.MacAddress;
                await db.SaveChangesAsync(ct);
            }
        }

        await _audit.LogAsync("IP address", input.Id == 0 ? "Created address" : "Updated address",
            $"{entity.Address} {entity.Status} {entity.Description}", ct);
        return entity;
    }

    public async Task DeleteAddressAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.IpAddresses.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (entity is null) return;
        db.IpAddresses.Remove(entity);
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("IP address", "Deleted address", entity.Address, ct);
    }

    public async Task<IpAddressEntry?> FindAddressAsync(string address, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.IpAddresses.AsNoTracking().Include(a => a.Device)
            .FirstOrDefaultAsync(a => a.Address == address, ct);
    }

    // ---------------------------------------------------------------- devices

    public async Task<List<Device>> GetDevicesAsync(string? search = null, DeviceCategory? category = null,
        DeviceStatus? status = null, int? subnetId = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.Devices.AsNoTracking().Include(d => d.Subnet).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(d => d.Name.Contains(s) || (d.IpAddress != null && d.IpAddress.Contains(s)) ||
                             (d.MacAddress != null && d.MacAddress.Contains(s)) ||
                             (d.Hostname != null && d.Hostname.Contains(s)) ||
                             (d.Manufacturer != null && d.Manufacturer.Contains(s)) ||
                             (d.Tags != null && d.Tags.Contains(s)) ||
                             (d.Location != null && d.Location.Contains(s)));
        }
        if (category is not null) q = q.Where(d => d.Category == category);
        if (status is not null) q = q.Where(d => d.Status == status);
        if (subnetId is not null) q = q.Where(d => d.SubnetId == subnetId);
        return await q.OrderBy(d => d.Name).ToListAsync(ct);
    }

    public async Task<Device?> GetDeviceAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Devices.AsNoTracking()
            .Include(d => d.Subnet)
            .Include(d => d.MonitoringTargets)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task<List<DeviceHistory>> GetDeviceHistoryAsync(int deviceId, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceHistory.AsNoTracking()
            .Where(h => h.DeviceId == deviceId)
            .OrderByDescending(h => h.ChangedAt)
            .Take(Math.Clamp(limit, 1, 2000))
            .ToListAsync(ct);
    }

    public async Task<Device> SaveDeviceAsync(Device input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("A device name is required.");
        if (!string.IsNullOrWhiteSpace(input.IpAddress) && !Ipv4Address.TryParse(input.IpAddress, out _))
            throw new ArgumentException("The IP address is not valid IPv4.");
        if (!string.IsNullOrWhiteSpace(input.MacAddress) && !MacUtil.IsValid(input.MacAddress))
            throw new ArgumentException("The MAC address must contain 12 hexadecimal digits.");
        if (!string.IsNullOrWhiteSpace(input.MacAddress) && (MacUtil.IsMulticast(input.MacAddress) || input.MacAddress.Replace(":", "") == "FFFFFFFFFFFF"))
            throw new ArgumentException("A multicast or broadcast MAC address cannot identify a device.");

        input.MacAddress = MacUtil.Normalize(input.MacAddress);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = _clock.UtcNow;

        if (!string.IsNullOrWhiteSpace(input.IpAddress))
        {
            var duplicate = await db.Devices.AsNoTracking()
                .Where(d => d.Id != input.Id && d.IpAddress == input.IpAddress)
                .Select(d => d.Name)
                .FirstOrDefaultAsync(ct);
            if (duplicate is not null)
                throw new DuplicateAssignmentException($"{input.IpAddress} is already assigned to '{duplicate}'.");
        }

        if (input.SubnetId is null && Ipv4Address.TryParse(input.IpAddress, out var deviceIp))
        {
            var subnets = await db.Subnets.AsNoTracking().ToListAsync(ct);
            input.SubnetId = subnets.FirstOrDefault(s => s.NetworkOrNull?.Contains(deviceIp) == true)?.Id;
        }

        Device entity;
        if (input.Id == 0)
        {
            input.FirstSeen = now;
            input.LastSeen = now;
            input.IsManuallyManaged = true;
            input.Status = DeviceStatus.Unknown;
            input.Manufacturer = string.IsNullOrWhiteSpace(input.Manufacturer) ? _oui.Lookup(input.MacAddress) : input.Manufacturer;
            db.Devices.Add(input);
            entity = input;
        }
        else
        {
            entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == input.Id, ct)
                     ?? throw new InvalidOperationException("The device no longer exists.");

            Track(entity, "Name", entity.Name, input.Name, now, "Manual");
            Track(entity, "IpAddress", entity.IpAddress, input.IpAddress, now, "Manual");
            Track(entity, "MacAddress", entity.MacAddress, input.MacAddress, now, "Manual");
            Track(entity, "Category", entity.Category.ToString(), input.Category.ToString(), now, "Manual");
            Track(entity, "Hostname", entity.Hostname, input.Hostname, now, "Manual");

            entity.Name = input.Name;
            entity.IpAddress = input.IpAddress;
            entity.MacAddress = input.MacAddress;
            entity.Manufacturer = string.IsNullOrWhiteSpace(input.Manufacturer) ? _oui.Lookup(input.MacAddress) : input.Manufacturer;
            entity.Vendor = input.Vendor;
            entity.Category = input.Category;
            entity.OperatingSystem = input.OperatingSystem;
            entity.Hostname = input.Hostname;
            entity.DnsName = input.DnsName;
            entity.InterfaceName = input.InterfaceName;
            entity.SubnetId = input.SubnetId;
            entity.Notes = input.Notes;
            entity.Tags = input.Tags;
            entity.Location = input.Location;
            entity.IsTrusted = input.IsTrusted;
            entity.IsImportant = input.IsImportant;
            entity.ExcludeFromMonitoring = input.ExcludeFromMonitoring;
            entity.IsManuallyManaged = true;
        }

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(entity.IpAddress))
            await EnsureAddressEntryAsync(db, entity, now, ct);

        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("Device", input.Id == 0 ? "Added device" : "Updated device",
            $"{entity.Name} {entity.IpAddress} {entity.MacAddress}", ct);
        return entity;
    }

    public async Task DeleteDeviceAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return;
        var linked = await db.IpAddresses.Where(a => a.DeviceId == id).ToListAsync(ct);
        foreach (var a in linked) a.DeviceId = null;
        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("Device", "Deleted device", $"{device.Name} {device.IpAddress}", ct);
    }

    private static void Track(Device device, string field, string? oldValue, string? newValue, DateTime now, string source)
    {
        if (string.Equals(oldValue ?? "", newValue ?? "", StringComparison.Ordinal)) return;
        device.History.Add(new DeviceHistory
        {
            Field = field,
            OldValue = Truncate(oldValue, 500),
            NewValue = Truncate(newValue, 500),
            ChangedAt = now,
            Source = source
        });
    }

    private static string? Truncate(string? s, int max) => s?.Length > max ? s[..max] : s;

    // ---------------------------------------------------------------- discovery merge

    public async Task<DiscoveryApplyResult> ApplyScanResultAsync(ScanResult result, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = _clock.UtcNow;
        var subnets = await db.Subnets.ToListAsync(ct);
        var devices = await db.Devices.ToListAsync(ct);
        var warnings = new List<string>();
        var newDevices = new List<Device>();
        var updated = 0;
        var newAddresses = 0;
        var observations = 0;

        foreach (var host in result.Hosts.Where(h => h.IsAlive))
        {
            var mac = MacUtil.Normalize(host.MacAddress);
            var device = mac is not null
                ? devices.FirstOrDefault(d => string.Equals(d.MacAddress, mac, StringComparison.OrdinalIgnoreCase))
                : null;
            device ??= devices.FirstOrDefault(d => d.IpAddress == host.IpAddress && (d.IsManuallyManaged || d.MacAddress is null));

            var subnet = subnets.FirstOrDefault(s => s.NetworkOrNull?.Contains(host.IpAddress) == true);
            var isNew = false;

            if (device is null)
            {
                if (mac is not null)
                {
                    // Avoid creating a second record for an address that already has a device with a different MAC.
                    var sameIp = devices.FirstOrDefault(d => d.IpAddress == host.IpAddress);
                    if (sameIp is not null)
                    {
                        warnings.Add($"{host.IpAddress} was seen with {mac} but is documented as '{sameIp.Name}' ({sameIp.MacAddress ?? "no MAC"}). Record retained; check for an IP conflict.");
                        continue;
                    }
                }

                device = new Device
                {
                    Name = BuildDeviceName(host),
                    IpAddress = host.IpAddress,
                    MacAddress = mac,
                    Manufacturer = host.Vendor,
                    Vendor = host.Vendor,
                    Category = DeviceClassifier.Guess(host.Vendor, host.BestName, null, host.OpenPorts),
                    Hostname = host.Hostname,
                    DnsName = host.DnsName,
                    SubnetId = subnet?.Id,
                    FirstSeen = now,
                    LastSeen = now,
                    Status = DeviceStatus.Online,
                    LastSeenOnline = now,
                    DiscoverySource = host.SourceText,
                    OpenPorts = host.OpenPorts.Count > 0 ? string.Join(',', host.OpenPorts) : null,
                    IsManuallyManaged = false
                };
                db.Devices.Add(device);
                devices.Add(device);
                newDevices.Add(device);
                isNew = true;

                if (_alerts is not null && _alertsEnabled)
                {
                    var unknown = device.Category == DeviceCategory.Unknown;
                    var severity = unknown ? AlertSeverity.Warning : AlertSeverity.Info;
                    _ = _alerts.RaiseAsync(unknown ? AlertType.UnknownDevice : AlertType.NewDevice, severity,
                        unknown ? "Unknown device discovered" : "New device discovered",
                        $"{device.Name} at {device.IpAddress} ({mac ?? "no MAC"}) was discovered via {host.SourceText}.",
                        "Device", null, device.IpAddress, $"new-device:{host.IpAddress}:{mac}", ct);
                }
            }
            else
            {
                if (device.MacAddress is not null && mac is not null &&
                    !string.Equals(device.MacAddress, mac, StringComparison.OrdinalIgnoreCase))
                {
                    device.History.Add(new DeviceHistory
                    {
                        Field = "MacAddress",
                        OldValue = device.MacAddress,
                        NewValue = mac,
                        ChangedAt = now,
                        Source = host.SourceText,
                        Note = "The MAC address observed for this IP changed."
                    });

                    if (_alerts is not null)
                    {
                        _ = _alerts.RaiseAsync(AlertType.MacAddressChanged, AlertSeverity.Warning,
                            "MAC address changed", $"{device.Name} ({device.IpAddress}) changed MAC from {device.MacAddress} to {mac}.",
                            "Device", device.Id, device.IpAddress, $"mac-change:{device.Id}", ct);
                    }
                }

                if (device.IpAddress != host.IpAddress)
                {
                    device.History.Add(new DeviceHistory
                    {
                        Field = "IpAddress",
                        OldValue = device.IpAddress,
                        NewValue = host.IpAddress,
                        ChangedAt = now,
                        Source = host.SourceText,
                        Note = "Address change observed during discovery."
                    });
                    device.IpAddress = host.IpAddress;
                    device.SubnetId = subnet?.Id;
                }

                device.LastSeen = now;
                device.LastSeenOnline = now;
                device.Status = DeviceStatus.Online;
                device.DiscoverySource = host.SourceText;
                device.MacAddress ??= mac;
                device.Hostname ??= host.Hostname;
                device.DnsName ??= host.DnsName;
                device.Manufacturer ??= host.Vendor;
                device.Vendor ??= host.Vendor;
                if (device.Category == DeviceCategory.Unknown)
                    device.Category = DeviceClassifier.Guess(host.Vendor, host.BestName, null, host.OpenPorts);
                if (host.OpenPorts.Count > 0) device.OpenPorts = string.Join(',', host.OpenPorts);
                updated++;
            }

            await db.SaveChangesAsync(ct); // obtains the device id for address entries

            if (mac is not null)
            {
                var obs = await db.MacObservations.FirstOrDefaultAsync(o => o.IpAddress == host.IpAddress && o.MacAddress == mac, ct);
                if (obs is null)
                {
                    db.MacObservations.Add(new MacObservation
                    {
                        IpAddress = host.IpAddress,
                        MacAddress = mac,
                        SubnetId = subnet?.Id,
                        FirstSeen = now,
                        LastSeen = now,
                        Source = host.Sources.FirstOrDefault().ToString(),
                        ObservationCount = 1
                    });
                }
                else
                {
                    obs.LastSeen = now;
                    obs.ObservationCount++;
                    obs.Source = host.SourceText;
                    obs.SubnetId ??= subnet?.Id;
                }
                observations++;
            }

            if (subnet is not null && !isNew)
            {
                var existing = await db.IpAddresses.FirstOrDefaultAsync(a => a.SubnetId == subnet.Id && a.Address == host.IpAddress, ct);
                if (existing is null)
                {
                    db.IpAddresses.Add(new IpAddressEntry
                    {
                        SubnetId = subnet.Id,
                        Address = host.IpAddress,
                        DeviceId = device.Id,
                        MacAddress = device.MacAddress,
                        Hostname = device.Hostname,
                        AllocationType = subnet.DhcpStart is not null && InRange(host.IpAddress, subnet.DhcpStart, subnet.DhcpEnd)
                            ? AllocationType.Dynamic : AllocationType.Unknown,
                        Status = IpStatus.Assigned,
                        Description = "Discovered device",
                        IsManuallyManaged = false,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    newAddresses++;
                }
            }
            else if (isNew && subnet is not null)
            {
                await EnsureAddressEntryAsync(db, device, now, ct);
                newAddresses++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new DiscoveryApplyResult
        {
            NewDevices = newDevices.Count,
            UpdatedDevices = updated,
            NewAddresses = newAddresses,
            ObservationsRecorded = observations,
            NewDeviceList = newDevices,
            Warnings = warnings.Take(50).ToList()
        };
    }

    private bool _alertsEnabled => _alerts is not null;

    private static string BuildDeviceName(DiscoveredHost host)
    {
        if (!string.IsNullOrWhiteSpace(host.Hostname)) return host.Hostname!;
        if (!string.IsNullOrWhiteSpace(host.NetBiosName)) return host.NetBiosName!;
        if (!string.IsNullOrWhiteSpace(host.Vendor)) return $"{host.Vendor} ({host.IpAddress})";
        return $"Device {host.IpAddress}";
    }

    private static async Task EnsureAddressEntryAsync(IpManagerDbContext db, Device device, DateTime now, CancellationToken ct)
    {
        if (device.SubnetId is null || string.IsNullOrWhiteSpace(device.IpAddress)) return;
        var existing = await db.IpAddresses.FirstOrDefaultAsync(a =>
            a.SubnetId == device.SubnetId && a.Address == device.IpAddress, ct);
        if (existing is null)
        {
            var subnet = await db.Subnets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == device.SubnetId, ct);
            db.IpAddresses.Add(new IpAddressEntry
            {
                SubnetId = device.SubnetId,
                Address = device.IpAddress,
                DeviceId = device.Id,
                MacAddress = device.MacAddress,
                Hostname = device.Hostname,
                AllocationType = subnet is not null && InRange(device.IpAddress, subnet.DhcpStart, subnet.DhcpEnd)
                    ? AllocationType.Dynamic
                    : AllocationType.Unknown,
                Status = IpStatus.Assigned,
                Description = device.IsManuallyManaged ? "Manually documented device" : "Discovered device",
                IsManuallyManaged = device.IsManuallyManaged,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else if (existing.DeviceId is null)
        {
            existing.DeviceId = device.Id;
        }
    }

    // ---------------------------------------------------------------- helpers

    public static bool InRange(string ip, string? start, string? end)
    {
        if (!Ipv4Address.TryParse(ip, out var address) || !Ipv4Address.TryParse(start, out var s) || !Ipv4Address.TryParse(end, out var e))
            return false;
        return address.Value >= s.Value && address.Value <= e.Value;
    }

    public static IReadOnlyList<Ipv4Address> ParseAddressList(string? text)
    {
        var result = new List<Ipv4Address>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (token.Contains('-'))
            {
                var bits = token.Split('-', 2);
                if (Ipv4Address.TryParse(bits[0], out var start) && Ipv4Address.TryParse(bits[1], out var end) && start.Value <= end.Value)
                {
                    var count = end.Value - start.Value + 1;
                    if (count > 4096) continue;
                    for (var v = start.Value; v <= end.Value; v++) result.Add(new Ipv4Address(v));
                }
                continue;
            }
            if (Ipv4Address.TryParse(token, out var ip)) result.Add(ip);
        }
        return result.Distinct().ToList();
    }

    private static void ValidateRange(string? start, string? end, Ipv4Network network, string label, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end)) return;
        if (!Ipv4Address.TryParse(start, out var s) || !Ipv4Address.TryParse(end, out var e))
            throw new ArgumentException($"The {label} range must contain two valid IPv4 addresses.");
        if (s.Value > e.Value) throw new ArgumentException($"The {label} range start is greater than its end.");
        if (!network.Contains(s) || !network.Contains(e))
            throw new ArgumentException($"The {label} range must be inside {network.Cidr}.");
        if (new Ipv4Range(s, e).Length > network.UsableHosts)
            warnings.Add($"The {label} range is larger than the usable host range of {network.Cidr}.");
    }
}

public readonly record struct Ipv4Range(Ipv4Address Start, Ipv4Address End)
{
    public long Length => End.Value >= Start.Value ? End.Value - Start.Value + 1L : 0;
    public bool Overlaps(Ipv4Range other) => Start.Value <= other.End.Value && other.Start.Value <= End.Value;
}
