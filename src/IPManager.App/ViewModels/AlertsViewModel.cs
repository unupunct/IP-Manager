using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class AlertSeverityOption
{
    public required string Name { get; init; }
    public AlertSeverity? Value { get; init; }
}

public sealed partial class AlertsViewModel : ViewModelBase, IActivatable
{
    private readonly IAlertService _alerts;
    private readonly ISettingsService _settings;
    private readonly IUiNavigator _navigator;

    public AlertsViewModel(IAlertService alerts, ISettingsService settings, IUiNavigator navigator, IUiService ui) : base(ui)
    {
        _alerts = alerts;
        _settings = settings;
        _navigator = navigator;
        SeverityOptions = new ObservableCollection<AlertSeverityOption>
        {
            new() { Name = "All severities" },
            new() { Name = "Critical", Value = AlertSeverity.Critical },
            new() { Name = "Warning", Value = AlertSeverity.Warning },
            new() { Name = "Info", Value = AlertSeverity.Info }
        };
        SelectedSeverity = SeverityOptions[0];
    }

    public ObservableCollection<Alert> Alerts { get; } = new();
    public ObservableCollection<Alert> VisibleAlerts { get; } = new();
    public ObservableCollection<AlertSeverityOption> SeverityOptions { get; }

    [ObservableProperty] private Alert? _selectedAlert;
    [ObservableProperty] private AlertSeverityOption? _selectedSeverity;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _includeAcknowledged;
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private int _acknowledgedCount;
    [ObservableProperty] private string _summary = "No alerts loaded.";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load alerts");

    private async Task LoadCoreAsync()
    {
        var keepId = SelectedAlert?.Id ?? 0;
        var alerts = await _alerts.GetAlertsAsync(IncludeAcknowledged);
        Alerts.Clear();
        foreach (var alert in alerts) Alerts.Add(alert);

        CriticalCount = alerts.Count(a => a.Severity == AlertSeverity.Critical);
        WarningCount = alerts.Count(a => a.Severity == AlertSeverity.Warning);
        InfoCount = alerts.Count(a => a.Severity == AlertSeverity.Info);
        UnreadCount = await _alerts.GetUnreadCountAsync();
        AcknowledgedCount = alerts.Count(a => a.IsAcknowledged);
        Summary = $"{alerts.Count} alert(s) · {UnreadCount} unread · {AcknowledgedCount} acknowledged · retention {_settings.Current.AlertRetentionDays} days";

        ApplyFilter();
        if (keepId > 0) SelectedAlert = VisibleAlerts.FirstOrDefault(a => a.Id == keepId);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedSeverityChanged(AlertSeverityOption? value) => ApplyFilter();
    partial void OnIncludeAcknowledgedChanged(bool value) => _ = LoadSafeAsync();

    private async Task LoadSafeAsync()
    {
        try { await LoadCoreAsync(); }
        catch (Exception ex) { Ui.Error(ex.Message, "Unable to load alerts"); }
    }

    private void ApplyFilter()
    {
        var query = Alerts.AsEnumerable();
        if (SelectedSeverity?.Value is { } severity) query = query.Where(a => a.Severity == severity);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(a => a.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     a.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     (a.IpAddress?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                     a.Type.ToString().Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        VisibleAlerts.Clear();
        foreach (var alert in query.OrderByDescending(a => a.CreatedAt)) VisibleAlerts.Add(alert);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task AcknowledgeAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedAlert is null) return;
        await _alerts.AcknowledgeAsync(SelectedAlert.Id, !SelectedAlert.IsAcknowledged);
        await LoadCoreAsync();
    }, errorTitle: "Unable to acknowledge the alert");

    [RelayCommand]
    private async Task AcknowledgeAllAsync() => await RunGuardedAsync(async () =>
    {
        if (!Ui.Confirm($"Acknowledge all {VisibleAlerts.Count} visible alert(s)?", "Acknowledge alerts")) return;
        var count = await _alerts.AcknowledgeAllAsync();
        await LoadCoreAsync();
        Ui.SetStatus($"Acknowledged {count} alert(s).");
    }, errorTitle: "Unable to acknowledge alerts");

    [RelayCommand]
    private async Task MarkAllReadAsync() => await RunGuardedAsync(async () =>
    {
        await _alerts.MarkAllReadAsync();
        await LoadCoreAsync();
    }, "All alerts marked read.", "Unable to mark alerts read");

    [RelayCommand]
    private async Task PurgeAsync() => await RunGuardedAsync(async () =>
    {
        var days = _settings.Current.AlertRetentionDays;
        if (!Ui.Confirm($"Delete alerts older than {days} days?", "Purge old alerts")) return;
        var count = await _alerts.PurgeOldAsync(days);
        await LoadCoreAsync();
        Ui.SetStatus($"Removed {count} old alert(s).");
    }, errorTitle: "Unable to purge alerts");

    [RelayCommand]
    private void OpenSource()
    {
        if (SelectedAlert is null) return;
        if (SelectedAlert.EntityType is "Device" && SelectedAlert.EntityId is > 0)
            _navigator.Navigate("Devices", SelectedAlert.EntityId.Value);
        else if (!string.IsNullOrWhiteSpace(SelectedAlert.IpAddress))
            _navigator.Navigate("Devices", SelectedAlert.IpAddress);
        else if (SelectedAlert.Type == AlertType.IpConflict)
            _navigator.Navigate("Conflicts");
        else
            Ui.Info("This alert has no linked entity or address.");
    }

    [RelayCommand]
    private void ClearFilter()
    {
        SearchText = "";
        SelectedSeverity = SeverityOptions[0];
    }
}
