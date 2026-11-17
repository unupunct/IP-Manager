using System.Text.Json;

namespace IPManager.Infrastructure;

public sealed class AppPaths
{
    public const string ProductName = "IP Manager";
    public const string DataFolderName = "IPManager";

    public string DataDirectory { get; private set; } = "";
    public string DatabasePath { get; private set; } = "";
    public string BackupDirectory { get; private set; } = "";
    public string LogDirectory { get; private set; } = "";
    public string ExportDirectory { get; private set; } = "";
    public string OuiDirectory { get; private set; } = "";
    public string ConfigPath { get; private set; } = "";
    public bool IsPortable { get; private set; }

    public string OuiDatabasePath => Path.Combine(OuiDirectory, "oui-database.csv");

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName, "config.json");

    public static AppPaths Load()
    {
        var paths = new AppPaths();
        var configPath = DefaultConfigPath;
        paths.ConfigPath = configPath;
        string? configuredRoot = null;
        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("dataDirectory", out var el))
                    configuredRoot = el.GetString();
            }
        }
        catch
        {
            configuredRoot = null; // a damaged config falls back to the default location
        }

        var portableFlag = Path.Combine(AppContext.BaseDirectory, "portable.flag");
        if (File.Exists(portableFlag))
        {
            paths.IsPortable = true;
            configuredRoot = Path.Combine(AppContext.BaseDirectory, "data");
        }

        paths.SetDataDirectory(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName)
            : configuredRoot);

        if (!paths.IsPortable) paths.EnsureDirectories();
        return paths;
    }

    /// <summary>Creates an isolated path set (used by tests and portable diagnostics).</summary>
    public static AppPaths Create(string dataDirectory)
    {
        var paths = new AppPaths
        {
            ConfigPath = Path.Combine(dataDirectory, "config.json")
        };
        paths.SetDataDirectory(dataDirectory);
        paths.EnsureDirectories();
        return paths;
    }

    private void SetDataDirectory(string dir)    {
        DataDirectory = dir;
        DatabasePath = Path.Combine(dir, "ipmanager.db");
        BackupDirectory = Path.Combine(dir, "backups");
        LogDirectory = Path.Combine(dir, "logs");
        ExportDirectory = Path.Combine(dir, "exports");
        OuiDirectory = Path.Combine(dir, "oui");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ExportDirectory);
        Directory.CreateDirectory(OuiDirectory);
    }

    /// <summary>Persists a new data location. The application must be restarted afterwards.</summary>
    public void SaveDataDirectory(string newDirectory)
    {
        Directory.CreateDirectory(newDirectory);
        var json = JsonSerializer.Serialize(new { dataDirectory = newDirectory }, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, json);
    }
}
