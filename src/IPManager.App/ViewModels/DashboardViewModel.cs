using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class ChartItem
{
    public required string Label { get; init; }
    public double Value { get; init; }
    public string? Detail { get; init; }
    public double BarWidth { get; init; }
    public string ValueText => Value.ToString("N0");
}

public sealed partial class DashboardViewModel : ViewModelBase, IActivatable
{
    private readonly DashboardService _dashboard;
    private readonly ISettingsService _settings;
    private readonly IUiNavigator _navigator;
    private readonly DispatcherTimer _timer;

    public DashboardViewModel(DashboardService dashboard, ISettingsService settings, IUiNavigator navigator, IUiService ui)
        : base(ui)
    {
        _dashboard = dashboard;
        _settings = settings;
        _navigator = navigator;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => await LoadAsync();
    }

    public ObservableCollection<ChartItem> Allocation { get; } = new();
    public ObservableCollection<ChartItem> DeviceStatus { get; } = new();
    public ObservableCollection<ChartItem> Manufacturers { get; } = new();
    public ObservableCollection<ChartItem> Categories { get; } = new();
    public ObservableCollection<ChartItem> SubnetUtilization { get; } = new();
    public ObservableCollection<RecentActivityItem> Activity { get; } = new();
    public ObservableCollection<Device> RecentDevices { get; } = new();

    [ObservableProperty] private int _subnetCount;
    [ObservableProperty] private int _deviceCount;
    [ObservableProperty] private int _onlineDevices;
    [ObservableProperty] private int _offlineDevices;
    [ObservableProperty] private int _unknownDevices;
    [ObservableProperty] private int _activeConflicts;
    [ObservableProperty] private int _availableAddresses;
    [ObservableProperty] private int _reservedAddresses;
    [ObservableProperty] private int _unacknowledgedAlerts;
    [ObservableProperty] private long _totalManagedAddresses;
    [ObservableProperty] private double _onlinePercent;
    [ObservableProperty] private string _lastUpdated = "Never";
    [ObservableProperty] private bool _hasData;

    public async Task ActivateAsync()
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(15, _settings.Current.UiRefreshSeconds * 3));
        _timer.Start();
        await LoadAsync();
    }

    public void Deactivate() => _timer.Stop();

    public override async Task LoadAsync() => await RunGuardedAsync(async () =>
    {
        var snapshot = await _dashboard.GetSnapshotAsync();
        SubnetCount = snapshot.SubnetCount;
        DeviceCount = snapshot.DeviceCount;
        OnlineDevices = snapshot.OnlineDevices;
        OfflineDevices = snapshot.OfflineDevices;
        UnknownDevices = snapshot.UnknownStatusDevices;
        ActiveConflicts = snapshot.ActiveConflicts;
        AvailableAddresses = snapshot.AvailableAddresses;
        ReservedAddresses = snapshot.ReservedAddresses;
        UnacknowledgedAlerts = snapshot.UnacknowledgedAlerts;
        TotalManagedAddresses = snapshot.TotalManagedAddresses;
        OnlinePercent = snapshot.OnlinePercent;
        LastUpdated = snapshot.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        HasData = snapshot.SubnetCount > 0 || snapshot.DeviceCount > 0;

        Replace(Allocation, snapshot.Allocation);
        Replace(DeviceStatus, snapshot.DeviceStatus);
        Replace(Manufacturers, snapshot.Manufacturers);
        Replace(Categories, snapshot.Categories);
        Replace(SubnetUtilization, snapshot.SubnetUtilization);

        Activity.Clear();
        foreach (var item in snapshot.Activity) Activity.Add(item);

        RecentDevices.Clear();
        foreach (var device in snapshot.RecentDevices) RecentDevices.Add(device);
    }, errorTitle: "Unable to load the dashboard");

    private static void Replace(ObservableCollection<ChartItem> target, IReadOnlyList<ChartSlice> slices)
    {
        target.Clear();
        var max = slices.Count == 0 ? 0 : slices.Max(s => s.Value);
        foreach (var slice in slices)
        {
            target.Add(new ChartItem
            {
                Label = slice.Label,
                Value = slice.Value,
                Detail = slice.Detail,
                BarWidth = max <= 0 ? 0 : Math.Max(slice.Value <= 0 ? 0 : 2, slice.Value / max * 190)
            });
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void GoDiscovery() => _navigator.Navigate("Discovery");

    [RelayCommand]
    private void GoConflicts() => _navigator.Navigate("Conflicts");

    [RelayCommand]
    private void GoDevices() => _navigator.Navigate("Devices");

    [RelayCommand]
    private void GoSubnets() => _navigator.Navigate("SubnetManager");

    [RelayCommand]
    private void GoAlerts() => _navigator.Navigate("Alerts");

    [RelayCommand]
    private void GoMonitoring() => _navigator.Navigate("Monitoring");
}
