using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class AddressStatusOption
{
    public required string Name { get; init; }
    public IpStatus? Value { get; init; }
}

public sealed partial class IpManagerViewModel : ViewModelBase, IActivatable, INavigationAware
{
    private readonly InventoryService _inventory;
    private readonly IUiNavigator _navigator;
    private bool _suppressSubnetReload;
    private bool _suppressRowEdit;

    public IpManagerViewModel(InventoryService inventory, IUiNavigator navigator, IUiService ui) : base(ui)
    {
        _inventory = inventory;
        _navigator = navigator;
        StatusOptions = new ObservableCollection<AddressStatusOption>
        {
            new() { Name = "All statuses" },
            new() { Name = "Available", Value = IpStatus.Available },
            new() { Name = "Assigned", Value = IpStatus.Assigned },
            new() { Name = "Reserved", Value = IpStatus.Reserved },
            new() { Name = "Gateway", Value = IpStatus.Gateway },
            new() { Name = "Conflict", Value = IpStatus.Conflict },
            new() { Name = "Excluded", Value = IpStatus.Excluded },
            new() { Name = "Unknown", Value = IpStatus.Unknown }
        };
        SelectedStatus = StatusOptions[0];
        AllocationValues = Enum.GetValues<AllocationType>().ToList();
        StatusValues = Enum.GetValues<IpStatus>().ToList();
    }

    public ObservableCollection<Subnet> Subnets { get; } = new();
    public ObservableCollection<AddressMapRow> Rows { get; } = new();
    public ObservableCollection<AddressMapRow> VisibleRows { get; } = new();
    public ObservableCollection<AddressStatusOption> StatusOptions { get; }
    public IReadOnlyList<AllocationType> AllocationValues { get; }
    public IReadOnlyList<IpStatus> StatusValues { get; }

    [ObservableProperty] private Subnet? _selectedSubnet;
    [ObservableProperty] private AddressStatusOption? _selectedStatus;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private AddressMapRow? _selectedRow;
    [ObservableProperty] private IpAddressEntry? _editingAddress;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isNewEntry;
    [ObservableProperty] private string _editorTitle = "Address details";
    [ObservableProperty] private long _totalHosts;
    [ObservableProperty] private int _assignedCount;
    [ObservableProperty] private int _reservedCount;
    [ObservableProperty] private int _availableCount;
    [ObservableProperty] private int _gatewayCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _excludedCount;
    [ObservableProperty] private double _utilizationPercent;
    [ObservableProperty] private bool _truncated;
    [ObservableProperty] private string _subnetSummary = "Select a subnet to inspect its address space.";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public async Task OnNavigatedToAsync(object? argument)
    {
        await LoadAsync();
        if (argument is int subnetId) await GoToSubnetAsync(subnetId);
    }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load addresses");

    private async Task LoadCoreAsync()
    {
        var keepId = SelectedSubnet?.Id ?? 0;
        var subnets = await _inventory.GetSubnetsAsync();
        _suppressSubnetReload = true;
        try
        {
            Subnets.Clear();
            foreach (var subnet in subnets) Subnets.Add(subnet);
            SelectedSubnet = Subnets.FirstOrDefault(s => s.Id == keepId) ?? Subnets.FirstOrDefault();
        }
        finally
        {
            _suppressSubnetReload = false;
        }
        await LoadMapCoreAsync();
    }

    partial void OnSelectedSubnetChanged(Subnet? value)
    {
        if (_suppressSubnetReload) return;
        _ = LoadMapSafeAsync();
    }

