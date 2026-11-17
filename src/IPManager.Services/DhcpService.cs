using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPManager.Core;
using IPManager.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public sealed record RemoteDhcpLease(string IpAddress, string MacAddress, string? Hostname, DateTime? ExpiresUtc);
public sealed record RemoteDhcpReservation(string IpAddress, string MacAddress, string? Hostname);

public sealed record RemoteDhcpScope
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string StartAddress { get; init; } = "";
    public string EndAddress { get; init; } = "";
    public string? SubnetMask { get; init; }
    public string? Server { get; init; }
    public string Provider { get; init; } = "";
    public IReadOnlyList<RemoteDhcpLease> Leases { get; init; } = Array.Empty<RemoteDhcpLease>();
    public IReadOnlyList<RemoteDhcpReservation> Reservations { get; init; } = Array.Empty<RemoteDhcpReservation>();
}

public sealed record DhcpProviderResult(bool Success, string Message, IReadOnlyList<RemoteDhcpScope> Scopes)
{
    public static DhcpProviderResult Fail(string message) => new(false, message, Array.Empty<RemoteDhcpScope>());
}

public interface IDhcpProvider
{
    DhcpProviderKind Kind { get; }
    string DisplayName { get; }
    string Requirements { get; }
    bool CanRead { get; }
    bool CanWrite { get; }
    Task<DhcpProviderResult> ReadAsync(IntegrationConfig config, CancellationToken ct);
    Task CreateReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct);
    Task DeleteReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct);
}

public sealed record LocalDhcpClientInfo
{
    public string Adapter { get; init; } = "";
    public string? MacAddress { get; init; }
    public string? IpAddress { get; init; }
    public bool DhcpEnabled { get; init; }
    public string? DhcpServer { get; init; }
    public DateTime? LeaseObtained { get; init; }
    public DateTime? LeaseExpires { get; init; }
    public string? DefaultGateway { get; init; }
    public string? DnsServers { get; init; }
}

