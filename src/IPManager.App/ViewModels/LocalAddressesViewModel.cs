using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Services;

namespace IPManager.App.ViewModels;

/// <summary>One square in the 16 x 16 local address map.</summary>
public sealed partial class LocalAddressCell : ObservableObject
{
    public required string Address { get; init; }
    public required int Index { get; init; }
    public required string RowLabel { get; init; }
    public required string ColumnLabel { get; init; }
    public IpStatus Status { get; init; }
    public DeviceStatus? DeviceStatus { get; init; }
    public bool IsBoundary { get; init; }
    public string? DeviceName { get; init; }
    public string? Hostname { get; init; }
    public string? MacAddress { get; init; }
    public AllocationType Allocation { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public string StatusText => IsBoundary
        ? (Index == 0 ? "Network address" : "Broadcast address")
        : Status switch
        {
            IpStatus.Assigned when DeviceStatus == IPManager.Core.DeviceStatus.Offline => "Offline",
            IpStatus.Assigned when DeviceStatus == IPManager.Core.DeviceStatus.Online => "Online",
            IpStatus.Assigned => "In use",
            IpStatus.Reserved => "Reserved",
            IpStatus.Gateway => "Gateway",
            IpStatus.Conflict => "Conflict",
            IpStatus.Excluded => "Excluded",
            IpStatus.Available => "Available",
            _ => Status.ToString()
        };

    public string DetailText
    {
        get
        {
            var parts = new List<string> { Address, StatusText };
            if (!string.IsNullOrWhiteSpace(DeviceName)) parts.Add(DeviceName);
            else if (!string.IsNullOrWhiteSpace(Hostname)) parts.Add(Hostname);
            if (!string.IsNullOrWhiteSpace(MacAddress)) parts.Add(MacAddress);
            return string.Join(" · ", parts);
        }
    }

    public string Tooltip => DetailText;
}

public sealed class LocalAddressRow
{
    public required string Label { get; init; }
    public required IReadOnlyList<LocalAddressCell> Cells { get; init; }
}

/// <summary>
/// Visual /24 address map. It renders exactly 256 squares (00-FF) and colours each square from
/// the documented address map, never from a guess. Non-/24 subnets are called out explicitly.
/// </summary>
public sealed partial class LocalAddressesViewModel : ViewModelBase, IActivatable
{
    private readonly InventoryService _inventory;
    private readonly IUiNavigator _navigator;
    private readonly AppPaths _paths;
    private bool _suppressSelection;

    public LocalAddressesViewModel(InventoryService inventory, IUiNavigator navigator, AppPaths paths, IUiService ui)
        : base(ui)
    {
        _inventory = inventory;
        _navigator = navigator;
        _paths = paths;
    }

    public ObservableCollection<Subnet> Subnets { get; } = new();
    public ObservableCollection<LocalAddressRow> Rows { get; } = new();
    public IReadOnlyList<string> ColumnLabels { get; } = Enumerable.Range(0, 16).Select(i => i.ToString("X")).ToArray();

    [ObservableProperty] private Subnet? _selectedSubnet;
    [ObservableProperty] private LocalAddressCell? _selectedCell;
    [ObservableProperty] private bool _hasMap;
    [ObservableProperty] private bool _hasSubnets;
    [ObservableProperty] private string _mapNotice = "";
    [ObservableProperty] private string _networkName = "";
    [ObservableProperty] private string _subnetCidr = "";
    [ObservableProperty] private string _gatewayAddress = "Not set";
    [ObservableProperty] private string _dnsServers = "Not set";
    [ObservableProperty] private string _locationText = "Not set";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _lastUpdated = "Never";
    [ObservableProperty] private int _usedCount;
    [ObservableProperty] private int _availableCount;
    [ObservableProperty] private int _gatewayCount;
    [ObservableProperty] private int _reservedCount;
    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _excludedCount;
    [ObservableProperty] private double _usedPercent;

    public int TotalAddresses => 256;
    public bool HasSelectedCell => SelectedCell is not null;

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load local addresses");

    private async Task LoadCoreAsync()
    {
        _suppressSelection = true;
        try
        {
            var previous = SelectedSubnet?.Id ?? 0;
            var loaded = await _inventory.GetSubnetsAsync();
            Subnets.Clear();
            foreach (var subnet in loaded.OrderBy(s => s.NetworkAddress, StringComparer.OrdinalIgnoreCase))
                Subnets.Add(subnet);
            HasSubnets = Subnets.Count > 0;
            SelectedSubnet = Subnets.FirstOrDefault(s => s.Id == previous)
                ?? Subnets.FirstOrDefault(s => s.PrefixLength == 24)
                ?? Subnets.FirstOrDefault();
        }
        finally
        {
            _suppressSelection = false;
        }

        await LoadMapCoreAsync();
    }

