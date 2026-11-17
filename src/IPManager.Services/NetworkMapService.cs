using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public enum MapNodeKind { Internet, Gateway, Device }
public enum MapEdgeConfidence { Verified, Inferred, Unknown }
public enum MapGrouping { Subnet, Category }

public sealed record MapNode
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public MapNodeKind Kind { get; init; }
    public string? IpAddress { get; init; }
    public string? MacAddress { get; init; }
    public string? SubnetCidr { get; init; }
    public int? SubnetId { get; init; }
    public DeviceCategory Category { get; init; }
    public DeviceStatus Status { get; init; }
    public bool IsTrusted { get; init; }
    public string GroupKey { get; init; } = "";
    public int? DeviceId { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record MapEdge
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public MapEdgeConfidence Confidence { get; init; }
    public string Label { get; init; } = "";
}

public sealed record NetworkMapGraph
{
    public IReadOnlyList<MapNode> Nodes { get; init; } = Array.Empty<MapNode>();
    public IReadOnlyList<MapEdge> Edges { get; init; } = Array.Empty<MapEdge>();
    public DateTime GeneratedAt { get; init; }
    public string Notes { get; init; } = "";
}

public sealed class NetworkMapService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly IClock _clock;

    public NetworkMapService(IDbContextFactory<IpManagerDbContext> factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<NetworkMapGraph> BuildAsync(MapGrouping grouping = MapGrouping.Subnet, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var subnets = await db.Subnets.AsNoTracking().ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        var observations = await db.MacObservations.AsNoTracking().ToListAsync(ct);
        var interfaces = NetworkInterfaceService.GetActive();

        var nodes = new List<MapNode>();
        var edges = new List<MapEdge>();

        nodes.Add(new MapNode
        {
            Id = "internet",
            Label = "Internet",
            Kind = MapNodeKind.Internet,
            GroupKey = "External",
            Detail = "External network boundary"
        });

        var gateways = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // ip -> node id
        void AddGateway(string ip, Subnet? subnet)
        {
            if (gateways.ContainsKey(ip)) return;
            var id = "gw:" + ip;
            gateways[ip] = id;
            nodes.Add(new MapNode
            {
                Id = id,
                Label = subnet is null ? $"Gateway {ip}" : $"{subnet.Name} gateway",
                Kind = MapNodeKind.Gateway,
                IpAddress = ip,
                SubnetId = subnet?.Id,
                SubnetCidr = subnet?.Cidr,
                GroupKey = grouping == MapGrouping.Subnet ? (subnet?.Name ?? "Gateway") : "Network infrastructure",
                Detail = subnet is null ? "Default gateway from the active interface" : $"Configured gateway of {subnet.Cidr}"
            });
            edges.Add(new MapEdge
            {
                SourceId = "internet",
                TargetId = id,
                Confidence = MapEdgeConfidence.Inferred,
                Label = "Default route (configuration)"
            });
        }

        foreach (var subnet in subnets.Where(s => !string.IsNullOrWhiteSpace(s.Gateway)))
            AddGateway(subnet.Gateway!, subnet);
        foreach (var ni in interfaces.Where(i => i.IsUp && i.Gateway is not null))
            AddGateway(ni.Gateway!, null);

        foreach (var device in devices)
        {
            var subnet = subnets.FirstOrDefault(s => s.Id == device.SubnetId)
                         ?? subnets.FirstOrDefault(s => device.IpAddress is not null && s.NetworkOrNull?.Contains(device.IpAddress) == true);
            var id = "device:" + device.Id;
            var observedMac = observations
                .Where(o => o.IpAddress == device.IpAddress)
                .OrderByDescending(o => o.LastSeen)
                .Select(o => o.MacAddress)
                .FirstOrDefault();
            var mac = device.MacAddress ?? observedMac;
            var groupKey = grouping == MapGrouping.Subnet
                ? subnet?.Name ?? "Unassigned"
                : DeviceClassifier.DisplayName(device.Category);

            nodes.Add(new MapNode
            {
                Id = id,
                Label = string.IsNullOrWhiteSpace(device.Name) ? (device.IpAddress ?? "Device") : device.Name,
                Kind = MapNodeKind.Device,
                IpAddress = device.IpAddress,
                MacAddress = mac,
                SubnetId = subnet?.Id,
                SubnetCidr = subnet?.Cidr,
                Category = device.Category,
                Status = device.Status,
                IsTrusted = device.IsTrusted,
                GroupKey = groupKey,
                DeviceId = device.Id,
                Detail = $"{DeviceClassifier.DisplayName(device.Category)} · {device.Manufacturer ?? "vendor unknown"} · {device.Status}"
            });

            if (subnet?.Gateway is not null && gateways.TryGetValue(subnet.Gateway, out var gatewayId))
            {
                edges.Add(new MapEdge
                {
                    SourceId = gatewayId,
                    TargetId = id,
                    Confidence = MapEdgeConfidence.Inferred,
                    Label = "Same IPv4 subnet - physical uplink unknown"
                });
            }
            else if (device.IpAddress is not null)
            {
                var interfaceGateway = interfaces.FirstOrDefault(i => i.IsUp && i.Gateway is not null);
                if (interfaceGateway?.Gateway is not null && gateways.TryGetValue(interfaceGateway.Gateway, out var defaultGw))
                {
                    edges.Add(new MapEdge
                    {
                        SourceId = defaultGw,
                        TargetId = id,
                        Confidence = MapEdgeConfidence.Unknown,
                        Label = "Subnet not mapped - relationship unknown"
                    });
                }
            }
        }

        // Mark a gateway->device edge as verified only when the device is the gateway itself (same IP + MAC evidence).
        foreach (var node in nodes.Where(n => n.Kind == MapNodeKind.Device && n.MacAddress is not null).ToList())
        {
            var gateway = gateways.FirstOrDefault(g => g.Key == node.IpAddress);
            if (gateway.Key is null) continue;
            var edge = edges.FirstOrDefault(e => e.TargetId == node.Id && e.SourceId == gateway.Value);
            if (edge is null) continue;
            var index = edges.IndexOf(edge);
            edges[index] = edge with { Confidence = MapEdgeConfidence.Verified, Label = "Device answers on the gateway address" };
        }

        return new NetworkMapGraph
        {
            Nodes = nodes,
            Edges = edges,
            GeneratedAt = _clock.UtcNow,
            Notes = "Edges marked 'inferred' are derived from configured subnets and the default route; IP Manager does not have " +
                    "LLDP/SNMP topology data, so physical switch ports and uplinks are not claimed as verified."
        };
    }

    public async Task SaveLayoutAsync(string json, string name = "Default", int? subnetId = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var layout = await db.TopologyLayouts.FirstOrDefaultAsync(l => l.Name == name && l.SubnetId == subnetId, ct);
        if (layout is null)
        {
            layout = new TopologyLayout { Name = name, SubnetId = subnetId, CreatedAt = _clock.UtcNow };
            db.TopologyLayouts.Add(layout);
        }
        layout.Json = json;
        layout.IsDefault = subnetId is null;
        layout.UpdatedAt = _clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<TopologyLayout?> GetLayoutAsync(string name = "Default", int? subnetId = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TopologyLayouts.AsNoTracking()
            .Where(l => l.Name == name && l.SubnetId == subnetId)
            .OrderByDescending(l => l.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
