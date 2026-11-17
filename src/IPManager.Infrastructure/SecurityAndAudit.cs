using System.Security.Cryptography;
using System.Text;
using IPManager.Core;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Infrastructure;

/// <summary>Windows DPAPI (current user) protection for integration secrets.</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string? TryUnprotect(string? protectedBase64);
    bool IsAvailable { get; }
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPManager.Secret.v1");

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        if (!OperatingSystem.IsWindows()) return ""; // never fall back to plain text
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string? TryUnprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = Convert.FromBase64String(protectedBase64);
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Non-secret, in-memory store for secrets typed by the user during a session.</summary>
public sealed class SessionSecrets
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public void Set(string key, string value) => _values[key] = value;
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Clear() => _values.Clear();
}

public sealed class AuditService : IAuditService
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly IClock _clock;

    public AuditService(IDbContextFactory<IpManagerDbContext> factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task LogAsync(string category, string action, string? detail = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.AuditEvents.Add(new AuditEvent
        {
            Timestamp = _clock.UtcNow,
            Category = Truncate(category, 64),
            Action = Truncate(action, 128),
            Detail = detail is null ? null : Truncate(detail, 2000),
            User = Environment.UserName
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
