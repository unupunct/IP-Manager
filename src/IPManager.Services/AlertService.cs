using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using IPManager.Core;
using IPManager.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPManager.Services;

public interface IAlertService
{
    Task<Alert?> RaiseAsync(AlertType type, AlertSeverity severity, string title, string message,
        string? entityType = null, int? entityId = null, string? ipAddress = null,
        string? cooldownKey = null, CancellationToken ct = default);

    Task RecoverAsync(string cooldownKey, string title, string message, CancellationToken ct = default);
    Task<bool> AcknowledgeAsync(int alertId, bool acknowledged = true, CancellationToken ct = default);
    Task<int> AcknowledgeAllAsync(CancellationToken ct = default);
    Task<List<Alert>> GetAlertsAsync(bool includeAcknowledged = false, AlertSeverity? minSeverity = null,
        AlertType? type = null, int limit = 500, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);
    Task MarkAllReadAsync(CancellationToken ct = default);
    Task<int> PurgeOldAsync(int retentionDays, CancellationToken ct = default);
    event EventHandler<Alert>? AlertRaised;
}

public sealed class AlertService : IAlertService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly ISettingsService _settings;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly ILogger<AlertService> _log;

    public AlertService(IDbContextFactory<IpManagerDbContext> factory, ISettingsService settings, IClock clock,
        IAuditService audit, IEnumerable<INotificationChannel> channels, ILogger<AlertService> log)
    {
        _factory = factory;
        _settings = settings;
        _clock = clock;
        _audit = audit;
        _channels = channels;
        _log = log;
    }

    public event EventHandler<Alert>? AlertRaised;

    public async Task<Alert?> RaiseAsync(AlertType type, AlertSeverity severity, string title, string message,
        string? entityType = null, int? entityId = null, string? ipAddress = null,
        string? cooldownKey = null, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var key = cooldownKey ?? $"{type}:{entityId?.ToString() ?? ipAddress ?? title}";
        var cooldown = TimeSpan.FromMinutes(_settings.Current.AlertCooldownMinutes);

        await using var db = await _factory.CreateDbContextAsync(ct);

        if (cooldown > TimeSpan.Zero)
        {
            var since = now - cooldown;
            var duplicate = await db.Alerts.AnyAsync(a =>
                a.CooldownKey == key && a.CreatedAt >= since && a.RecoveredAt == null && !a.IsAcknowledged, ct);
            if (duplicate) return null;
        }

        var alert = new Alert
        {
            Type = type,
            Severity = severity,
            Title = title,
            Message = message,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = ipAddress,
            CreatedAt = now,
            CooldownKey = key
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);

        await _audit.LogAsync("Alert", type.ToString(), $"{title}: {message}", ct);
        AlertRaised?.Invoke(this, alert);

        _ = Task.Run(async () =>
        {
            foreach (var channel in _channels)
            {
                try
                {
                    if (!channel.IsEnabled(_settings.Current)) continue;
                    await channel.SendAsync(alert, _settings.Current, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Notification channel {Channel} failed", channel.Name);
                    await _audit.LogAsync("Notification", $"Channel {channel.Name} failed", ex.Message, CancellationToken.None);
                }
            }
        }, CancellationToken.None);

        return alert;
    }

    public async Task RecoverAsync(string cooldownKey, string title, string message, CancellationToken ct = default)
    {
        if (!_settings.Current.RecoveryNotifications) return;
        var now = _clock.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var candidates = await db.Alerts
            .Where(a => a.CooldownKey == cooldownKey && a.RecoveredAt == null)
            .ToListAsync(ct);
        foreach (var c in candidates)
        {
            c.IsRecovered = true;
            c.RecoveredAt = now;
        }

        var recovery = new Alert
        {
            Type = AlertType.DeviceOnline,
            Severity = AlertSeverity.Info,
            Title = title,
            Message = message,
            CreatedAt = now,
            CooldownKey = "recovery:" + cooldownKey
        };
        db.Alerts.Add(recovery);
        await db.SaveChangesAsync(ct);
        AlertRaised?.Invoke(this, recovery);
    }

    public async Task<bool> AcknowledgeAsync(int alertId, bool acknowledged = true, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return false;
        alert.IsAcknowledged = acknowledged;
        alert.AcknowledgedAt = acknowledged ? _clock.UtcNow : null;
        alert.IsRead = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> AcknowledgeAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = _clock.UtcNow;
        return await db.Alerts.Where(a => !a.IsAcknowledged)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IsAcknowledged, true)
                .SetProperty(a => a.AcknowledgedAt, now)
                .SetProperty(a => a.IsRead, true), ct);
    }

    public async Task<List<Alert>> GetAlertsAsync(bool includeAcknowledged = false, AlertSeverity? minSeverity = null,
        AlertType? type = null, int limit = 500, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.Alerts.AsNoTracking();
        if (!includeAcknowledged) q = q.Where(a => !a.IsAcknowledged);
        if (minSeverity is not null) q = q.Where(a => a.Severity >= minSeverity);
        if (type is not null) q = q.Where(a => a.Type == type);
        return await q.OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(limit, 1, 5000)).ToListAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Alerts.CountAsync(a => !a.IsRead, ct);
    }

    public async Task MarkAllReadAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Alerts.Where(a => !a.IsRead).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRead, true), ct);
    }

    public async Task<int> PurgeOldAsync(int retentionDays, CancellationToken ct = default)
    {
        var cutoff = _clock.UtcNow.AddDays(-Math.Max(1, retentionDays));
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Alerts.Where(a => a.CreatedAt < cutoff && a.IsAcknowledged).ExecuteDeleteAsync(ct);
    }
}

