using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class UptimeRangeOption
{
    public required string Name { get; init; }
    public int Days { get; init; }
}

public sealed record UptimeBar(string Label, double Height, string Tooltip, double? Percent)
{
    public string PercentText => Percent is null ? "no data" : $"{Percent:0.0}%";
}

public sealed partial class MonitoringViewModel : ViewModelBase, IActivatable, INavigationAware
{
    private readonly MonitoringService _monitoring;
    private readonly InventoryService _inventory;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _timer;
    private int _selectAfterLoad;

    public MonitoringViewModel(MonitoringService monitoring, InventoryService inventory, ISettingsService settings,
        IUiService ui) : base(ui)
    {
        _monitoring = monitoring;
        _inventory = inventory;
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await LoadDataCoreAsync();

        UptimeRanges = new ObservableCollection<UptimeRangeOption>
        {
            new() { Name = "Last 24 hours", Days = 1 },
            new() { Name = "Last 7 days", Days = 7 },
            new() { Name = "Last 30 days", Days = 30 }
        };
        SelectedUptimeRange = UptimeRanges[1];
        Methods = Enum.GetValues<MonitoringMethod>().ToList();
        SelectedMethod = MonitoringMethod.Icmp;
    }

    public ObservableCollection<Device> Devices { get; } = new();
    public ObservableCollection<MonitoringTarget> Targets { get; } = new();
    public ObservableCollection<MonitoringObservation> History { get; } = new();
    public ObservableCollection<UptimeBar> Buckets { get; } = new();
    public ObservableCollection<UptimeRangeOption> UptimeRanges { get; }
    public IReadOnlyList<MonitoringMethod> Methods { get; }

    [ObservableProperty] private Device? _selectedDevice;
    [ObservableProperty] private MonitoringTarget? _selectedTarget;
    [ObservableProperty] private UptimeRangeOption? _selectedUptimeRange;
    [ObservableProperty] private string _deviceFilter = "";
    [ObservableProperty] private string _summary = "Select a device to inspect monitoring data.";
    [ObservableProperty] private string _uptimeText = "No monitoring data";
    [ObservableProperty] private int _totalChecks;
    [ObservableProperty] private int _successfulChecks;
    [ObservableProperty] private int _failedChecks;
    [ObservableProperty] private string _lastCheckText = "Never";
    [ObservableProperty] private string _lastSuccessText = "Never";
    [ObservableProperty] private double? _averageResponse;
    [ObservableProperty] private bool _isEditorOpen;

    [ObservableProperty] private MonitoringMethod _selectedMethod = MonitoringMethod.Icmp;
    [ObservableProperty] private string _targetPort = "";
    [ObservableProperty] private string _targetUrl = "";
    [ObservableProperty] private string _targetInterval = "60";
    [ObservableProperty] private string _targetTimeout = "2000";
    [ObservableProperty] private bool _targetEnabled = true;
    [ObservableProperty] private string _editorTitle = "Monitoring target";

