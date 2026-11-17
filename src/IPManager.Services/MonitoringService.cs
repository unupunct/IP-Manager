using System.Net;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPManager.Services;

public sealed record MonitoringObservation
{
    public int DeviceId { get; init; }
    public string DeviceName { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public bool Success { get; init; }
    public double? ResponseMs { get; init; }
    public MonitoringMethod Method { get; init; }
    public string? Detail { get; init; }
}

public sealed record AvailabilityBucket(DateTime Start, int Total, int Success)
{
    public double? Percent => Total == 0 ? null : Math.Round(Success * 100.0 / Total, 1);
}

public sealed record UptimeStats(int DeviceId, int TotalChecks, int Successes, int Failures, double? UptimePercent,
    DateTime? LastCheck, DateTime? LastSuccess, IReadOnlyList<AvailabilityBucket> Buckets)
{
    public string UptimeText => UptimePercent is null ? "No monitoring data" : $"{UptimePercent:0.0}%";
}

public sealed class MonitoringService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly ISettingsService _settings;
    private readonly IAlertService _alerts;
    private readonly IClock _clock;
    private readonly ILogger<MonitoringService> _log;

    public MonitoringService(IDbContextFactory<IpManagerDbContext> factory, ISettingsService settings,
        IAlertService alerts, IClock clock, ILogger<MonitoringService> log)
    {
        _factory = factory;
        _settings = settings;
        _alerts = alerts;
        _clock = clock;
        _log = log;
    }

    public async Task<MonitorCycleSummary> RunCycleAsync(CancellationToken ct = default)
    {
        var s = _settings.Current;
        var now = _clock.UtcNow;
        var offlineThreshold = TimeSpan.FromMinutes(s.OfflineThresholdMinutes);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var devices = await db.Devices
            .Include(d => d.MonitoringTargets)
            .Where(d => !d.ExcludeFromMonitoring && d.IpAddress != null)
            .ToListAsync(ct);

        var checkedCount = 0;
        var onlineCount = 0;
        var offlineCount = 0;
        var gateways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(s.ScanConcurrency / 4, 1, 16), CancellationToken = ct };

