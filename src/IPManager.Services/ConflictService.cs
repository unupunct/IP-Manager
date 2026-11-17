using IPManager.Core;
using IPManager.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

public sealed record ConflictRunResult(int Active, int Confirmed, int Suspected, int New, int Resolved, int DatabaseDuplicates);

public sealed class ConflictService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly ISettingsService _settings;
    private readonly IClock _clock;
    private readonly IAlertService _alerts;
    private readonly IAuditService _audit;

    public ConflictService(IDbContextFactory<IpManagerDbContext> factory, ISettingsService settings, IClock clock,
        IAlertService alerts, IAuditService audit)
    {
        _factory = factory;
        _settings = settings;
        _clock = clock;
        _alerts = alerts;
        _audit = audit;
    }

    public async Task<ConflictRunResult> RunAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var settings = _settings.Current;
        var options = new ConflictAnalyzerOptions
        {
            FreshWindow = TimeSpan.FromMinutes(settings.ConflictFreshMinutes),
            StaleWindow = TimeSpan.FromHours(settings.ConflictStaleHours)
        };

        await using var db = await _factory.CreateDbContextAsync(ct);
        var observationCutoff = now.AddHours(-Math.Max(24, settings.ConflictStaleHours * 2));
        var observations = await db.MacObservations.AsNoTracking()
            .Where(o => o.LastSeen >= observationCutoff)
            .ToListAsync(ct);
        var addressEntries = await db.IpAddresses.AsNoTracking().Include(a => a.Device).ToListAsync(ct);
        var reservations = await db.DhcpReservations.AsNoTracking().ToListAsync(ct);

        var findings = ConflictAnalyzer.AnalyzeNetwork(observations, now, options).ToList();
        findings.AddRange(ConflictAnalyzer.AnalyzeDatabase(addressEntries));

        // Duplicate DHCP reservations within one scope are documentation conflicts.
        foreach (var group in reservations.GroupBy(r => new { r.DhcpScopeId, r.IpAddress }).Where(g => g.Count() > 1))
        {
            var scope = await db.DhcpScopes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == group.Key.DhcpScopeId, ct);
            findings.Add(new ConflictFinding
            {
                IpAddress = group.Key.IpAddress,
                MacAddresses = group.Select(r => r.MacAddress).Distinct().ToList(),
                Method = ConflictMethod.DuplicateReservation,
                State = ConflictState.Suspected,
                Confidence = 100,
                Evidence = $"The DHCP scope '{scope?.Name ?? group.Key.DhcpScopeId.ToString()}' contains {group.Count()} reservations for {group.Key.IpAddress}.",
                LastSeen = group.Max(r => r.UpdatedAt),
                SubnetId = scope?.SubnetId
            });
        }

        var active = await db.IpConflicts.Where(c => c.State != ConflictState.Resolved).ToListAsync(ct);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newCount = 0;
        var resolvedCount = 0;
        var databaseDuplicates = findings.Count(f => f.Method == ConflictMethod.DuplicateDatabaseAssignment);

        foreach (var finding in findings)
        {
            var key = $"{finding.IpAddress}|{finding.Method}";
            seenKeys.Add(key);
            var existing = active.FirstOrDefault(c => c.IpAddress == finding.IpAddress && c.Method == finding.Method);
            if (existing is null)
            {
                var conflict = new IpConflict
                {
                    IpAddress = finding.IpAddress,
                    MacAddresses = string.Join(", ", finding.MacAddresses),
                    Method = finding.Method,
                    State = finding.State,
                    Confidence = finding.Confidence,
                    Evidence = finding.Evidence,
                    DetectedAt = now,
                    LastSeenAt = finding.LastSeen == default ? now : finding.LastSeen,
                    SubnetId = finding.SubnetId
                };
                db.IpConflicts.Add(conflict);
                newCount++;

                if (settings.AlertOnConflicts)
                {
                    await _alerts.RaiseAsync(AlertType.IpConflict,
                        finding.State == ConflictState.Confirmed ? AlertSeverity.Critical : AlertSeverity.Warning,
                        finding.State == ConflictState.Confirmed ? "IP conflict confirmed" : "Suspected IP conflict",
                        $"Address {finding.IpAddress} has conflicting evidence ({finding.Method}). {finding.MacAddresses.Count} MAC address(es) involved. Confidence {finding.Confidence}%.",
                        "IpConflict", null, finding.IpAddress, $"conflict:{finding.IpAddress}:{finding.Method}", ct);
                }
            }
            else
            {
                existing.State = finding.State;
                existing.Confidence = finding.Confidence;
                existing.MacAddresses = string.Join(", ", finding.MacAddresses);
                existing.Evidence = finding.Evidence;
                existing.LastSeenAt = finding.LastSeen == default ? now : finding.LastSeen;
            }
        }

        // Auto-resolve only when fresh evidence shows the address has a single MAC again.
        foreach (var conflict in active.Where(c => c.State != ConflictState.Resolved))
        {
            if (seenKeys.Contains($"{conflict.IpAddress}|{conflict.Method}")) continue;
            var recent = observations.Where(o => o.IpAddress == conflict.IpAddress &&
                                                 now - o.LastSeen <= TimeSpan.FromMinutes(settings.ConflictFreshMinutes))
                .Select(o => o.MacAddress).Distinct().ToList();
            var singleFreshMac = recent.Count == 1;
            var duplicateGone = conflict.Method is ConflictMethod.DuplicateDatabaseAssignment or ConflictMethod.DuplicateReservation;

            if (singleFreshMac || duplicateGone)
            {
                conflict.State = ConflictState.Resolved;
                conflict.ResolvedAt = now;
                conflict.Notes = (conflict.Notes + "\n" + $"Auto-resolved at {now:u}: fresh observations show a single MAC / duplicate no longer present.").Trim();
                resolvedCount++;
            }
        }

        await db.SaveChangesAsync(ct);
        var actives = await db.IpConflicts.AsNoTracking().Where(c => c.State != ConflictState.Resolved).ToListAsync(ct);
        await _audit.LogAsync("Conflict", "Conflict analysis completed",
            $"{findings.Count} findings, {newCount} new, {resolvedCount} resolved", ct);

        return new ConflictRunResult(actives.Count,
            actives.Count(c => c.State == ConflictState.Confirmed),
            actives.Count(c => c.State == ConflictState.Suspected),
            newCount, resolvedCount, databaseDuplicates);
    }

    public async Task<List<IpConflict>> GetConflictsAsync(bool includeResolved = false, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.IpConflicts.AsNoTracking();
        if (!includeResolved) q = q.Where(c => c.State != ConflictState.Resolved);
        return await q.OrderByDescending(c => c.LastSeenAt).ToListAsync(ct);
    }

    public async Task<List<MacObservation>> GetObservationsAsync(string ipAddress, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MacObservations.AsNoTracking()
            .Where(o => o.IpAddress == ipAddress)
            .OrderByDescending(o => o.LastSeen)
            .ToListAsync(ct);
    }

    public async Task ResolveAsync(int conflictId, string? note = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conflict = await db.IpConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct);
        if (conflict is null) return;
        conflict.State = ConflictState.Resolved;
        conflict.ResolvedAt = _clock.UtcNow;
        conflict.Notes = string.IsNullOrWhiteSpace(note) ? conflict.Notes : $"{conflict.Notes}\n{note}".Trim();
        await db.SaveChangesAsync(ct);
        await _audit.LogAsync("Conflict", "Manually resolved conflict", conflict.IpAddress, ct);
    }

    public async Task ReopenAsync(int conflictId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conflict = await db.IpConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct);
        if (conflict is null) return;
        conflict.State = ConflictState.Suspected;
        conflict.ResolvedAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<MacObservation>> GetRecentObservationsAsync(int hours = 24, CancellationToken ct = default)
    {
        var cutoff = _clock.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * 30));
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MacObservations.AsNoTracking()
            .Where(o => o.LastSeen >= cutoff)
            .OrderByDescending(o => o.LastSeen)
            .ToListAsync(ct);
    }
}