    public async Task ActivateAsync()
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(15, _settings.Current.UiRefreshSeconds * 3));
        _timer.Start();
        await LoadAsync();
    }

    public void Deactivate() => _timer.Stop();

    public async Task OnNavigatedToAsync(object? argument)
    {
        if (argument is int id) _selectAfterLoad = id;
        await LoadAsync();
    }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadDataCoreAsync, errorTitle: "Unable to load monitoring data");

    private async Task LoadDataCoreAsync()
    {
        var keepId = _selectAfterLoad > 0 ? _selectAfterLoad : SelectedDevice?.Id ?? 0;
        _selectAfterLoad = 0;

        var devices = await _inventory.GetDevicesAsync();
        Devices.Clear();
        foreach (var device in devices
                     .Where(d => !string.IsNullOrWhiteSpace(d.IpAddress))
                     .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            Devices.Add(device);

        SelectedDevice = Devices.FirstOrDefault(d => d.Id == keepId) ?? SelectedDevice;
        if (SelectedDevice is null) SelectedDevice = Devices.FirstOrDefault();
        else await LoadDeviceDataCoreAsync();
    }

    partial void OnDeviceFilterChanged(string value)
    {
        // filtering is applied by the view over the Devices collection; the selected device stays stable
        OnPropertyChanged(nameof(VisibleDevices));
    }

    public IEnumerable<Device> VisibleDevices
    {
        get
        {
            var term = DeviceFilter.Trim();
            if (term.Length == 0) return Devices;
            return Devices.Where(d =>
                d.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (d.IpAddress?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.MacAddress?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }
    }

    partial void OnSelectedDeviceChanged(Device? value)
    {
        if (value is null)
        {
            Targets.Clear();
            History.Clear();
            Buckets.Clear();
            Summary = "No device selected.";
            return;
        }
        _ = LoadDeviceDataSafeAsync(value.Id);
    }

    private async Task LoadDeviceDataSafeAsync(int deviceId)
    {
        try
        {
            await LoadDeviceDataCoreAsync();
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message, "Unable to load monitoring history");
        }
    }

    private async Task LoadDeviceDataCoreAsync()
    {
        var device = SelectedDevice;
        if (device is null) return;

        var targets = await _monitoring.GetTargetsAsync(device.Id);
        Targets.Clear();
        foreach (var target in targets) Targets.Add(target);

        var days = SelectedUptimeRange?.Days ?? 7;
        var uptime = await _monitoring.GetUptimeAsync(device.Id, days);
        UptimeText = uptime.UptimeText;
        TotalChecks = uptime.TotalChecks;
        SuccessfulChecks = uptime.Successes;
        FailedChecks = uptime.Failures;
        LastCheckText = uptime.LastCheck?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Never";
        LastSuccessText = uptime.LastSuccess?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Never";

        Buckets.Clear();
        foreach (var bucket in uptime.Buckets)
        {
            var height = bucket.Percent is null ? 2 : Math.Max(2, bucket.Percent.Value / 100 * 90);
            Buckets.Add(new UptimeBar(bucket.Start.ToLocalTime().ToString("MM-dd HH:mm"),
                height,
                $"{bucket.Start.ToLocalTime():yyyy-MM-dd HH:mm} · {bucket.Success}/{bucket.Total} successful",
                bucket.Percent));
        }

        var history = await _monitoring.GetHistoryAsync(device.Id, days * 24);
        History.Clear();
        foreach (var observation in history) History.Add(observation);
        AverageResponse = history.Where(h => h.Success && h.ResponseMs is not null)
            .Select(h => h.ResponseMs!.Value)
            .DefaultIfEmpty(0)
            .Average();

        Summary = $"{device.Name} · {device.IpAddress} · {device.Status} · {targets.Count} target(s)";
    }

    partial void OnSelectedUptimeRangeChanged(UptimeRangeOption? value)
    {
        if (SelectedDevice is not null) _ = LoadDeviceDataSafeAsync(SelectedDevice.Id);
    }

    partial void OnSelectedTargetChanged(MonitoringTarget? value)
    {
        IsEditorOpen = value is not null;
        if (value is null) return;
        EditorTitle = $"{value.Method}" + (value.Port is null ? "" : $" · port {value.Port}");
        SelectedMethod = value.Method;
        TargetPort = value.Port?.ToString() ?? "";
        TargetUrl = value.Url ?? "";
        TargetInterval = value.IntervalSeconds.ToString();
        TargetTimeout = value.TimeoutMs.ToString();
        TargetEnabled = value.Enabled;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task RunCycleAsync() => await RunGuardedAsync(async () =>
    {
        var result = await _monitoring.RunCycleAsync();
        await LoadDataCoreAsync();
        Summary = $"Cycle completed {result.CompletedAt.ToLocalTime():HH:mm:ss} · {result.Checked} checked, " +
                  $"{result.Online} online, {result.Offline} offline, {result.Gateways} gateway(s).";
    }, "Monitoring cycle completed.", "Monitoring cycle failed");

    [RelayCommand]
    private void NewTarget()
    {
        if (SelectedDevice is null)
        {
            Ui.Info("Select a device first.");
            return;
        }
        SelectedTarget = new MonitoringTarget
        {
            DeviceId = SelectedDevice.Id,
            Method = MonitoringMethod.Icmp,
            IntervalSeconds = Math.Max(30, _settings.Current.MonitoringIntervalSeconds),
            TimeoutMs = Math.Max(200, _settings.Current.MonitoringTimeoutMs),
            Enabled = true
        };
        IsEditorOpen = true;
        EditorTitle = "New monitoring target";
        SelectedMethod = MonitoringMethod.Icmp;
        TargetPort = "";
        TargetUrl = "";
        TargetInterval = Math.Max(30, _settings.Current.MonitoringIntervalSeconds).ToString();
        TargetTimeout = Math.Max(200, _settings.Current.MonitoringTimeoutMs).ToString();
        TargetEnabled = true;
    }

    [RelayCommand]
    private async Task SaveTargetAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedTarget is null || SelectedDevice is null) return;
        SelectedTarget.DeviceId = SelectedDevice.Id;
        SelectedTarget.Method = SelectedMethod;
        SelectedTarget.Port = int.TryParse(TargetPort, out var port) ? port : null;
        SelectedTarget.Url = string.IsNullOrWhiteSpace(TargetUrl) ? null : TargetUrl.Trim();
        SelectedTarget.IntervalSeconds = int.TryParse(TargetInterval, out var interval) ? Math.Clamp(interval, 15, 86400) : 60;
        SelectedTarget.TimeoutMs = int.TryParse(TargetTimeout, out var timeout) ? Math.Clamp(timeout, 100, 60000) : 2000;
        SelectedTarget.Enabled = TargetEnabled;
        if (SelectedTarget.Method == MonitoringMethod.Tcp && SelectedTarget.Port is null)
            throw new InvalidOperationException("A TCP target requires a port.");
        if (SelectedTarget.Method == MonitoringMethod.Http && string.IsNullOrWhiteSpace(SelectedTarget.Url))
            throw new InvalidOperationException("An HTTP target requires a URL.");
        var saved = await _monitoring.SaveTargetAsync(SelectedTarget);
        await LoadDeviceDataCoreAsync();
        SelectedTarget = Targets.FirstOrDefault(t => t.Id == saved.Id);
        Ui.SetStatus("Monitoring target saved.");
    }, errorTitle: "Unable to save the monitoring target");

    [RelayCommand]
    private async Task DeleteTargetAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedTarget is null || SelectedTarget.Id == 0) return;
        if (!Ui.Confirm($"Delete this {SelectedTarget.Method} monitoring target?", "Delete target")) return;
        await _monitoring.DeleteTargetAsync(SelectedTarget.Id);
        SelectedTarget = null;
        IsEditorOpen = false;
        await LoadDeviceDataCoreAsync();
    }, "Monitoring target deleted.", "Unable to delete the monitoring target");

    [RelayCommand]
    private void CancelEditor()
    {
        SelectedTarget = null;
        IsEditorOpen = false;
    }

    [RelayCommand]
    private void OpenDevice()
    {
        if (SelectedDevice is not null) Ui.SetStatus($"{SelectedDevice.Name} · {SelectedDevice.IpAddress}");
    }
}
