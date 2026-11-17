using System.Diagnostics;
using System.Net.Http;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public sealed record DiscoveryRunResult
{
    public required ScanResult Scan { get; init; }
    public required DiscoveryApplyResult Applied { get; init; }
    public ConflictRunResult? Conflicts { get; init; }
    public int SessionId { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string Summary { get; init; } = "";
}

public sealed class DiscoveryService
{
    private readonly ScannerService _scanner;
    private readonly InventoryService _inventory;
    private readonly ConflictService _conflicts;
    private readonly ISettingsService _settings;
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public DiscoveryService(ScannerService scanner, InventoryService inventory, ConflictService conflicts,
        ISettingsService settings, IDbContextFactory<IpManagerDbContext> factory, IClock clock, IAuditService audit)
    {
        _scanner = scanner;
        _inventory = inventory;
        _conflicts = conflicts;
        _settings = settings;
        _factory = factory;
        _clock = clock;
        _audit = audit;
    }

    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces() => NetworkInterfaceService.GetActive();

    public ScanOptions BuildOptions()
    {
        var s = _settings.Current;
        return new ScanOptions
        {
            Concurrency = s.ScanConcurrency,
            PingTimeoutMs = s.ScanTimeoutMs,
            PingRetries = s.ScanRetryCount,
            DelayBetweenProbesMs = s.ScanDelayMs,
            MaxAddresses = s.MaxAddressesPerScan,
            EnableArp = s.EnableArp,
            EnableIcmp = s.EnableIcmp,
            EnableTcp = s.EnableTcp,
            EnableReverseDns = s.EnableReverseDns,
            EnableMdns = s.EnableMdns,
            EnableNetBios = s.EnableNetBios,
            TcpPorts = ScanOptions.ParsePorts(s.TcpProbePorts),
            AllowExternalRanges = false
        };
    }

    public async Task<DiscoveryRunResult> RunAsync(IReadOnlyList<string> targets, bool allowExternal,
        IProgress<ScanProgress>? progress, CancellationToken ct = default)
    {
        var options = BuildOptions();
        options.AllowExternalRanges = allowExternal;

        var parsed = ScanTargetParser.Parse(targets, options.MaxAddresses);
        if (parsed.Addresses.Count == 0)
            throw new ArgumentException(string.Join(Environment.NewLine, parsed.Errors.DefaultIfEmpty("No addresses to scan.")));
        if (parsed.ContainsPublicAddresses && !allowExternal)
            throw new InvalidOperationException("The target contains public addresses. Confirm external scanning explicitly before running it.");

        var started = _clock.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var session = new ScanSession
            {
                StartedAt = started,
                Targets = string.Join(", ", parsed.Descriptions),
                Methods = string.Join(", ", new[]
                {
                    options.EnableArp ? "ARP" : null,
                    options.EnableIcmp ? "ICMP" : null,
                    options.EnableTcp ? "TCP" : null,
                    options.EnableReverseDns ? "DNS" : null,
                    options.EnableMdns ? "mDNS" : null,
                    options.EnableNetBios ? "NetBIOS" : null
                }.Where(m => m is not null)),
                Status = ScanStatus.Running
            };
            db.ScanSessions.Add(session);
            await db.SaveChangesAsync(ct);
        }

        ScanResult scan;
        try
        {
            scan = await _scanner.ScanAsync(new ScanRequest { Targets = targets, Options = options }, progress, ct);
        }
        catch (Exception ex)
        {
            await UpdateSessionAsync(started, null, ScanStatus.Failed, ex.Message, ct);
            throw;
        }

        var applied = await _inventory.ApplyScanResultAsync(scan, ct);
        ConflictRunResult? conflicts = null;
        try { conflicts = await _conflicts.RunAsync(ct); }
        catch (Exception ex) { await _audit.LogAsync("Conflict", "Conflict analysis after scan failed", ex.Message, ct); }

        await UpdateSessionAsync(started, scan, scan.Cancelled ? ScanStatus.Cancelled : ScanStatus.Completed,
            scan.Errors.Count > 0 ? string.Join(" | ", scan.Errors.Take(20)) : null, ct);

        var warnings = parsed.Errors.Concat(applied.Warnings).Distinct().ToList();
        return new DiscoveryRunResult
        {
            Scan = scan,
            Applied = applied,
            Conflicts = conflicts,
            Warnings = warnings,
            Summary = $"{scan.Hosts.Count} device(s) found in {scan.Duration.TotalSeconds:0.0}s; " +
                      $"{applied.NewDevices} new, {applied.UpdatedDevices} updated, {scan.AddressesChecked} address(es) checked."
        };
    }

    private async Task UpdateSessionAsync(DateTime started, ScanResult? scan, ScanStatus status, string? error, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var session = await db.ScanSessions.OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(s => s.StartedAt == started, ct);
        if (session is null) return;
        session.CompletedAt = _clock.UtcNow;
        session.Status = status;
        session.AddressesChecked = scan?.AddressesChecked ?? 0;
        session.DevicesFound = scan?.Hosts.Count ?? 0;
        session.ErrorCount = scan?.ErrorCount ?? 0;
        session.ErrorSummary = error;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ScanSession>> GetHistoryAsync(int limit = 25, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ScanSessions.AsNoTracking().OrderByDescending(s => s.StartedAt).Take(limit).ToListAsync(ct);
    }

    public async Task PurgeStaleSessionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var stale = await db.ScanSessions.Where(s => s.Status == ScanStatus.Running).ToListAsync(ct);
        foreach (var s in stale)
        {
            s.Status = ScanStatus.Failed;
            s.CompletedAt = _clock.UtcNow;
            s.ErrorSummary = "The application closed or the scan was interrupted before completion.";
        }
        await db.SaveChangesAsync(ct);
    }
}