    private async Task LoadMapCoreAsync()
    {
        SelectedCell = null;
        Rows.Clear();
        ResetCounts();

        var subnet = SelectedSubnet;
        if (subnet is null)
        {
            HasMap = false;
            MapNotice = "No subnets are documented yet. Add a subnet or use Add from adapter on the Subnet Manager page.";
            NetworkName = "No subnet selected";
            SubnetCidr = "—";
            GatewayAddress = "Not set";
            DnsServers = "Not set";
            LocationText = "Not set";
            RangeText = "";
            LastUpdated = "Never";
            return;
        }

        NetworkName = string.IsNullOrWhiteSpace(subnet.Name) ? subnet.Cidr : subnet.Name;
        SubnetCidr = subnet.Cidr;
        GatewayAddress = string.IsNullOrWhiteSpace(subnet.Gateway) ? "Not set" : subnet.Gateway;
        DnsServers = string.IsNullOrWhiteSpace(subnet.DnsServers) ? "Not set" : subnet.DnsServers;
        LocationText = string.IsNullOrWhiteSpace(subnet.Location) ? "Not set" : subnet.Location;
        RangeText = subnet.NetworkOrNull?.RangeText ?? "";
        LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (subnet.PrefixLength != 24 || subnet.NetworkOrNull is not { } network)
        {
            HasMap = false;
            MapNotice = "The 256-square map is designed for /24 subnets. Select a /24 subnet, or add one from an adapter on the Subnet Manager page.";
            return;
        }

        var map = await _inventory.GetAddressMapAsync(subnet);
        var byAddress = map.Rows
            .GroupBy(r => r.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var baseValue = network.Network.Value;
        var cells = new List<LocalAddressCell>(256);
        for (var index = 0; index < 256; index++)
        {
            var address = new Ipv4Address(unchecked(baseValue + (uint)index));
            var ip = address.ToString();
            var isBoundary = index == 0 || index == 255;
            byAddress.TryGetValue(ip, out var mapRow);

            var status = mapRow?.Status ?? IpStatus.Available;
            if (isBoundary) status = IpStatus.Available;

            cells.Add(new LocalAddressCell
            {
                Address = ip,
                Index = index,
                RowLabel = (index / 16).ToString("X"),
                ColumnLabel = (index % 16).ToString("X"),
                Status = status,
                DeviceStatus = mapRow?.DeviceStatus,
                IsBoundary = isBoundary,
                DeviceName = mapRow?.DeviceName,
                Hostname = mapRow?.Hostname,
                MacAddress = mapRow?.MacAddress,
                Allocation = mapRow?.Allocation ?? AllocationType.Unknown
            });
        }

        for (var row = 0; row < 16; row++)
        {
            Rows.Add(new LocalAddressRow
            {
                Label = row.ToString("X"),
                Cells = cells.GetRange(row * 16, 16)
            });
        }

        UsedCount = cells.Count(c => !c.IsBoundary &&
            c.Status is IpStatus.Assigned or IpStatus.Reserved or IpStatus.Gateway or IpStatus.Conflict);
        AvailableCount = cells.Count(c => !c.IsBoundary && c.Status == IpStatus.Available);
        GatewayCount = cells.Count(c => c.Status == IpStatus.Gateway);
        ReservedCount = cells.Count(c => c.Status == IpStatus.Reserved);
        ConflictCount = cells.Count(c => c.Status == IpStatus.Conflict);
        ExcludedCount = cells.Count(c => c.Status == IpStatus.Excluded);
        UsedPercent = Math.Round(UsedCount * 100.0 / TotalAddresses, 1);
        HasMap = true;
        MapNotice = "";
    }

    private void ResetCounts()
    {
        UsedCount = 0;
        AvailableCount = 0;
        GatewayCount = 0;
        ReservedCount = 0;
        ConflictCount = 0;
        ExcludedCount = 0;
        UsedPercent = 0;
    }

    partial void OnSelectedSubnetChanged(Subnet? value)
    {
        if (_suppressSelection) return;
        _ = RunGuardedAsync(LoadMapCoreAsync, errorTitle: "Unable to load the address map");
    }

    partial void OnSelectedCellChanged(LocalAddressCell? oldValue, LocalAddressCell? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(HasSelectedCell));
    }

    [RelayCommand]
    private void SelectCell(LocalAddressCell? cell) => SelectedCell = cell;

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void ScanNetwork()
    {
        if (SelectedSubnet is { PrefixLength: 24 } subnet)
            _navigator.Navigate("Discovery", subnet.Cidr);
        else
            _navigator.Navigate("Discovery");
    }

    [RelayCommand]
    private void ViewActiveDevices() => _navigator.Navigate("Devices");

    [RelayCommand]
    private void CheckConflicts() => _navigator.Navigate("Conflicts");

    [RelayCommand]
    private void ExportResults()
    {
        if (!HasMap)
        {
            Ui.SetStatus("There is no /24 address map to export.");
            return;
        }

        var cidr = SelectedSubnet?.Cidr ?? "local";
        var fileName = $"local-addresses-{cidr.Replace('/', '-').Replace('.', '-')}.csv";
        var path = Ui.SaveFile("CSV files (*.csv)|*.csv", fileName, _paths.ExportDirectory);
        if (string.IsNullOrWhiteSpace(path)) return;

        var builder = new StringBuilder();
        builder.AppendLine("Address,Status,Boundary,Device,Hostname,MAC,Allocation");
        foreach (var cell in Rows.SelectMany(r => r.Cells))
        {
            builder.Append(Csv(cell.Address)).Append(',')
                .Append(Csv(cell.StatusText)).Append(',')
                .Append(cell.IsBoundary ? "yes" : "no").Append(',')
                .Append(Csv(cell.DeviceName)).Append(',')
                .Append(Csv(cell.Hostname)).Append(',')
                .Append(Csv(cell.MacAddress)).Append(',')
                .Append(Csv(cell.Allocation.ToString()))
                .AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        Ui.SetStatus($"Exported 256 local addresses to {path}");
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