public sealed class DhcpService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly IEnumerable<IDhcpProvider> _providers;

    public DhcpService(IDbContextFactory<IpManagerDbContext> factory, IClock clock, IAuditService audit,
        IEnumerable<IDhcpProvider> providers)
    {
        _factory = factory;
        _clock = clock;
        _audit = audit;
        _providers = providers;
    }

    public IReadOnlyList<IDhcpProvider> Providers => _providers.ToList();

    public async Task<List<DhcpScope>> GetScopesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DhcpScopes.AsNoTracking()
            .Include(s => s.Subnet)
            .Include(s => s.Reservations)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<DhcpScopeSaveResult> SaveScopeAsync(DhcpScope input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("A scope name is required.");
        if (!Ipv4Address.TryParse(input.StartAddress, out var start) || !Ipv4Address.TryParse(input.EndAddress, out var end))
            throw new ArgumentException("The pool start and end must be valid IPv4 addresses.");
        if (start.Value > end.Value) throw new ArgumentException("The pool start must not be greater than the end.");
        if (input.SubnetId is null) throw new ArgumentException("Select the subnet this DHCP scope belongs to.");
        if (input.LeaseMinutes is < 1 or > 525600) throw new ArgumentException("The lease duration must be between 1 and 525600 minutes.");
        if (!string.IsNullOrWhiteSpace(input.ExcludedAddresses))
        {
            foreach (var excluded in InventoryService.ParseAddressList(input.ExcludedAddresses))
            {
                if (excluded.Value < start.Value || excluded.Value > end.Value)
                    throw new ArgumentException($"Excluded address {excluded} is outside the DHCP pool.");
            }
        }

        var warnings = new List<string>();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var subnet = await db.Subnets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == input.SubnetId, ct)
                     ?? throw new InvalidOperationException("The selected subnet no longer exists.");
        if (subnet.NetworkOrNull is { } network)
        {
            if (!network.Contains(start) || !network.Contains(end))
                throw new ArgumentException($"The DHCP pool must be inside {network.Cidr}.");
            if (!string.IsNullOrWhiteSpace(subnet.StaticStart) && !string.IsNullOrWhiteSpace(subnet.StaticEnd) &&
                Ipv4Address.TryParse(subnet.StaticStart, out var ss) && Ipv4Address.TryParse(subnet.StaticEnd, out var se) &&
                new Ipv4Range(start, end).Overlaps(new Ipv4Range(ss, se)))
                warnings.Add("The DHCP pool overlaps the subnet's static allocation range.");
        }

        var now = _clock.UtcNow;
        DhcpScope entity;
        if (input.Id == 0)
        {
            input.CreatedAt = now;
            input.UpdatedAt = now;
            input.Provider = string.IsNullOrWhiteSpace(input.Provider)
                ? DhcpProviderKind.LocalDocumentation.ToString()
                : input.Provider;
            db.DhcpScopes.Add(input);
            entity = input;
        }
        else
        {
            entity = await db.DhcpScopes.FirstOrDefaultAsync(s => s.Id == input.Id, ct)
                     ?? throw new InvalidOperationException("The scope no longer exists.");
            entity.Name = input.Name;
            entity.SubnetId = input.SubnetId;
            entity.ServerAddress = input.ServerAddress;
            entity.StartAddress = input.StartAddress;
            entity.EndAddress = input.EndAddress;
            entity.LeaseMinutes = input.LeaseMinutes;
            entity.ExcludedAddresses = input.ExcludedAddresses;
            entity.Notes = input.Notes;
            entity.Enabled = input.Enabled;
            entity.Provider = input.Provider;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("DHCP", input.Id == 0 ? "Created DHCP scope" : "Updated DHCP scope",
            $"{entity.Name} {entity.StartAddress}-{entity.EndAddress}", ct);
        return new DhcpScopeSaveResult { Scope = entity, Warnings = warnings };
    }

    public async Task DeleteScopeAsync(int scopeId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var scope = await db.DhcpScopes.FirstOrDefaultAsync(s => s.Id == scopeId, ct);
        if (scope is null) return;
        db.DhcpScopes.Remove(scope);
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("DHCP", "Deleted DHCP scope", scope.Name, ct);
    }

    public async Task<DhcpReservation> SaveReservationAsync(DhcpReservation input, CancellationToken ct = default)
    {
        if (!Ipv4Address.TryParse(input.IpAddress, out var ip))
            throw new ArgumentException("The reserved IP address is not valid.");
        var mac = MacUtil.Normalize(input.MacAddress)
                  ?? throw new ArgumentException("The MAC address must contain 12 hexadecimal digits.");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var scope = await db.DhcpScopes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == input.DhcpScopeId, ct)
                    ?? throw new InvalidOperationException("The scope no longer exists.");
        if (Ipv4Address.TryParse(scope.StartAddress, out var start) && Ipv4Address.TryParse(scope.EndAddress, out var end) &&
            (ip.Value < start.Value || ip.Value > end.Value))
            throw new ArgumentException($"{input.IpAddress} is outside the pool {scope.StartAddress}-{scope.EndAddress}.");

        var duplicate = await db.DhcpReservations.AsNoTracking()
            .Where(r => r.DhcpScopeId == input.DhcpScopeId && r.Id != input.Id &&
                        (r.IpAddress == input.IpAddress || r.MacAddress == mac))
            .Select(r => new { r.IpAddress, r.MacAddress })
            .FirstOrDefaultAsync(ct);
        if (duplicate is not null)
            throw new DuplicateAssignmentException(
                $"The scope already reserves {duplicate.IpAddress} for {duplicate.MacAddress}.");

        var now = _clock.UtcNow;
        DhcpReservation entity;
        if (input.Id == 0)
        {
            input.MacAddress = mac;
            input.CreatedAt = now;
            input.UpdatedAt = now;
            input.Source = string.IsNullOrWhiteSpace(input.Source) ? "Manual" : input.Source;
            db.DhcpReservations.Add(input);
            entity = input;
        }
        else
        {
            entity = await db.DhcpReservations.FirstOrDefaultAsync(r => r.Id == input.Id, ct)
                     ?? throw new InvalidOperationException("The reservation no longer exists.");
            entity.IpAddress = input.IpAddress;
            entity.MacAddress = mac;
            entity.Hostname = input.Hostname;
            entity.Description = input.Description;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("DHCP", input.Id == 0 ? "Created reservation" : "Updated reservation",
            $"{entity.IpAddress} {entity.MacAddress} {entity.Hostname}", ct);
        return entity;
    }

    public async Task DeleteReservationAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var reservation = await db.DhcpReservations.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reservation is null) return;
        db.DhcpReservations.Remove(reservation);
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("DHCP", "Deleted reservation", reservation.IpAddress, ct);
    }

    public async Task<List<LocalDhcpClientInfo>> GetLocalClientInfoAsync(CancellationToken ct = default)
    {
        var script = "Get-CimInstance Win32_NetworkAdapterConfiguration -Filter \"IPEnabled=TRUE\" | " +
                     "Select-Object Description,MACAddress,IPAddress,DHCPEnabled,DHCPServer,DHCPLeaseObtained,DHCPLeaseExpires,DefaultIPGateway,DNSServerSearchOrder | " +
                     "ConvertTo-Json -Compress";
        var json = await RunPowerShellAsync(script, ct);
        if (string.IsNullOrWhiteSpace(json)) return new List<LocalDhcpClientInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            var list = new List<LocalDhcpClientInfo>();
            foreach (var el in elements)
            {
                list.Add(new LocalDhcpClientInfo
                {
                    Adapter = GetString(el, "Description") ?? "",
                    MacAddress = MacUtil.Normalize(GetString(el, "MACAddress")),
                    IpAddress = FirstArrayValue(el, "IPAddress"),
                    DhcpEnabled = GetBool(el, "DHCPEnabled"),
                    DhcpServer = GetString(el, "DHCPServer"),
                    LeaseObtained = ParseWmiDate(GetString(el, "DHCPLeaseObtained")),
                    LeaseExpires = ParseWmiDate(GetString(el, "DHCPLeaseExpires")),
                    DefaultGateway = FirstArrayValue(el, "DefaultIPGateway"),
                    DnsServers = string.Join(", ", GetArray(el, "DNSServerSearchOrder"))
                });
            }
            return list;
        }
        catch
        {
            return new List<LocalDhcpClientInfo>();
        }
    }

    public async Task<IntegrationConfig?> GetProviderConfigAsync(DhcpProviderKind kind, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Integrations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Kind == kind.ToString(), ct);
    }

    public async Task<IntegrationConfig> SaveProviderConfigAsync(DhcpProviderKind kind, string? pathOrEndpoint, string? optionsJson, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.FirstOrDefaultAsync(i => i.Kind == kind.ToString(), ct);
        var now = _clock.UtcNow;
        if (config is null)
        {
            config = new IntegrationConfig
            {
                Kind = kind.ToString(),
                Name = kind.ToString(),
                CreatedAt = now
            };
            db.Integrations.Add(config);
        }
        config.Endpoint = pathOrEndpoint;
        config.OptionsJson = optionsJson;
        config.UpdatedAt = now;
        config.Enabled = true;
        await db.SaveChangesAsync(ct);
        return config;
    }

    public async Task<DhcpProviderResult> ReadFromProviderAsync(DhcpProviderKind kind, CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(p => p.Kind == kind)
                       ?? throw new InvalidOperationException($"The DHCP provider {kind} is not available in this build.");
        var config = await GetProviderConfigAsync(kind, ct) ?? new IntegrationConfig { Kind = kind.ToString(), Name = kind.ToString() };
        return await provider.ReadAsync(config, ct);
    }

    /// <summary>Copies a provider snapshot into the local documentation after explicit user confirmation.</summary>
    public async Task<int> ImportAsync(DhcpProviderKind kind, CancellationToken ct = default)
    {
        var snapshot = await ReadFromProviderAsync(kind, ct);
        if (!snapshot.Success) throw new InvalidOperationException(snapshot.Message);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var subnets = await db.Subnets.ToListAsync(ct);
        var now = _clock.UtcNow;
        var imported = 0;

        foreach (var remote in snapshot.Scopes)
        {
            var subnet = subnets.FirstOrDefault(s =>
                Ipv4Address.TryParse(remote.StartAddress, out var start) && s.NetworkOrNull?.Contains(start) == true);

            var scope = await db.DhcpScopes.FirstOrDefaultAsync(s => s.Name == remote.Name && s.Provider == kind.ToString(), ct);
            if (scope is null)
            {
                scope = new DhcpScope
                {
                    Name = remote.Name,
                    SubnetId = subnet?.Id,
                    ServerAddress = remote.Server,
                    Provider = kind.ToString(),
                    StartAddress = remote.StartAddress,
                    EndAddress = remote.EndAddress,
                    LeaseMinutes = 1440,
                    Notes = $"Imported from {kind} at {now:u}",
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastSyncedAt = now
                };
                db.DhcpScopes.Add(scope);
                await db.SaveChangesAsync(ct);
                imported++;
            }
            else
            {
                scope.StartAddress = remote.StartAddress;
                scope.EndAddress = remote.EndAddress;
                scope.ServerAddress = remote.Server;
                scope.LastSyncedAt = now;
                scope.UpdatedAt = now;
            }

            foreach (var res in remote.Reservations)
            {
                var mac = MacUtil.Normalize(res.MacAddress);
                if (mac is null) continue;
                var exists = await db.DhcpReservations.AnyAsync(r => r.DhcpScopeId == scope.Id && r.IpAddress == res.IpAddress, ct);
                if (exists) continue;
                db.DhcpReservations.Add(new DhcpReservation
                {
                    DhcpScopeId = scope.Id,
                    IpAddress = res.IpAddress,
                    MacAddress = mac,
                    Hostname = res.Hostname,
                    Source = kind.ToString(),
                    CreatedAt = now,
                    UpdatedAt = now
                });
                imported++;
            }
        }

        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("DHCP", "Imported DHCP snapshot", $"{kind}: {snapshot.Scopes.Count} scopes, {imported} records", ct);
        return imported;
    }

    internal static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process is null) return "";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(cts.Token);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        return await output;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<string> GetArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) yield return s;
        else if (v.ValueKind == JsonValueKind.String && v.GetString() is { } single) yield return single;
    }

    private static string? FirstArrayValue(JsonElement el, string name) => GetArray(el, name).FirstOrDefault();

    private static DateTime? ParseWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length >= 14 && DateTime.TryParseExact(value[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fallback)
            ? fallback.ToUniversalTime()
            : null;
    }
}

public sealed class DhcpScopeSaveResult
{
    public required DhcpScope Scope { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
