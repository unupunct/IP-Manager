using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Network;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class SubnetRow
{
    public required Subnet Subnet { get; init; }
    public SubnetUtilization? Utilization { get; init; }
    public string WarningsText => Utilization is null || Utilization.Warnings.Count == 0
        ? ""
        : string.Join(" · ", Utilization.Warnings);
    public double Percent => Utilization?.Percent ?? 0;
    public double BarWidth => Math.Clamp(Percent * 1.8, 0, 180);
    public string UsedText => Utilization is null ? "—" : $"{Utilization.Used:N0} / {Utilization.TotalHosts:N0}";
}

public sealed partial class SubnetManagerViewModel : ViewModelBase, IActivatable
{
    private readonly InventoryService _inventory;
    private readonly DiscoveryService _discovery;
    private readonly IUiNavigator _navigator;
    private int _selectAfterLoad;

    public SubnetManagerViewModel(InventoryService inventory, DiscoveryService discovery, IUiNavigator navigator, IUiService ui)
        : base(ui)
    {
        _inventory = inventory;
        _discovery = discovery;
        _navigator = navigator;
    }

    public ObservableCollection<SubnetRow> Rows { get; } = new();
    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = new();

    [ObservableProperty] private SubnetRow? _selectedRow;
    [ObservableProperty] private Subnet? _editorSubnet;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _editorTitle = "Subnet details";
    [ObservableProperty] private string _editorWarnings = "";
    [ObservableProperty] private NetworkInterfaceInfo? _selectedInterface;
    [ObservableProperty] private int _subnetCount;
    [ObservableProperty] private long _totalHosts;
    [ObservableProperty] private string _summary = "";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load subnets");

    private async Task LoadCoreAsync()
    {
        var keepId = _selectAfterLoad > 0 ? _selectAfterLoad : SelectedRow?.Subnet.Id ?? EditorSubnet?.Id ?? 0;
        _selectAfterLoad = 0;

        var subnets = await _inventory.GetSubnetsAsync();
        var utilizations = await _inventory.GetUtilizationAsync();
        Rows.Clear();
        foreach (var subnet in subnets)
        {
            Rows.Add(new SubnetRow
            {
                Subnet = subnet,
                Utilization = utilizations.FirstOrDefault(u => u.SubnetId == subnet.Id)
            });
        }
        SubnetCount = Rows.Count;
        TotalHosts = utilizations.Sum(u => u.TotalHosts);
        Summary = $"{SubnetCount} documented subnet(s) · {TotalHosts:N0} total host addresses";

        var interfaces = _discovery.GetInterfaces();
        Interfaces.Clear();
        foreach (var item in interfaces.Where(i => i.HasUsableIpv4)) Interfaces.Add(item);
        SelectedInterface ??= Interfaces.FirstOrDefault();

        if (keepId > 0) SelectedRow = Rows.FirstOrDefault(r => r.Subnet.Id == keepId);
        if (SelectedRow is null && EditorSubnet is null && Rows.Count > 0 && keepId == 0)
        {
            // Do not open the editor automatically; the list is the primary view.
        }
    }

    partial void OnSelectedRowChanged(SubnetRow? value)
    {
        if (value is null) return;
        EditorSubnet = Clone(value.Subnet);
        IsNew = false;
        IsEditorOpen = true;
        EditorTitle = value.Subnet.Cidr;
        EditorWarnings = value.WarningsText;
    }

    private static Subnet Clone(Subnet s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        NetworkAddress = s.NetworkAddress,
        PrefixLength = s.PrefixLength,
        Gateway = s.Gateway,
        DnsServers = s.DnsServers,
        VlanId = s.VlanId,
        Location = s.Location,
        Description = s.Description,
        DhcpStart = s.DhcpStart,
        DhcpEnd = s.DhcpEnd,
        StaticStart = s.StaticStart,
        StaticEnd = s.StaticEnd,
        ExcludedAddresses = s.ExcludedAddresses,
        MonitoringEnabled = s.MonitoringEnabled,
        IsAutoDiscovered = s.IsAutoDiscovered,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt
    };

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void NewSubnet()
    {
        EditorSubnet = new Subnet
        {
            Name = "",
            NetworkAddress = "",
            PrefixLength = 24,
            MonitoringEnabled = true
        };
        IsNew = true;
        IsEditorOpen = true;
        EditorTitle = "New subnet";
        EditorWarnings = "";
        SelectedRow = null;
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorSubnet is null) return;
        var result = await _inventory.SaveSubnetAsync(EditorSubnet);
        _selectAfterLoad = result.Subnet.Id;
        EditorWarnings = result.Warnings.Count == 0 ? "" : string.Join(Environment.NewLine, result.Warnings);
        await LoadCoreAsync();
        Ui.SetStatus(result.Warnings.Count == 0
            ? $"Saved subnet {result.Subnet.Cidr}."
            : $"Saved {result.Subnet.Cidr} with {result.Warnings.Count} warning(s).");
    }, errorTitle: "Unable to save the subnet");

    [RelayCommand]
    private async Task DeleteAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorSubnet is null || EditorSubnet.Id == 0) return;
        if (!Ui.Confirm($"Delete subnet {EditorSubnet.Cidr}?\n\nAddress entries belonging to it are kept but unlinked " +
                        "from the subnet. Devices remain in the inventory.", "Delete subnet")) return;
        await _inventory.DeleteSubnetAsync(EditorSubnet.Id);
        EditorSubnet = null;
        IsEditorOpen = false;
        SelectedRow = null;
        await LoadCoreAsync();
    }, "Subnet deleted.", "Unable to delete the subnet");

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditorOpen = false;
        EditorSubnet = null;
        SelectedRow = null;
    }

    [RelayCommand]
    private async Task AddFromAdapterAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedInterface?.SuggestedCidr is not { } cidr)
        {
            Ui.Info("Select an adapter with an IPv4 address first.");
            return;
        }
        if (!Ipv4Network.TryParseCidr(cidr, out var network, out var error))
            throw new ArgumentException(error);

        var subnet = new Subnet
        {
            Name = SelectedInterface.Name,
            NetworkAddress = network.Network.ToString(),
            PrefixLength = network.PrefixLength,
            Gateway = SelectedInterface.Gateway,
            DnsServers = string.Join(", ", SelectedInterface.DnsServers),
            Location = "Detected from adapter",
            MonitoringEnabled = true,
            IsAutoDiscovered = true,
            Description = SelectedInterface.Description
        };
        var result = await _inventory.SaveSubnetAsync(subnet);
        _selectAfterLoad = result.Subnet.Id;
        await LoadCoreAsync();
    }, "Subnet added from the selected adapter.", "Unable to add the adapter subnet");

    [RelayCommand]
    private void OpenAddressMap()
    {
        if (EditorSubnet?.Id is > 0) _navigator.Navigate("IpManager", EditorSubnet.Id);
    }

    [RelayCommand]
    private void OpenDhcp()
    {
        if (EditorSubnet?.Id is > 0) _navigator.Navigate("Dhcp", EditorSubnet.Id);
    }
}