public sealed class MaintenanceService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly ISettingsService _settings;
    private readonly IBackupService _backup;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public MaintenanceService(IDbContextFactory<IpManagerDbContext> factory, ISettingsService settings,
        IBackupService backup, IClock clock, IAuditService audit)
    {
        _factory = factory;
        _settings = settings;
        _backup = backup;
        _clock = clock;
        _audit = audit;
    }

    public async Task<string> RunAsync(CancellationToken ct = default)
    {
        var s = _settings.Current;
        var now = _clock.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);

        var monitoringCutoff = now.AddDays(-s.MonitoringRetentionDays);
        var monitoring = await db.MonitoringResults
            .Where(r => r.Timestamp < monitoringCutoff)
            .ExecuteDeleteAsync(ct);

        var dnsCutoff = now.AddDays(-s.MonitoringRetentionDays);
        var dns = await db.DnsQueryLogs.Where(q => q.Timestamp < dnsCutoff).ExecuteDeleteAsync(ct);

        var auditCutoff = now.AddDays(-s.AuditRetentionDays);
        var audit = await db.AuditEvents.Where(a => a.Timestamp < auditCutoff).ExecuteDeleteAsync(ct);

        var conflictsCutoff = now.AddDays(-365);
        var conflicts = await db.IpConflicts
            .Where(c => c.State == ConflictState.Resolved && c.ResolvedAt < conflictsCutoff)
            .ExecuteDeleteAsync(ct);

        var backups = await _backup.PruneAsync(s.BackupRetentionCount, ct);
        await _audit.LogAsync("Maintenance", "Retention cleanup",
            $"monitoring {monitoring}, dns {dns}, audit {audit}, resolved conflicts {conflicts}, backups {backups}", ct);

        return $"Removed {monitoring:N0} monitoring results, {dns:N0} DNS log entries, {audit:N0} audit events, {conflicts:N0} old conflicts; pruned {backups} backup(s).";
    }
}

public sealed class OuiService
{
    private readonly OuiDatabase _database;
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAuditService _audit;

    public OuiService(OuiDatabase database, AppPaths paths, ISettingsService settings,
        IHttpClientFactory httpFactory, IAuditService audit)
    {
        _database = database;
        _paths = paths;
        _settings = settings;
        _httpFactory = httpFactory;
        _audit = audit;
    }

    public int Count => _database.Count;
    public DateTime? LastLoaded => _database.LoadedAt;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_paths.OuiDatabasePath))
                await _database.LoadFromFileAsync(_paths.OuiDatabasePath, ct);
        }
        catch
        {
            // the embedded seed remains available
        }
    }

    public async Task<int> UpdateFromIeeeAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var url = _settings.Current.OuiUpdateUrl;
        var client = _httpFactory.CreateClient("downloads");
        progress?.Report($"Downloading {url} ...");
        var data = await client.GetStringAsync(url, ct);
        if (!data.Contains("MA-L", StringComparison.OrdinalIgnoreCase) && !data.Contains("Registry", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded file does not look like the IEEE OUI registry CSV.");

        var temp = Path.Combine(Path.GetTempPath(), "ipmanager-oui-" + Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(temp, data, ct);
        var before = _database.Count;
        var added = await _database.LoadFromFileAsync(temp, ct);
        _paths.EnsureDirectories();
        await _database.SaveAsync(_paths.OuiDatabasePath, ct);
        try { File.Delete(temp); } catch { }
        await _audit.LogAsync("Discovery", "Updated OUI database", $"{added} new prefixes from {url}", ct);
        progress?.Report($"OUI database updated: {added:N0} new prefixes (total {_database.Count:N0}).");
        return _database.Count - before;
    }

    public async Task<int> ImportFileAsync(string path, CancellationToken ct = default)
    {
        var added = await _database.LoadFromFileAsync(path, ct);
        _paths.EnsureDirectories();
        await _database.SaveAsync(_paths.OuiDatabasePath, ct);
        await _audit.LogAsync("Discovery", "Imported OUI file", $"{path}: {added} new prefixes", ct);
        return added;
    }

    public string? Lookup(string? mac) => _database.Lookup(mac);
}
