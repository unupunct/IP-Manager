using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public sealed record DnsComparisonRow(string Server, bool Success, string Summary, double DurationMs, string? Error);

public sealed class DnsManagerService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly IDnsResolver _resolver;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly ISecretProtector _protector;
    private readonly IEnumerable<IDnsProvider> _providers;

    public DnsManagerService(IDbContextFactory<IpManagerDbContext> factory, IDnsResolver resolver, IClock clock,
        IAuditService audit, ISecretProtector protector, IEnumerable<IDnsProvider> providers)
    {
        _factory = factory;
        _resolver = resolver;
        _clock = clock;
        _audit = audit;
        _protector = protector;
        _providers = providers;
    }

    public IReadOnlyList<IDnsProvider> Providers => _providers.ToList();
    public static IReadOnlyList<string> RecordTypes { get; } = new[] { "A", "AAAA", "CNAME", "PTR", "MX", "TXT", "NS", "SRV" };

    public IReadOnlyList<string> GetSystemDnsServers() => NetworkInterfaceService.GetSystemDnsServers();

    public async Task<DnsQueryResult> LookupAsync(string name, string recordType, string? server, CancellationToken ct = default)
    {
        var result = await _resolver.QueryAsync(name, recordType, server, 4000, ct);
        await LogQueryAsync(result, ct);
        return result;
    }

    public async Task<DnsQueryResult> ReverseAsync(string ipAddress, string? server, CancellationToken ct = default)
    {
        var result = await _resolver.ReverseAsync(ipAddress, server, 4000, ct);
        await LogQueryAsync(result, ct);
        return result;
    }

    public async Task<IReadOnlyList<DnsComparisonRow>> CompareAsync(string name, string recordType, IEnumerable<string> servers,
        CancellationToken ct = default)
    {
        var rows = new List<DnsComparisonRow>();
        foreach (var server in servers.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var result = await LookupAsync(name, recordType, server, ct);
            rows.Add(new DnsComparisonRow(server, result.Success,
                result.Answers.Count == 0 ? (result.Error ?? "No records") : string.Join(", ", result.Answers.Select(a => a.Value)),
                result.DurationMs, result.Error));
        }
        return rows;
    }

    private async Task LogQueryAsync(DnsQueryResult result, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.DnsQueryLogs.Add(new DnsQueryLog
        {
            QueryName = result.QueryName,
            RecordType = result.RecordType,
            Server = result.Server,
            Response = result.ResponseText,
            Ttl = result.Answers.Count > 0 ? (int)result.Answers.Min(a => a.Ttl) : null,
            DurationMs = result.DurationMs,
            Success = result.Success,
            Timestamp = _clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<DnsQueryLog>> GetHistoryAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DnsQueryLogs.AsNoTracking().OrderByDescending(q => q.Timestamp).Take(Math.Clamp(limit, 1, 2000)).ToListAsync(ct);
    }

    public async Task<List<DnsRecordDoc>> GetLocalRecordsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DnsRecords.AsNoTracking().OrderBy(r => r.Zone).ThenBy(r => r.Name).ToListAsync(ct);
    }

    public async Task<DnsRecordDoc> SaveLocalRecordAsync(DnsRecordDoc input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Zone)) throw new ArgumentException("A zone is required.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ArgumentException("A record name is required.");
        if (string.IsNullOrWhiteSpace(input.Value)) throw new ArgumentException("A record value is required.");
        var type = (input.Type ?? "A").ToUpperInvariant();
        if (!RecordTypes.Contains(type)) throw new ArgumentException($"Unsupported record type '{input.Type}'.");
        if (type == "A" && !Ipv4Address.TryParse(input.Value, out _))
            throw new ArgumentException("An A record value must be a valid IPv4 address.");
        if (type == "AAAA" && (!System.Net.IPAddress.TryParse(input.Value, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6))
            throw new ArgumentException("An AAAA record value must be a valid IPv6 address.");
        if (input.Ttl is < 0 or > 2147483647) throw new ArgumentException("The TTL is out of range.");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = _clock.UtcNow;
        DnsRecordDoc entity;
        if (input.Id == 0)
        {
            input.Type = type;
            input.CreatedAt = now;
            input.UpdatedAt = now;
            db.DnsRecords.Add(input);
            entity = input;
        }
        else
        {
            entity = await db.DnsRecords.FirstOrDefaultAsync(r => r.Id == input.Id, ct)
                     ?? throw new InvalidOperationException("The record no longer exists.");
            entity.Zone = input.Zone;
            entity.Name = input.Name;
            entity.Type = type;
            entity.Value = input.Value;
            entity.Ttl = input.Ttl;
            entity.Server = input.Server;
            entity.Notes = input.Notes;
            entity.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("DNS", input.Id == 0 ? "Documented DNS record" : "Updated DNS record",
            $"{entity.Name} {entity.Type} {entity.Value}", ct);
        return entity;
    }

    public async Task DeleteLocalRecordAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var record = await db.DnsRecords.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null) return;
        db.DnsRecords.Remove(record);
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("DNS", "Deleted DNS record", $"{record.Name} {record.Type}", ct);
    }

    public async Task<List<IntegrationConfig>> GetIntegrationsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var kinds = _providers.Select(p => p.Kind.ToString()).ToList();
        return await db.Integrations.AsNoTracking().Where(i => kinds.Contains(i.Kind)).OrderBy(i => i.Name).ToListAsync(ct);
    }

    public async Task<IntegrationConfig> SaveIntegrationAsync(DnsProviderKind kind, string name, string? endpoint,
        string? username, string? plainSecret, string? zone, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A display name is required.");
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.FirstOrDefaultAsync(i => i.Kind == kind.ToString() && i.Name == name, ct);
        var now = _clock.UtcNow;
        if (config is null)
        {
            config = new IntegrationConfig { Kind = kind.ToString(), Name = name, CreatedAt = now };
            db.Integrations.Add(config);
        }

        config.Endpoint = endpoint;
        config.Username = username;
        config.Enabled = enabled;
        config.OptionsJson = string.IsNullOrWhiteSpace(zone) ? config.OptionsJson : $"{{\"zone\":\"{zone.Replace("\"", "")}\"}}";
        if (!string.IsNullOrEmpty(plainSecret))
            config.SecretProtected = _protector.Protect(plainSecret);
        config.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("Security", "DNS integration saved", $"{kind} '{name}' (secret DPAPI-protected)", ct);
        return config;
    }

    public async Task DeleteIntegrationAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (config is null) return;
        db.Integrations.Remove(config);
        await db.SaveChangesAsync(ct);
    }

    public IDnsProvider GetProvider(DnsProviderKind kind) =>
        _providers.FirstOrDefault(p => p.Kind == kind)
        ?? throw new InvalidOperationException($"The DNS provider {kind} is not available.");

    public async Task<DnsProviderResult> TestProviderAsync(int integrationId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == integrationId, ct)
                     ?? throw new InvalidOperationException("Integration not found.");
        var provider = GetProvider(Enum.Parse<DnsProviderKind>(config.Kind));
        var result = await provider.TestAsync(config, _protector, ct);
        await UpdateStateAsync(config.Id, result.Success ? IntegrationState.Connected : IntegrationState.Error, result.Message, ct);
        return result;
    }

    public async Task<DnsProviderResult> GetRemoteRecordsAsync(int integrationId, string? zone, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == integrationId, ct)
                     ?? throw new InvalidOperationException("Integration not found.");
        var provider = GetProvider(Enum.Parse<DnsProviderKind>(config.Kind));
        var result = await provider.GetRecordsAsync(config, _protector, zone, ct);
        await UpdateStateAsync(config.Id, result.Success ? IntegrationState.Connected : IntegrationState.Error, result.Message, ct);
        return result;
    }

    public async Task CreateRemoteRecordAsync(int integrationId, RemoteDnsRecord record, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == integrationId, ct)
                     ?? throw new InvalidOperationException("Integration not found.");
        var provider = GetProvider(Enum.Parse<DnsProviderKind>(config.Kind));
        if (!provider.CanWrite) throw new NotSupportedException($"{provider.DisplayName} is read-only.");
        await provider.CreateRecordAsync(config, _protector, record, ct);
        await _audit.LogAsync("DNS", "Created remote DNS record", $"{provider.DisplayName}: {record.Name} {record.Type} {record.Value}", ct);
    }

    public async Task DeleteRemoteRecordAsync(int integrationId, RemoteDnsRecord record, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == integrationId, ct)
                     ?? throw new InvalidOperationException("Integration not found.");
        var provider = GetProvider(Enum.Parse<DnsProviderKind>(config.Kind));
        if (!provider.CanWrite) throw new NotSupportedException($"{provider.DisplayName} is read-only.");
        await provider.DeleteRecordAsync(config, _protector, record, ct);
        await _audit.LogAsync("DNS", "Deleted remote DNS record", $"{provider.DisplayName}: {record.Name} {record.Type} {record.Value}", ct);
    }

    private async Task UpdateStateAsync(int id, IntegrationState state, string? message, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var config = await db.Integrations.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (config is null) return;
        config.State = state;
        config.LastError = state == IntegrationState.Error ? message : null;
        config.LastUsedAt = _clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