public interface INotificationChannel
{
    string Name { get; }
    bool IsEnabled(AppSettings settings);
    Task SendAsync(Alert alert, AppSettings settings, CancellationToken ct);
}

public sealed class TelegramNotificationChannel : INotificationChannel
{
    private readonly HttpClient _http;
    private readonly ISecretProtector _protector;
    private readonly ILogger<TelegramNotificationChannel> _log;

    public TelegramNotificationChannel(IHttpClientFactory factory, ISecretProtector protector, ILogger<TelegramNotificationChannel> log)
    {
        _http = factory.CreateClient("notifications");
        _protector = protector;
        _log = log;
    }

    public string Name => "Telegram";

    public bool IsEnabled(AppSettings settings) =>
        settings.TelegramEnabled && !string.IsNullOrWhiteSpace(settings.TelegramBotTokenProtected) &&
        !string.IsNullOrWhiteSpace(settings.TelegramChatId);

    public async Task SendAsync(Alert alert, AppSettings settings, CancellationToken ct)
    {
        var token = _protector.TryUnprotect(settings.TelegramBotTokenProtected);
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("Telegram is enabled but the bot token cannot be decrypted.");
            return;
        }

        var text = $"*{Escape(alert.Title)}*\n{alert.Message}\nSeverity: {alert.Severity}\nTime: {alert.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC";
        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        using var response = await _http.PostAsJsonAsync(url, new { chat_id = settings.TelegramChatId, text, parse_mode = "Markdown" }, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Telegram API returned {(int)response.StatusCode}: {Truncate(body, 300)}");
        }
    }

    private static string Escape(string text) => text.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`");
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

public sealed class EmailNotificationChannel : INotificationChannel
{
    private readonly ISecretProtector _protector;

    public EmailNotificationChannel(ISecretProtector protector) => _protector = protector;

    public string Name => "Email";

    public bool IsEnabled(AppSettings settings) =>
        settings.EmailEnabled && !string.IsNullOrWhiteSpace(settings.EmailSmtpHost) &&
        !string.IsNullOrWhiteSpace(settings.EmailTo) && !string.IsNullOrWhiteSpace(settings.EmailFrom);

    public async Task SendAsync(Alert alert, AppSettings settings, CancellationToken ct)
    {
        using var message = new MailMessage(settings.EmailFrom, settings.EmailTo)
        {
            Subject = $"[IP Manager] {alert.Severity}: {alert.Title}",
            Body = $"{alert.Title}\n\n{alert.Message}\n\nSeverity: {alert.Severity}\nTime: {alert.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC\nIP: {alert.IpAddress ?? "-"}",
            IsBodyHtml = false
        };

        using var client = new SmtpClient(settings.EmailSmtpHost, settings.EmailSmtpPort)
        {
            EnableSsl = settings.EmailUseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        var password = _protector.TryUnprotect(settings.EmailPasswordProtected);
        if (!string.IsNullOrWhiteSpace(settings.EmailUsername))
            client.Credentials = new NetworkCredential(settings.EmailUsername, password ?? "");

        await client.SendMailAsync(message, ct);
    }
}