        try
        {
            await Parallel.ForEachAsync(devices, options, async (device, token) =>
            {
                var result = await CheckDeviceAsync(device, token);
                Interlocked.Increment(ref checkedCount);
                if (result.Success) Interlocked.Increment(ref onlineCount);
                else Interlocked.Increment(ref offlineCount);

                await using var write = await _factory.CreateDbContextAsync(token);
                var tracked = await write.Devices.FirstOrDefaultAsync(d => d.Id == device.Id, token);
                if (tracked is null) return;

                if (s.PersistMonitoringResults)
                {
                    write.MonitoringResults.Add(new MonitoringResult
                    {
                        DeviceId = device.Id,
                        TargetId = result.TargetId,
                        Timestamp = result.Timestamp,
                        Success = result.Success,
                        ResponseMs = result.ResponseMs,
                        Method = result.Method,
                        Detail = result.Detail
                    });
                }

                var wasOffline = tracked.Status == DeviceStatus.Offline;
                if (result.Success)
                {
                    tracked.Status = DeviceStatus.Online;
                    tracked.LastSeen = result.Timestamp;
                    tracked.LastSeenOnline = result.Timestamp;

                    if (s.RecoveryNotifications && wasOffline)
                    {
                        await _alerts.RecoverAsync($"device-offline:{device.Id}", "Device back online",
                            $"{device.Name} ({device.IpAddress}) is responding again.", token);
                    }
                }
                else
                {
                    var lastSuccess = await write.MonitoringResults.AsNoTracking()
                        .Where(r => r.DeviceId == device.Id && r.Success)
                        .OrderByDescending(r => r.Timestamp)
                        .Select(r => (DateTime?)r.Timestamp)
                        .FirstOrDefaultAsync(token) ?? tracked.LastSeenOnline;

                    if (lastSuccess is null || now - lastSuccess.Value >= offlineThreshold)
                    {
                        tracked.Status = DeviceStatus.Offline;
                        if (s.AlertOnDeviceOffline)
                        {
                            await _alerts.RaiseAsync(AlertType.DeviceOffline, tracked.IsImportant ? AlertSeverity.Critical : AlertSeverity.Warning,
                                "Device offline", $"{tracked.Name} ({tracked.IpAddress}) has not responded since {lastSuccess?.ToString("u") ?? "the last check"}.",
                                "Device", tracked.Id, tracked.IpAddress, $"device-offline:{tracked.Id}", token);
                        }
                    }
                }

                await write.SaveChangesAsync(token);

                if (s.MonitorGateway)
                {
                    var subnets = await write.Subnets.AsNoTracking()
                        .Where(x => x.MonitoringEnabled && x.Gateway != null)
                        .Select(x => x.Gateway!)
                        .ToListAsync(token);
                    foreach (var g in subnets) gateways.Add(g);
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        if (s.MonitorGateway)
        {
            foreach (var gateway in gateways)
            {
                var ping = await PingService.PingAsync(gateway, s.MonitoringTimeoutMs, 0, ct: ct);
                if (!ping.Success)
                {
                    await _alerts.RaiseAsync(AlertType.GatewayUnavailable, AlertSeverity.Critical,
                        "Gateway unavailable", $"The configured gateway {gateway} did not respond to ICMP.",
                        "Gateway", null, gateway, $"gateway-down:{gateway}", ct);
                }
            }
        }

        return new MonitorCycleSummary(checkedCount, onlineCount, offlineCount, gateways.Count);
    }

    private async Task<CheckOutcome> CheckDeviceAsync(Device device, CancellationToken ct)
    {
        var s = _settings.Current;
        var targets = device.MonitoringTargets.Where(t => t.Enabled).ToList();
        if (targets.Count == 0)
            targets.Add(new MonitoringTarget { DeviceId = device.Id, Method = MonitoringMethod.Icmp, TimeoutMs = s.MonitoringTimeoutMs });

        MonitoringTarget? usedTarget = null;
        var success = false;
        double? bestResponse = null;
        var details = new List<string>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await CheckTargetAsync(device, target, ct);
            details.Add(outcome.Detail ?? "");
            if (outcome.Success)
            {
                success = true;
                usedTarget = target;
                bestResponse = outcome.ResponseMs;
                break;
            }
        }

        return new CheckOutcome(success, bestResponse, usedTarget?.Method ?? targets[0].Method,
            usedTarget?.Id, _clock.UtcNow, string.Join("; ", details.Where(d => d.Length > 0)));
    }

    private async Task<(bool Success, long? ResponseMs, string? Detail)> CheckTargetAsync(Device device, MonitoringTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress))
            return (false, null, "No IP address");

        switch (target.Method)
        {
            case MonitoringMethod.Icmp:
            {
                var ping = await PingService.PingAsync(device.IpAddress, target.TimeoutMs, 0, ct: ct);
                return (ping.Success, ping.RoundtripMs, ping.Success ? null : ping.Status);
            }
            case MonitoringMethod.Tcp:
            {
                if (target.Port is null) return (false, null, "No TCP port configured");
                var probe = await TcpProbeService.ProbeAsync(device.IpAddress, target.Port.Value, target.TimeoutMs, ct);
                return (probe.Open, probe.ConnectMs, probe.Open ? null : probe.Status);
            }
            case MonitoringMethod.Http:
            {
                var url = target.Url;
                if (string.IsNullOrWhiteSpace(url))
                    url = $"http://{device.IpAddress}{(target.Port is null or 80 ? "" : $":{target.Port}")}/";
                try
                {
                    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(target.TimeoutMs) };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    sw.Stop();
                    return ((int)response.StatusCode < 500, sw.ElapsedMilliseconds, $"HTTP {(int)response.StatusCode}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return (false, null, ex.Message);
                }
            }
            case MonitoringMethod.Dns:
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var entries = await Dns.GetHostAddressesAsync(device.IpAddress, ct);
                    sw.Stop();
                    return (entries.Length > 0, sw.ElapsedMilliseconds, null);
                }
                catch
                {
                    return (false, null, "DNS resolution failed");
                }
            }
            default:
                return (false, null, "Unsupported method");
        }
    }

    public async Task<List<MonitoringObservation>> GetHistoryAsync(int deviceId, int hours = 24, CancellationToken ct = default)
    {
        var cutoff = _clock.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * 90));
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.MonitoringResults.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.Timestamp >= cutoff)
            .OrderBy(r => r.Timestamp)
            .ToListAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        return rows.Select(r => new MonitoringObservation
        {
            DeviceId = r.DeviceId,
            DeviceName = device?.Name ?? "",
            Timestamp = r.Timestamp,
            Success = r.Success,
            ResponseMs = r.ResponseMs,
            Method = r.Method,
            Detail = r.Detail
        }).ToList();
    }

    public async Task<UptimeStats> GetUptimeAsync(int deviceId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var now = _clock.UtcNow;
        var start = now.Date.AddDays(-(days - 1));
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.MonitoringResults.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.Timestamp >= start)
            .ToListAsync(ct);

        var successes = rows.Count(r => r.Success);
        var failures = rows.Count(r => !r.Success);
        double? percent = rows.Count == 0 ? null : Math.Round(successes * 100.0 / rows.Count, 2);

        var buckets = new List<AvailabilityBucket>();
        for (var i = 0; i < days; i++)
        {
            var day = start.AddDays(i);
            var dayRows = rows.Where(r => r.Timestamp >= day && r.Timestamp < day.AddDays(1)).ToList();
            buckets.Add(new AvailabilityBucket(day, dayRows.Count, dayRows.Count(r => r.Success)));
        }

        return new UptimeStats(deviceId, rows.Count, successes, failures, percent,
            rows.Count == 0 ? null : rows.Max(r => r.Timestamp),
            rows.Where(r => r.Success).Select(r => (DateTime?)r.Timestamp).Max(),
            buckets);
    }

    public async Task<List<MonitoringTarget>> GetTargetsAsync(int deviceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.MonitoringTargets.AsNoTracking().Where(t => t.DeviceId == deviceId).OrderBy(t => t.Method).ToListAsync(ct);
    }

    public async Task<MonitoringTarget> SaveTargetAsync(MonitoringTarget target, CancellationToken ct = default)
    {
        if (target.Method == MonitoringMethod.Tcp && target.Port is null)
            throw new ArgumentException("A TCP monitoring target requires a port.");
        if (target.Port is not null && target.Port is < 1 or > 65535)
            throw new ArgumentException("The port must be between 1 and 65535.");
        target.IntervalSeconds = Math.Clamp(target.IntervalSeconds, 15, 86400);
        target.TimeoutMs = Math.Clamp(target.TimeoutMs, 200, 60000);

        await using var db = await _factory.CreateDbContextAsync(ct);
        if (target.Id == 0)
        {
            target.CreatedAt = _clock.UtcNow;
            db.MonitoringTargets.Add(target);
        }
        else
        {
            var existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.Id == target.Id, ct)
                           ?? throw new InvalidOperationException("The monitoring target no longer exists.");
            existing.Method = target.Method;
            existing.Port = target.Port;
            existing.Url = target.Url;
            existing.IntervalSeconds = target.IntervalSeconds;
            existing.TimeoutMs = target.TimeoutMs;
            existing.Enabled = target.Enabled;
        }
        await db.SaveChangesAsync(ct);
        return target;
    }

    public async Task DeleteTargetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var target = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (target is null) return;
        db.MonitoringTargets.Remove(target);
        await db.SaveChangesAsync(ct);
    }

    private sealed record CheckOutcome(bool Success, double? ResponseMs, MonitoringMethod Method, int? TargetId, DateTime Timestamp, string? Detail);
}

public sealed record MonitorCycleSummary(int Checked, int Online, int Offline, int Gateways)
{
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}

public sealed class MonitoringBackgroundService : BackgroundService
{
    private readonly ISettingsService _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MonitoringBackgroundService> _log;
    public event EventHandler<MonitorCycleSummary>? CycleCompleted;

    public MonitoringBackgroundService(ISettingsService settings, IServiceScopeFactory scopeFactory, ILogger<MonitoringBackgroundService> log)
    {
        _settings = settings;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the UI and database a moment before the first cycle.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(15, _settings.Current.MonitoringIntervalSeconds));
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<MonitoringService>();
                var summary = await service.RunCycleAsync(stoppingToken);
                CycleCompleted?.Invoke(this, summary);
                _log.LogInformation("Monitoring cycle finished: {Checked} devices, {Online} online, {Offline} offline", summary.Checked, summary.Online, summary.Offline);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Monitoring cycle failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
