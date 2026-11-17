using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IPManager.Infrastructure;

/// <summary>Used by `dotnet ef` at design time. Runtime configuration lives in AppPaths/DI.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<IpManagerDbContext>
{
    public IpManagerDbContext CreateDbContext(string[] args)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPManager");
        Directory.CreateDirectory(dir);
        var options = new DbContextOptionsBuilder<IpManagerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dir, "ipmanager.db")}")
            .Options;
        return new IpManagerDbContext(options);
    }
}
