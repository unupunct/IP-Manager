using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class DeviceCategoryOption
{
    public required string Name { get; init; }
    public DeviceCategory? Value { get; init; }
}

public sealed class DeviceStatusOption
{
    public required string Name { get; init; }
    public DeviceStatus? Value { get; init; }
}

public sealed partial class DevicesViewModel : ViewModelBase, IActivatable, INavigationAware
{
    private readonly InventoryService _inventory;
    private readonly IUiNavigator _navigator;
    private int _selectAfterLoad;

    public DevicesViewModel(InventoryService inventory, IUiNavigator navigator, IUiService ui) : base(ui)
    {
        _inventory = inventory;
        _navigator = navigator;
        CategoryOptions = new ObservableCollection<DeviceCategoryOption>
        {
            new() { Name = "All categories" }
        };
        foreach (var value in Enum.GetValues<DeviceCategory>())
            CategoryOptions.Add(new DeviceCategoryOption { Name = DeviceClassifier.DisplayName(value), Value = value });
        SelectedCategory = CategoryOptions[0];

        StatusOptions = new ObservableCollection<DeviceStatusOption>
        {
            new() { Name = "All statuses" },
            new() { Name = "Online", Value = DeviceStatus.Online },
            new() { Name = "Offline", Value = DeviceStatus.Offline },
            new() { Name = "Unknown", Value = DeviceStatus.Unknown }
        };
        SelectedStatus = StatusOptions[0];

        CategoryValues = Enum.GetValues<DeviceCategory>().ToList();
        StatusValues = new[] { DeviceStatus.Unknown, DeviceStatus.Online, DeviceStatus.Offline };
    }

    public ObservableCollection<Device> Devices { get; } = new();
    public ObservableCollection<Device> VisibleDevices { get; } = new();
    public ObservableCollection<Subnet> Subnets { get; } = new();
    public ObservableCollection<DeviceHistory> History { get; } = new();
    public ObservableCollection<DeviceCategoryOption> CategoryOptions { get; }
    public ObservableCollection<DeviceStatusOption> StatusOptions { get; }
    public IReadOnlyList<DeviceCategory> CategoryValues { get; }
    public IReadOnlyList<DeviceStatus> StatusValues { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private DeviceCategoryOption? _selectedCategory;
    [ObservableProperty] private DeviceStatusOption? _selectedStatus;
    [ObservableProperty] private Device? _selectedDevice;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _offlineCount;
    [ObservableProperty] private int _unknownCount;
    [ObservableProperty] private int _trustedCount;
    [ObservableProperty] private string _editorTitle = "New device";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public async Task OnNavigatedToAsync(object? argument)
    {
        if (argument is string term) SearchText = term;
        else if (argument is int id) _selectAfterLoad = id;
        await LoadAsync();
    }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load devices");

    private async Task LoadCoreAsync()
    {
        var keepId = _selectAfterLoad > 0 ? _selectAfterLoad : SelectedDevice?.Id ?? 0;
        _selectAfterLoad = 0;

        var subnets = await _inventory.GetSubnetsAsync();
        Subnets.Clear();
        foreach (var subnet in subnets) Subnets.Add(subnet);

        var devices = await _inventory.GetDevicesAsync();
        Devices.Clear();
        foreach (var device in devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)) Devices.Add(device);

        OnlineCount = devices.Count(d => d.Status == DeviceStatus.Online);
        OfflineCount = devices.Count(d => d.Status == DeviceStatus.Offline);
        UnknownCount = devices.Count(d => d.Status == DeviceStatus.Unknown);
        TrustedCount = devices.Count(d => d.IsTrusted);

        ApplyFilter();

        if (keepId > 0)
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == keepId);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(DeviceCategoryOption? value) => ApplyFilter();
    partial void OnSelectedStatusChanged(DeviceStatusOption? value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = Devices.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            query = query.Where(d =>
                (d.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.IpAddress?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.MacAddress?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Hostname?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Manufacturer?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Tags?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Location?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        if (SelectedCategory?.Value is { } category) query = query.Where(d => d.Category == category);
        if (SelectedStatus?.Value is { } status) query = query.Where(d => d.Status == status);

        VisibleDevices.Clear();
        foreach (var device in query) VisibleDevices.Add(device);
    }

    partial void OnSelectedDeviceChanged(Device? value)
    {
        IsEditorOpen = value is not null;
        IsNew = value is { Id: 0 };
        EditorTitle = IsNew ? "New device" : value is null ? "Device details" : value.Name;
        if (value is { Id: > 0 }) _ = LoadHistoryAsync(value.Id);
        else History.Clear();
    }

    private async Task LoadHistoryAsync(int deviceId)
    {
        try
        {
            var rows = await _inventory.GetDeviceHistoryAsync(deviceId);
            History.Clear();
            foreach (var row in rows) History.Add(row);
        }
        catch
        {
            History.Clear();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void AddDevice()
    {
        SelectedDevice = new Device
        {
            Category = DeviceCategory.Unknown,
            Status = DeviceStatus.Unknown,
            IsTrusted = false
        };
        IsEditorOpen = true;
        IsNew = true;
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedDevice is null) return;
        var saved = await _inventory.SaveDeviceAsync(SelectedDevice);
        _selectAfterLoad = saved.Id;
        await LoadCoreAsync();
        Ui.SetStatus($"Saved device {saved.Name}.");
    }, errorTitle: "Unable to save device");

    [RelayCommand]
    private async Task DeleteAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedDevice is null || SelectedDevice.Id == 0) return;
        if (!Ui.Confirm($"Delete '{SelectedDevice.Name}'?\n\nAddress documentation is kept and unlinked; device history is removed.",
                "Delete device")) return;
        await _inventory.DeleteDeviceAsync(SelectedDevice.Id);
        SelectedDevice = null;
        await LoadCoreAsync();
    }, "Device deleted.", "Unable to delete device");

    [RelayCommand]
    private void CancelEdit()
    {
        if (SelectedDevice?.Id > 0) _selectAfterLoad = SelectedDevice.Id;
        SelectedDevice = null;
        IsEditorOpen = false;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void OpenMonitoring()
    {
        if (SelectedDevice?.Id > 0) _navigator.Navigate("Monitoring", SelectedDevice.Id);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedCategory = CategoryOptions[0];
        SelectedStatus = StatusOptions[0];
    }
}
