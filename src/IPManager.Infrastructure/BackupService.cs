using System.IO.Compression;
using IPManager.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Infrastructure;

public sealed record BackupInfo(string Path, DateTime CreatedUtc, long SizeBytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024.0:0.0} KB"
        : $"{SizeBytes / (1024.0 * 1024.0):0.0} MB";
}

public interface IBackupService
{
    Task<BackupInfo> CreateBackupAsync(string? label = null, CancellationToken ct = default);
    IReadOnlyList<BackupInfo> ListBackups();
    Task RestoreBackupAsync(string backupPath, CancellationToken ct = default);
    Task<int> PruneAsync(int retentionCount, CancellationToken ct = default);
    Task<string> ExportDatabasePackageAsync(string destinationZip, CancellationToken ct = default);
}

public sealed class BackupService : IBackupService
{
    private readonly AppPaths _paths;
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    private readonly IClock _clock;

    public BackupService(AppPaths paths, IDbContextFactory<IpManagerDbContext> factory, IClock clock)
    {
        _paths = paths;
        _factory = factory;
        _clock = clock;
    }

    public async Task<BackupInfo> CreateBackupAsync(string? label = null, CancellationToken ct = default)
    {
        _paths.EnsureDirectories();
        // Flush WAL content so a file-level copy is consistent.
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(FULL);", ct);
        }

        var safeLabel = string.IsNullOrWhiteSpace(label)
            ? ""
            : "-" + new string(label.Where(char.IsLetterOrDigit).Take(24).ToArray());
        var file = Path.Combine(_paths.BackupDirectory, $"ipmanager-{_clock.UtcNow:yyyyMMdd-HHmmss}{safeLabel}.db");

        // Pooling must stay off: a pooled handle would keep the backup file open and block a later restore.
        await using (var source = new SqliteConnection($"Data Source={_paths.DatabasePath};Mode=ReadOnly;Pooling=False"))
        await using (var destination = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            await source.OpenAsync(ct);
            await destination.OpenAsync(ct);
            source.BackupDatabase(destination);
        }

        var info = new FileInfo(file);
        PruneOldFiles();
        return new BackupInfo(file, info.LastWriteTimeUtc, info.Length);
    }

    public IReadOnlyList<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(_paths.BackupDirectory)) return Array.Empty<BackupInfo>();
        return Directory.GetFiles(_paths.BackupDirectory, "ipmanager-*.db")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.FullName, f.LastWriteTimeUtc, f.Length))
            .ToList();
    }

    public async Task RestoreBackupAsync(string backupPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup file not found.", backupPath);
        ValidateSqliteFile(backupPath);

        // Keep a safety copy of the current database before overwriting it.
        await CreateBackupAsync("pre-restore", ct);
        SqliteConnection.ClearAllPools();
        await Task.Delay(150, ct);
        File.Copy(backupPath, _paths.DatabasePath, true);
    }

    public Task<int> PruneAsync(int retentionCount, CancellationToken ct = default)
    {
        if (retentionCount < 1) retentionCount = 1;
        var files = ListBackups().Skip(retentionCount).ToList();
        foreach (var f in files)
        {
            try { File.Delete(f.Path); } catch { /* locked files are retried next time */ }
        }
        return Task.FromResult(files.Count);
    }

    public async Task<string> ExportDatabasePackageAsync(string destinationZip, CancellationToken ct = default)
    {
        var backup = await CreateBackupAsync("export", ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZip)!);
        if (File.Exists(destinationZip)) File.Delete(destinationZip);
        using (var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(backup.Path, "ipmanager.db");
            var settingsPath = Path.Combine(_paths.DataDirectory, "config.json");
            if (File.Exists(settingsPath)) zip.CreateEntryFromFile(settingsPath, "config.json");
        }
        return destinationZip;
    }

    private void PruneOldFiles()
    {
        try
        {
            var old = ListBackups().Skip(20).ToList();
            foreach (var b in old) File.Delete(b.Path);
        }
        catch
        {
            // Retention failures must never block a backup.
        }
    }

    private static void ValidateSqliteFile(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = fs.Read(header);
        var expected = "SQLite format 3\0"u8;
        if (read < 16 || !header.SequenceEqual(expected))
            throw new InvalidDataException("The selected file is not a valid IP Manager SQLite database.");
    }
}
