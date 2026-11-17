using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Infrastructure;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IActivatable
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IBackupService _backup;
    private readonly MaintenanceService _maintenance;
    private readonly OuiService _oui;
    private readonly StartupService _startup;
    private readonly ISecretProtector _protector;
    private readonly AppPaths _paths;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, IThemeService theme, IBackupService backup,
        MaintenanceService maintenance, OuiService oui, StartupService startup, ISecretProtector protector,
        AppPaths paths, IUiService ui) : base(ui)
    {
        _settings = settings;
        _theme = theme;
        _backup = backup;
        _maintenance = maintenance;
        _oui = oui;
        _startup = startup;
        _protector = protector;
        _paths = paths;
        VersionText = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public ObservableCollection<BackupInfo> Backups { get; } = new();
    public IReadOnlyList<string> LogLevels { get; } = new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    [ObservableProperty] private int _selectedTabIndex;

    // Appearance
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private bool _use24HourClock;
    [ObservableProperty] private int _uiRefreshSeconds;

    // Discovery
    [ObservableProperty] private string _defaultScanRange = "";
    [ObservableProperty] private string _preferredInterface = "";
    [ObservableProperty] private int _scanTimeoutMs;
    [ObservableProperty] private int _scanConcurrency;
    [ObservableProperty] private int _scanRetryCount;
    [ObservableProperty] private int _scanDelayMs;
    [ObservableProperty] private bool _enableArp;
    [ObservableProperty] private bool _enableIcmp;
    [ObservableProperty] private bool _enableTcp;
    [ObservableProperty] private bool _enableDns;
    [ObservableProperty] private bool _enableReverseDns;
    [ObservableProperty] private bool _enableMdns;
    [ObservableProperty] private bool _enableNetBios;
    [ObservableProperty] private string _tcpProbePorts = "";
    [ObservableProperty] private bool _requireConfirmationForExternalRanges;
    [ObservableProperty] private int _maxAddressesPerScan;
    [ObservableProperty] private bool _autoScanOnStartup;

    // Monitoring
    [ObservableProperty] private int _monitoringIntervalSeconds;
    [ObservableProperty] private int _offlineThresholdMinutes;
    [ObservableProperty] private int _monitoringTimeoutMs;
    [ObservableProperty] private bool _monitorGateway;
    [ObservableProperty] private bool _persistMonitoringResults;
    [ObservableProperty] private int _monitoringRetentionDays;

    // Conflicts and alerts
    [ObservableProperty] private int _conflictFreshMinutes;
    [ObservableProperty] private int _conflictStaleHours;
    [ObservableProperty] private int _subnetUtilizationThreshold;
    [ObservableProperty] private int _dhcpExhaustionThreshold;
    [ObservableProperty] private int _alertCooldownMinutes;
    [ObservableProperty] private bool _alertOnNewDevices;
    [ObservableProperty] private bool _alertOnUnknownDevices;
    [ObservableProperty] private bool _alertOnDeviceOffline;
    [ObservableProperty] private bool _alertOnConflicts;
    [ObservableProperty] private bool _recoveryNotifications;

    // Notifications
    [ObservableProperty] private bool _telegramEnabled;
    [ObservableProperty] private string _telegramChatId = "";
    [ObservableProperty] private string _telegramToken = "";
    [ObservableProperty] private bool _telegramTokenConfigured;
    [ObservableProperty] private bool _emailEnabled;
    [ObservableProperty] private string _emailSmtpHost = "";
    [ObservableProperty] private int _emailSmtpPort;
    [ObservableProperty] private string _emailFrom = "";
    [ObservableProperty] private string _emailTo = "";
    [ObservableProperty] private string _emailUsername = "";
    [ObservableProperty] private string _emailPassword = "";
    [ObservableProperty] private bool _emailPasswordConfigured;
    [ObservableProperty] private bool _emailUseTls;

    // Data
    [ObservableProperty] private bool _backupOnStartup;
    [ObservableProperty] private int _backupRetentionCount;
    [ObservableProperty] private int _alertRetentionDays;
    [ObservableProperty] private int _auditRetentionDays;
    [ObservableProperty] private bool _confirmBeforeRemoteChanges;
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private string _ouiUpdateUrl = "";
    [ObservableProperty] private bool _startWithWindows;

    [ObservableProperty] private BackupInfo? _selectedBackup;
    [ObservableProperty] private string _backupSummary = "No backups listed.";
    [ObservableProperty] private string _maintenanceSummary = "Retention cleanup is manual unless you run it here.";
    [ObservableProperty] private string _ouiSummary = "";
    [ObservableProperty] private string _dataDirectory = "";
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _logDirectory = "";
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _storageSummary = "";
    [ObservableProperty] private bool _isImportingOui;

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load settings");

    private async Task LoadCoreAsync()
    {
        _loading = true;
        try
        {
            ApplyFromSettings(_settings.Current);
            RefreshBackups();
            OuiSummary = $"{_oui.Count:N0} OUI prefixes Â· " +
                         (_oui.LastLoaded is null ? "embedded seed only" : $"file loaded {_oui.LastLoaded.Value.ToLocalTime():yyyy-MM-dd HH:mm}");
            DataDirectory = _paths.DataDirectory;
            DatabasePath = _paths.DatabasePath;
            LogDirectory = _paths.LogDirectory;
            StorageSummary = $"{(_paths.IsPortable ? "Portable" : "Installed")} data location Â· backups, logs, exports and OUI data live under the data directory.";
        }
        finally
        {
            _loading = false;
        }
        await Task.CompletedTask;
    }

    private void ApplyFromSettings(AppSettings s)
    {
        IsDarkTheme = !string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        Use24HourClock = s.Use24HourClock;
        UiRefreshSeconds = s.UiRefreshSeconds;

        DefaultScanRange = s.DefaultScanRange;
        PreferredInterface = s.PreferredInterface;
        ScanTimeoutMs = s.ScanTimeoutMs;
        ScanConcurrency = s.ScanConcurrency;
        ScanRetryCount = s.ScanRetryCount;
        ScanDelayMs = s.ScanDelayMs;
        EnableArp = s.EnableArp;
        EnableIcmp = s.EnableIcmp;
        EnableTcp = s.EnableTcp;
        EnableDns = s.EnableDns;
        EnableReverseDns = s.EnableReverseDns;
        EnableMdns = s.EnableMdns;
        EnableNetBios = s.EnableNetBios;
        TcpProbePorts = s.TcpProbePorts;
        RequireConfirmationForExternalRanges = s.RequireConfirmationForExternalRanges;
        MaxAddressesPerScan = s.MaxAddressesPerScan;
        AutoScanOnStartup = s.AutoScanOnStartup;

        MonitoringIntervalSeconds = s.MonitoringIntervalSeconds;
        OfflineThresholdMinutes = s.OfflineThresholdMinutes;
        MonitoringTimeoutMs = s.MonitoringTimeoutMs;
        MonitorGateway = s.MonitorGateway;
        PersistMonitoringResults = s.PersistMonitoringResults;
        MonitoringRetentionDays = s.MonitoringRetentionDays;

        ConflictFreshMinutes = s.ConflictFreshMinutes;
        ConflictStaleHours = s.ConflictStaleHours;
        SubnetUtilizationThreshold = s.SubnetUtilizationThreshold;
        DhcpExhaustionThreshold = s.DhcpExhaustionThreshold;
        AlertCooldownMinutes = s.AlertCooldownMinutes;
        AlertOnNewDevices = s.AlertOnNewDevices;
        AlertOnUnknownDevices = s.AlertOnUnknownDevices;
        AlertOnDeviceOffline = s.AlertOnDeviceOffline;
        AlertOnConflicts = s.AlertOnConflicts;
        RecoveryNotifications = s.RecoveryNotifications;

        TelegramEnabled = s.TelegramEnabled;
        TelegramChatId = s.TelegramChatId;
        TelegramToken = "";
        TelegramTokenConfigured = !string.IsNullOrWhiteSpace(s.TelegramBotTokenProtected);
        EmailEnabled = s.EmailEnabled;
        EmailSmtpHost = s.EmailSmtpHost;
        EmailSmtpPort = s.EmailSmtpPort;
        EmailFrom = s.EmailFrom;
        EmailTo = s.EmailTo;
        EmailUsername = s.EmailUsername;
        EmailPassword = "";
        EmailPasswordConfigured = !string.IsNullOrWhiteSpace(s.EmailPasswordProtected);
        EmailUseTls = s.EmailUseTls;

        BackupOnStartup = s.BackupOnStartup;
        BackupRetentionCount = s.BackupRetentionCount;
        AlertRetentionDays = s.AlertRetentionDays;
        AuditRetentionDays = s.AuditRetentionDays;
        ConfirmBeforeRemoteChanges = s.ConfirmBeforeRemoteChanges;
        LogLevel = string.IsNullOrWhiteSpace(s.LogLevel) ? "Information" : s.LogLevel;
        OuiUpdateUrl = s.OuiUpdateUrl;
        StartWithWindows = _startup.IsEnabled();
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var backup in _backup.ListBackups()) Backups.Add(backup);
        SelectedBackup = Backups.FirstOrDefault();
        BackupSummary = Backups.Count == 0
            ? "No backups yet. Create one before testing restores."
            : $"{Backups.Count} backup(s) Â· newest {Backups.Max(b => b.CreatedUtc).ToLocalTime():yyyy-MM-dd HH:mm} Â· " +
              $"total {Backups.Sum(b => b.SizeBytes) / (1024.0 * 1024.0):0.0} MB";
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunGuardedAsync(async () =>
    {
        var s = _settings.Current;
        s.Theme = IsDarkTheme ? "Dark" : "Light";
        s.Use24HourClock = Use24HourClock;
        s.UiRefreshSeconds = Math.Clamp(UiRefreshSeconds, 3, 300);

        s.DefaultScanRange = DefaultScanRange.Trim();
        s.PreferredInterface = PreferredInterface.Trim();
        s.ScanTimeoutMs = Math.Clamp(ScanTimeoutMs, 100, 30000);
        s.ScanConcurrency = Math.Clamp(ScanConcurrency, 1, 256);
        s.ScanRetryCount = Math.Clamp(ScanRetryCount, 0, 10);
        s.ScanDelayMs = Math.Clamp(ScanDelayMs, 0, 10000);
        s.EnableArp = EnableArp;
        s.EnableIcmp = EnableIcmp;
        s.EnableTcp = EnableTcp;
        s.EnableDns = EnableDns;
        s.EnableReverseDns = EnableReverseDns;
        s.EnableMdns = EnableMdns;
        s.EnableNetBios = EnableNetBios;
        s.TcpProbePorts = TcpProbePorts.Trim();
        s.RequireConfirmationForExternalRanges = RequireConfirmationForExternalRanges;
        s.MaxAddressesPerScan = Math.Clamp(MaxAddressesPerScan, 1, 65536);
        s.AutoScanOnStartup = AutoScanOnStartup;

        s.MonitoringIntervalSeconds = Math.Clamp(MonitoringIntervalSeconds, 15, 86400);
        s.OfflineThresholdMinutes = Math.Clamp(OfflineThresholdMinutes, 1, 1440);
        s.MonitoringTimeoutMs = Math.Clamp(MonitoringTimeoutMs, 100, 60000);
        s.MonitorGateway = MonitorGateway;
        s.PersistMonitoringResults = PersistMonitoringResults;
        s.MonitoringRetentionDays = Math.Clamp(MonitoringRetentionDays, 1, 3650);

        s.ConflictFreshMinutes = Math.Clamp(ConflictFreshMinutes, 1, 1440);
        s.ConflictStaleHours = Math.Clamp(ConflictStaleHours, 1, 720);
        s.SubnetUtilizationThreshold = Math.Clamp(SubnetUtilizationThreshold, 1, 100);
        s.DhcpExhaustionThreshold = Math.Clamp(DhcpExhaustionThreshold, 1, 100);
        s.AlertCooldownMinutes = Math.Clamp(AlertCooldownMinutes, 0, 10080);
        s.AlertOnNewDevices = AlertOnNewDevices;
        s.AlertOnUnknownDevices = AlertOnUnknownDevices;
        s.AlertOnDeviceOffline = AlertOnDeviceOffline;
        s.AlertOnConflicts = AlertOnConflicts;
        s.RecoveryNotifications = RecoveryNotifications;

        s.TelegramEnabled = TelegramEnabled;
        s.TelegramChatId = TelegramChatId.Trim();
        s.EmailEnabled = EmailEnabled;
        s.EmailSmtpHost = EmailSmtpHost.Trim();
        s.EmailSmtpPort = Math.Clamp(EmailSmtpPort, 1, 65535);
        s.EmailFrom = EmailFrom.Trim();
        s.EmailTo = EmailTo.Trim();
        s.EmailUsername = EmailUsername.Trim();
        s.EmailUseTls = EmailUseTls;

        if (!string.IsNullOrWhiteSpace(TelegramToken))
        {
            s.TelegramBotTokenProtected = _protector.Protect(TelegramToken.Trim());
            TelegramTokenConfigured = true;
            TelegramToken = "";
        }
        if (!string.IsNullOrWhiteSpace(EmailPassword))
        {
            s.EmailPasswordProtected = _protector.Protect(EmailPassword);
            EmailPasswordConfigured = true;
            EmailPassword = "";
        }

        s.BackupOnStartup = BackupOnStartup;
        s.BackupRetentionCount = Math.Clamp(BackupRetentionCount, 1, 100);
        s.AlertRetentionDays = Math.Clamp(AlertRetentionDays, 1, 3650);
        s.AuditRetentionDays = Math.Clamp(AuditRetentionDays, 1, 3650);
        s.ConfirmBeforeRemoteChanges = ConfirmBeforeRemoteChanges;
        s.LogLevel = LogLevel;
        s.OuiUpdateUrl = OuiUpdateUrl.Trim();

        await _settings.SaveAsync();
        _theme.Apply(s.Theme);

        var startup = _startup.SetEnabled(StartWithWindows);
        if (!startup.Success)
            Ui.Error($"The Windows startup entry could not be updated.\n\n{startup.Message}", "Startup setting");
        else
            Ui.SetStatus("Settings saved." + (StartWithWindows ? " IP Manager will start with Windows." : ""));
    }, "Settings saved.", "Unable to save settings");

    [RelayCommand]
    private async Task CreateBackupAsync() => await RunGuardedAsync(async () =>
    {
        var info = await _backup.CreateBackupAsync("manual");
        RefreshBackups();
        SelectedBackup = Backups.FirstOrDefault(b => b.Path == info.Path);
        await _backup.PruneAsync(Math.Clamp(BackupRetentionCount, 1, 100));
        RefreshBackups();
        Ui.SetStatus($"Backup created: {info.Name} ({info.SizeText})");
    }, errorTitle: "Unable to create a backup");

    [RelayCommand]
    private async Task RestoreBackupAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedBackup is null)
        {
            Ui.Info("Select a backup first.");
            return;
        }
        if (!Ui.Confirm($"Restore {SelectedBackup.Name} ({SelectedBackup.SizeText})?\n\n" +
                        "The current database is replaced. A safety copy is made first. " +
                        "Restart IP Manager after the restore to reload every page.", "Restore backup")) return;
        await _backup.RestoreBackupAsync(SelectedBackup.Path);
        RefreshBackups();
        Ui.Info("The backup was restored. Restart IP Manager so every page uses the restored database.", "Restore complete");
    }, "Backup restored.", "Unable to restore the backup");

    [RelayCommand]
    private async Task PruneBackupsAsync() => await RunGuardedAsync(async () =>
    {
        var removed = await _backup.PruneAsync(Math.Clamp(BackupRetentionCount, 1, 100));
        RefreshBackups();
        Ui.SetStatus($"Removed {removed} old backup(s).");
    }, errorTitle: "Unable to prune backups");

    [RelayCommand]
    private async Task ExportPackageAsync() => await RunGuardedAsync(async () =>
    {
        var target = Ui.SaveFile("Zip archive (*.zip)|*.zip", $"IPManager-Data-{DateTime.Now:yyyyMMdd}.zip", _paths.ExportDirectory);
        if (string.IsNullOrWhiteSpace(target)) return;
        var path = await _backup.ExportDatabasePackageAsync(target);
        Ui.SetStatus($"Data package exported to {path}");
        Ui.OpenInExplorer(path);
    }, errorTitle: "Unable to export the data package");

    [RelayCommand]
    private async Task RunMaintenanceAsync() => await RunGuardedAsync(async () =>
    {
        if (!Ui.Confirm("Run retention cleanup now? Old monitoring results, DNS logs, audit events and resolved conflicts " +
                        "are deleted according to the retention settings.", "Run maintenance")) return;
        MaintenanceSummary = await _maintenance.RunAsync();
        RefreshBackups();
    }, "Maintenance completed.", "Maintenance failed");

    [RelayCommand]
    private async Task UpdateOuiAsync() => await RunGuardedAsync(async () =>
    {
        IsImportingOui = true;
        try
        {
            var progress = new Progress<string>(message => Ui.SetStatus(message));
            var added = await _oui.UpdateFromIeeeAsync(progress);
            OuiSummary = $"{_oui.Count:N0} OUI prefixes Â· updated now (+{added:N0})";
        }
        finally
        {
            IsImportingOui = false;
        }
    }, "OUI database updated.", "Unable to update the OUI database");

    [RelayCommand]
    private async Task ImportOuiAsync() => await RunGuardedAsync(async () =>
    {
        var path = Ui.OpenFile("OUI registry CSV (*.csv)|*.csv|All files (*.*)|*.*", _paths.OuiDirectory);
        if (string.IsNullOrWhiteSpace(path)) return;
        IsImportingOui = true;
        try
        {
            var added = await _oui.ImportFileAsync(path);
            OuiSummary = $"{_oui.Count:N0} OUI prefixes Â· imported {Path.GetFileName(path)} (+{added:N0})";
        }
        finally
        {
            IsImportingOui = false;
        }
    }, "OUI database imported.", "Unable to import the OUI database");

    [RelayCommand]
    private void OpenDataFolder() => Ui.OpenInExplorer(_paths.DataDirectory);

    [RelayCommand]
    private void OpenBackupFolder() => Ui.OpenInExplorer(_paths.BackupDirectory);

    [RelayCommand]
    private void OpenLogFolder() => Ui.OpenInExplorer(_paths.LogDirectory);

    [RelayCommand]
    private void OpenExportFolder() => Ui.OpenInExplorer(_paths.ExportDirectory);

    [RelayCommand]
    private void ChangeDataDirectory()
    {
        var folder = Ui.PickFolder(_paths.DataDirectory);
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (!Ui.Confirm($"Use '{folder}' as the data directory?\n\nExisting data is not moved automatically. " +
                        "Restart IP Manager to use the new location.", "Change data directory")) return;
        try
        {
            _paths.SaveDataDirectory(folder);
            Ui.Info("The data directory is saved. Restart IP Manager; the current database stays in the old folder.",
                "Restart required");
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message, "Unable to change the data directory");
        }
    }

    [RelayCommand]
    private async Task ResetThemeAsync() => await RunGuardedAsync(async () =>
    {
        IsDarkTheme = true;
        _settings.Current.Theme = "Dark";
        _theme.Apply("Dark");
        await _settings.SaveAsync();
    }, "Dark theme restored.", "Unable to change the theme");

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();
}