    private async Task LoadMapSafeAsync()
    {
        try
        {
            await LoadMapCoreAsync();
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message, "Unable to load the address map");
        }
    }

    private async Task LoadMapCoreAsync()
    {
        if (SelectedSubnet is null)
        {
            Rows.Clear();
            VisibleRows.Clear();
            SubnetSummary = "No subnet selected.";
            return;
        }

        var map = await _inventory.GetAddressMapAsync(SelectedSubnet);
        Rows.Clear();
        foreach (var row in map.Rows) Rows.Add(row);
        TotalHosts = map.TotalHosts;
        AssignedCount = map.AssignedCount;
        ReservedCount = map.ReservedCount;
        AvailableCount = map.AvailableCount;
        GatewayCount = map.GatewayCount;
        ConflictCount = map.ConflictCount;
        ExcludedCount = map.ExcludedCount;
        UtilizationPercent = map.UtilizationPercent;
        Truncated = map.Truncated;
        SubnetSummary = $"{SelectedSubnet.Name} · {SelectedSubnet.Cidr} · {map.TotalHosts:N0} host addresses" +
                        (map.Truncated ? " · display limited to the first 4096 rows" : "");
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedStatusChanged(AddressStatusOption? value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = Rows.AsEnumerable();
        if (SelectedStatus?.Value is { } status) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            query = query.Where(r =>
                r.Address.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                (r.DeviceName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.MacAddress?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Hostname?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Description?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        VisibleRows.Clear();
        foreach (var row in query) VisibleRows.Add(row);
    }

    partial void OnSelectedRowChanged(AddressMapRow? value)
    {
        if (_suppressRowEdit) return;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void AddAddress()
    {
        if (SelectedSubnet is null)
        {
            Ui.Info("Create or select a subnet first.");
            return;
        }
        EditingAddress = new IpAddressEntry
        {
            SubnetId = SelectedSubnet.Id,
            Address = NextFreeAddress() ?? "",
            Status = IpStatus.Reserved,
            AllocationType = AllocationType.Static,
            IsManuallyManaged = true
        };
        IsNewEntry = true;
        IsEditorOpen = true;
        EditorTitle = "Reserve / assign address";
    }

    [RelayCommand]
    private async Task EditSelectedAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedRow is null) return;
        var entry = await _inventory.FindAddressAsync(SelectedRow.Address);
        entry ??= new IpAddressEntry
        {
            SubnetId = SelectedSubnet?.Id,
            Address = SelectedRow.Address,
            Status = SelectedRow.Status == IpStatus.Available ? IpStatus.Reserved : SelectedRow.Status,
            AllocationType = SelectedRow.Allocation,
            IsManuallyManaged = true
        };
        EditingAddress = entry;
        IsNewEntry = entry.Id == 0;
        IsEditorOpen = true;
        EditorTitle = $"{SelectedRow.Address} · {SelectedRow.Status}";
    }, errorTitle: "Unable to open the address");

    [RelayCommand]
    private void FindFree()
    {
        var free = Rows.FirstOrDefault(r => r.Status == IpStatus.Available && !r.InDhcpRange)
                   ?? Rows.FirstOrDefault(r => r.Status == IpStatus.Available);
        if (free is null)
        {
            Ui.Info("No available address was found in this subnet.", "No free address");
            return;
        }
        SelectedRow = free;
        EditingAddress = new IpAddressEntry
        {
            SubnetId = SelectedSubnet?.Id,
            Address = free.Address,
            Status = IpStatus.Reserved,
            AllocationType = AllocationType.Static,
            IsManuallyManaged = true
        };
        IsNewEntry = true;
        IsEditorOpen = true;
        EditorTitle = $"Reserve {free.Address}";
    }

    private string? NextFreeAddress()
    {
        var free = Rows.FirstOrDefault(r => r.Status == IpStatus.Available && !r.InDhcpRange)
                   ?? Rows.FirstOrDefault(r => r.Status == IpStatus.Available);
        return free?.Address;
    }

    [RelayCommand]
    private async Task SaveEntryAsync() => await RunGuardedAsync(async () =>
    {
        if (EditingAddress is null) return;
        if (EditingAddress.SubnetId is null && SelectedSubnet is not null) EditingAddress.SubnetId = SelectedSubnet.Id;
        await _inventory.SaveAddressAsync(EditingAddress);
        IsEditorOpen = false;
        EditingAddress = null;
        await LoadMapCoreAsync();
    }, "Address saved.", "Unable to save the address");

    [RelayCommand]
    private async Task ReleaseEntryAsync() => await RunGuardedAsync(async () =>
    {
        if (EditingAddress is null || EditingAddress.Id == 0)
        {
            IsEditorOpen = false;
            EditingAddress = null;
            return;
        }
        if (!Ui.Confirm($"Remove the documentation entry for {EditingAddress.Address}?\n\nThe address becomes available. " +
                        "A linked device keeps its own record.", "Release address")) return;
        await _inventory.DeleteAddressAsync(EditingAddress.Id);
        IsEditorOpen = false;
        EditingAddress = null;
        await LoadMapCoreAsync();
    }, "Address released.", "Unable to release the address");

    [RelayCommand]
    private async Task DeleteRowEntryAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedRow?.EntryId is not { } id) return;
        if (!Ui.Confirm($"Remove the documentation entry for {SelectedRow.Address}?", "Release address")) return;
        await _inventory.DeleteAddressAsync(id);
        await LoadMapCoreAsync();
    }, "Address released.", "Unable to release the address");

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditorOpen = false;
        EditingAddress = null;
    }

    [RelayCommand]
    private void OpenDevice()
    {
        var deviceId = SelectedRow?.DeviceId;
        if (deviceId is > 0) _navigator.Navigate("Devices", deviceId.Value);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedStatus = StatusOptions[0];
    }

    public async Task GoToSubnetAsync(int subnetId)
    {
        _suppressSubnetReload = true;
        try { SelectedSubnet = Subnets.FirstOrDefault(s => s.Id == subnetId) ?? SelectedSubnet; }
        finally { _suppressSubnetReload = false; }
        await LoadMapCoreAsync();
    }
}
