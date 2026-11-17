using System.Text.Json;
using IPManager.Core;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Infrastructure;

public sealed class AppSettings
{
    // Appearance
    public string Theme { get; set; } = "Dark";
    public bool Use24HourClock { get; set; } = true;
    public int UiRefreshSeconds { get; set; } = 10;

    // Discovery
    public string DefaultScanRange { get; set; } = "";
    public string PreferredInterface { get; set; } = "";
    public int ScanTimeoutMs { get; set; } = 900;
    public int ScanConcurrency { get; set; } = 32;
    public int ScanRetryCount { get; set; } = 1;
    public int ScanDelayMs { get; set; } = 0;
    public bool EnableArp { get; set; } = true;
    public bool EnableIcmp { get; set; } = true;
    public bool EnableTcp { get; set; } = true;
    public bool EnableDns { get; set; } = true;
    public bool EnableReverseDns { get; set; } = true;
    public bool EnableMdns { get; set; } = true;
    public bool EnableNetBios { get; set; } = true;
    public string TcpProbePorts { get; set; } = "80,443,22,445,3389,8080,9100,631,554,5000";
    public bool RequireConfirmationForExternalRanges { get; set; } = true;
    public int MaxAddressesPerScan { get; set; } = 8192;
    public bool AutoScanOnStartup { get; set; } = false;

    // Monitoring
    public int MonitoringIntervalSeconds { get; set; } = 60;
    public int OfflineThresholdMinutes { get; set; } = 5;
    public int MonitoringTimeoutMs { get; set; } = 2000;
    public bool MonitorGateway { get; set; } = true;
    public bool PersistMonitoringResults { get; set; } = true;

    // Conflicts and alerts
    public int ConflictFreshMinutes { get; set; } = 15;
    public int ConflictStaleHours { get; set; } = 6;
    public int SubnetUtilizationThreshold { get; set; } = 85;
    public int DhcpExhaustionThreshold { get; set; } = 90;
    public int AlertCooldownMinutes { get; set; } = 30;
    public bool AlertOnNewDevices { get; set; } = true;
    public bool AlertOnUnknownDevices { get; set; } = true;
    public bool AlertOnDeviceOffline { get; set; } = true;
    public bool AlertOnConflicts { get; set; } = true;
    public bool RecoveryNotifications { get; set; } = true;

    // Notifications
    public bool TelegramEnabled { get; set; } = false;
    public string TelegramBotTokenProtected { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public bool EmailEnabled { get; set; } = false;
    public string EmailSmtpHost { get; set; } = "";
    public int EmailSmtpPort { get; set; } = 587;
    public string EmailFrom { get; set; } = "";
    public string EmailTo { get; set; } = "";
    public string EmailUsername { get; set; } = "";
    public string EmailPasswordProtected { get; set; } = "";
    public bool EmailUseTls { get; set; } = true;

    // Data
    public bool BackupOnStartup { get; set; } = true;
    public int BackupRetentionCount { get; set; } = 10;
    public int MonitoringRetentionDays { get; set; } = 30;
    public int AlertRetentionDays { get; set; } = 180;
    public int AuditRetentionDays { get; set; } = 365;
    public bool StartWithWindows { get; set; } = false;
    public bool ConfirmBeforeRemoteChanges { get; set; } = true;
    public string LogLevel { get; set; } = "Information";
    public string OuiUpdateUrl { get; set; } = "https://standards-oui.ieee.org/oui/oui.csv";
    public string LastPage { get; set; } = "Dashboard";
}

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
    T Get<T>(Func<AppSettings, T> selector);
    Task SetAsync(Action<AppSettings> mutate, CancellationToken ct = default);
    event EventHandler? Changed;
}

public sealed class SettingsService : ISettingsService
{
    private const string Key = "app.settings";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsService(IDbContextFactory<IpManagerDbContext> factory)
    {
        _factory = factory;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == Key, ct);
        if (row is not null)
        {
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(row.Value) ?? new AppSettings();
            }
            catch
            {
                Current = new AppSettings();
            }
        }
        else
        {
            Current = new AppSettings();
            db.Settings.Add(new AppSetting { Key = Key, Value = JsonSerializer.Serialize(Current, JsonOptions) });
            await db.SaveChangesAsync(ct);
        }

        Sanitize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Sanitize(Current);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            await using var db = await _factory.CreateDbContextAsync(ct);
            var row = await db.Settings.FirstOrDefaultAsync(x => x.Key == Key, ct);
            if (row is null) db.Settings.Add(new AppSetting { Key = Key, Value = json });
            else row.Value = json;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public T Get<T>(Func<AppSettings, T> selector) => selector(Current);

    public async Task SetAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        mutate(Current);
        await SaveAsync(ct);
    }

    private static void Sanitize(AppSettings s)
    {
        s.ScanConcurrency = Math.Clamp(s.ScanConcurrency, 1, 512);
        s.ScanTimeoutMs = Math.Clamp(s.ScanTimeoutMs, 100, 30000);
        s.ScanRetryCount = Math.Clamp(s.ScanRetryCount, 0, 5);
        s.ScanDelayMs = Math.Clamp(s.ScanDelayMs, 0, 5000);
        s.MonitoringIntervalSeconds = Math.Clamp(s.MonitoringIntervalSeconds, 15, 86400);
        s.OfflineThresholdMinutes = Math.Clamp(s.OfflineThresholdMinutes, 1, 10080);
        s.MonitoringTimeoutMs = Math.Clamp(s.MonitoringTimeoutMs, 200, 60000);
        s.UiRefreshSeconds = Math.Clamp(s.UiRefreshSeconds, 2, 300);
        s.ConflictFreshMinutes = Math.Clamp(s.ConflictFreshMinutes, 1, 1440);
        s.ConflictStaleHours = Math.Clamp(s.ConflictStaleHours, 1, 720);
        s.SubnetUtilizationThreshold = Math.Clamp(s.SubnetUtilizationThreshold, 1, 100);
        s.DhcpExhaustionThreshold = Math.Clamp(s.DhcpExhaustionThreshold, 1, 100);
        s.AlertCooldownMinutes = Math.Clamp(s.AlertCooldownMinutes, 0, 10080);
        s.MaxAddressesPerScan = Math.Clamp(s.MaxAddressesPerScan, 1, 65536);
        s.MonitoringRetentionDays = Math.Clamp(s.MonitoringRetentionDays, 1, 3650);
        s.AlertRetentionDays = Math.Clamp(s.AlertRetentionDays, 7, 3650);
        s.AuditRetentionDays = Math.Clamp(s.AuditRetentionDays, 7, 3650);
        s.BackupRetentionCount = Math.Clamp(s.BackupRetentionCount, 1, 100);
        if (s.Theme is not ("Dark" or "Light")) s.Theme = "Dark";
        if (string.IsNullOrWhiteSpace(s.TcpProbePorts)) s.TcpProbePorts = "80,443";
    }
}
