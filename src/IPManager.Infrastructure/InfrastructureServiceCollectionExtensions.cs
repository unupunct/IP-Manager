using IPManager.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IPManager.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddIpManagerInfrastructure(
        this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SessionSecrets>();
        services.AddSingleton<IAuditService, AuditService>();
        services.AddSingleton<IBackupService, BackupService>();

        services.AddDbContextFactory<IpManagerDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath};Cache=Shared", sqlite =>
            {
                sqlite.CommandTimeout(30);
            }));

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}

public interface IAuditService
{
    Task LogAsync(string category, string action, string? detail = null, CancellationToken ct = default);
}

public sealed class DatabaseInitializer
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly AppPaths _paths;
    private readonly IAuditService _audit;

    public DatabaseInitializer(IDbContextFactory<IpManagerDbContext> factory, AppPaths paths, IAuditService audit)
    {
        _factory = factory;
        _paths = paths;
        _audit = audit;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _paths.EnsureDirectories();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        if (!File.Exists(_paths.OuiDatabasePath))
        {
            var seed = OuiDatabase.CreateWithSeed();
            await seed.SaveAsync(_paths.OuiDatabasePath, ct);
        }

        await _audit.LogAsync("Application", "Database ready", _paths.DatabasePath, ct);
    }
}
