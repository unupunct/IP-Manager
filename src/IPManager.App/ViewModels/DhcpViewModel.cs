using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class DhcpProviderInfo
{
    public required DhcpProviderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string Requirements { get; init; }
    public bool CanRead { get; init; }
    public bool CanWrite { get; init; }
    public string AccessText => CanWrite ? "Read + write" : CanRead ? "Read only" : "Unavailable";
}

public sealed partial class DhcpViewModel : ViewModelBase, IActivatable, INavigationAware
{
    private readonly DhcpService _dhcp;
    private readonly InventoryService _inventory;
    private readonly IUiNavigator _navigator;
    private int _selectAfterLoad;

    public DhcpViewModel(DhcpService dhcp, InventoryService inventory, IUiNavigator navigator, IUiService ui) : base(ui)
    {
        _dhcp = dhcp;
        _inventory = inventory;
        _navigator = navigator;
    }

    public ObservableCollection<DhcpScope> Scopes { get; } = new();
    public ObservableCollection<DhcpReservation> Reservations { get; } = new();
    public ObservableCollection<Subnet> Subnets { get; } = new();
    public ObservableCollection<DhcpProviderInfo> Providers { get; } = new();
    public ObservableCollection<RemoteDhcpScope> RemoteScopes { get; } = new();
    public ObservableCollection<RemoteDhcpLease> RemoteLeases { get; } = new();
    public ObservableCollection<LocalDhcpClientInfo> LocalClients { get; } = new();

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private DhcpScope? _selectedScope;
    [ObservableProperty] private DhcpScope? _editorScope;
    [ObservableProperty] private bool _isScopeEditorOpen;
    [ObservableProperty] private string _scopeTitle = "Scope details";
    [ObservableProperty] private string _scopeWarnings = "";
    [ObservableProperty] private DhcpReservation? _selectedReservation;
    [ObservableProperty] private DhcpReservation? _editorReservation;
    [ObservableProperty] private bool _isReservationEditorOpen;
    [ObservableProperty] private string _reservationTitle = "Reservation details";
    [ObservableProperty] private DhcpProviderInfo? _selectedProvider;
    [ObservableProperty] private string _providerPath = "";
    [ObservableProperty] private string _providerOptions = "";
    [ObservableProperty] private string _providerMessage = "";
    [ObservableProperty] private RemoteDhcpScope? _selectedRemoteScope;
    [ObservableProperty] private string _scopeSummary = "";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public async Task OnNavigatedToAsync(object? argument)
    {
        await LoadAsync();
        if (argument is int subnetId) NewScopeForSubnet(subnetId);
    }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load DHCP data");

    private async Task LoadCoreAsync()
    {
        var keepId = _selectAfterLoad > 0 ? _selectAfterLoad : SelectedScope?.Id ?? 0;
        _selectAfterLoad = 0;

        var subnets = await _inventory.GetSubnetsAsync();
        Subnets.Clear();
        foreach (var subnet in subnets) Subnets.Add(subnet);

        if (Providers.Count == 0)
        {
            foreach (var provider in _dhcp.Providers)
            {
                Providers.Add(new DhcpProviderInfo
                {
                    Kind = provider.Kind,
                    DisplayName = provider.DisplayName,
                    Requirements = provider.Requirements,
                    CanRead = provider.CanRead,
                    CanWrite = provider.CanWrite
                });
            }
            SelectedProvider = Providers.FirstOrDefault();
        }

        var scopes = await _dhcp.GetScopesAsync();
        Scopes.Clear();
        foreach (var scope in scopes) Scopes.Add(scope);
        ScopeSummary = $"{scopes.Count} documented scope(s) · {scopes.Sum(s => s.Reservations.Count)} reservation(s)";

        SelectedScope = Scopes.FirstOrDefault(s => s.Id == keepId) ?? Scopes.FirstOrDefault();
        if (SelectedScope is null)
        {
            Reservations.Clear();
            EditorScope = null;
            IsScopeEditorOpen = false;
        }
    }

    partial void OnSelectedScopeChanged(DhcpScope? value)
    {
        Reservations.Clear();
        SelectedReservation = null;
        IsReservationEditorOpen = false;
        if (value is null) return;
        foreach (var reservation in value.Reservations.OrderBy(r => r.IpAddress)) Reservations.Add(reservation);
        EditorScope = CloneScope(value);
        IsScopeEditorOpen = true;
        ScopeTitle = $"{value.Name} · {value.Range}";
        ScopeWarnings = "";
    }

    private static DhcpScope CloneScope(DhcpScope s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        SubnetId = s.SubnetId,
        ServerAddress = s.ServerAddress,
        Provider = s.Provider,
        StartAddress = s.StartAddress,
        EndAddress = s.EndAddress,
        LeaseMinutes = s.LeaseMinutes,
        ExcludedAddresses = s.ExcludedAddresses,
        Notes = s.Notes,
        Enabled = s.Enabled,
        LastSyncedAt = s.LastSyncedAt,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt
    };

    partial void OnSelectedReservationChanged(DhcpReservation? value)
    {
        if (value is null) return;
        EditorReservation = new DhcpReservation
        {
            Id = value.Id,
            DhcpScopeId = value.DhcpScopeId,
            IpAddress = value.IpAddress,
            MacAddress = value.MacAddress,
            Hostname = value.Hostname,
            Description = value.Description,
            Source = value.Source,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt
        };
        IsReservationEditorOpen = true;
        ReservationTitle = $"{value.IpAddress} · {value.MacAddress}";
    }

    partial void OnSelectedProviderChanged(DhcpProviderInfo? value)
    {
        if (value is null) return;
        _ = LoadProviderConfigAsync(value.Kind);
    }

    private async Task LoadProviderConfigAsync(DhcpProviderKind kind)
    {
        try
        {
            var config = await _dhcp.GetProviderConfigAsync(kind);
            ProviderPath = config?.Endpoint ?? "";
            ProviderOptions = config?.OptionsJson ?? "";
            ProviderMessage = config is null ? "Not configured." : $"Configured · state {config.State}";
        }
        catch (Exception ex)
        {
            ProviderMessage = ex.Message;
        }
    }

    partial void OnSelectedRemoteScopeChanged(RemoteDhcpScope? value)
    {
        RemoteLeases.Clear();
        if (value is null) return;
        foreach (var lease in value.Leases.OrderBy(l => l.IpAddress)) RemoteLeases.Add(lease);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void NewScope()
    {
        if (Subnets.Count == 0)
        {
            Ui.Info("Create a subnet first on the Subnet Manager page.");
            return;
        }
        NewScopeForSubnet(Subnets[0].Id);
    }

    private void NewScopeForSubnet(int subnetId)
    {
        var subnet = Subnets.FirstOrDefault(s => s.Id == subnetId) ?? Subnets.FirstOrDefault();
        if (subnet is null) return;
        var network = subnet.NetworkOrNull;
        var start = subnet.DhcpStart ?? network?.FirstUsable.ToString() ?? "";
        var end = subnet.DhcpEnd ?? network?.LastUsable.ToString() ?? "";
        EditorScope = new DhcpScope
        {
            Name = $"{subnet.Name} pool",
            SubnetId = subnet.Id,
            Provider = DhcpProviderKind.LocalDocumentation.ToString(),
            StartAddress = start,
            EndAddress = end,
            ServerAddress = subnet.Gateway,
            LeaseMinutes = 1440,
            Enabled = true
        };
        IsScopeEditorOpen = true;
        ScopeTitle = "New DHCP scope";
        ScopeWarnings = "";
        SelectedScope = null;
        SelectedTabIndex = 0;
    }

    [RelayCommand]
    private async Task SaveScopeAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorScope is null) return;
        var result = await _dhcp.SaveScopeAsync(EditorScope);
        _selectAfterLoad = result.Scope.Id;
        ScopeWarnings = result.Warnings.Count == 0 ? "" : string.Join(Environment.NewLine, result.Warnings);
        await LoadCoreAsync();
        Ui.SetStatus(result.Warnings.Count == 0
            ? $"Saved DHCP scope {result.Scope.Name}."
            : $"Saved {result.Scope.Name} with {result.Warnings.Count} warning(s).");
    }, errorTitle: "Unable to save the DHCP scope");

    [RelayCommand]
    private async Task DeleteScopeAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorScope is null || EditorScope.Id == 0) return;
        if (!Ui.Confirm($"Delete DHCP scope '{EditorScope.Name}' and its {Reservations.Count} reservation(s)?", "Delete scope")) return;
        await _dhcp.DeleteScopeAsync(EditorScope.Id);
        SelectedScope = null;
        EditorScope = null;
        IsScopeEditorOpen = false;
        await LoadCoreAsync();
    }, "DHCP scope deleted.", "Unable to delete the scope");

    [RelayCommand]
    private void CancelScopeEdit()
    {
        IsScopeEditorOpen = false;
        EditorScope = null;
        SelectedScope = null;
    }

    [RelayCommand]
    private void NewReservation()
    {
        if (SelectedScope is null)
        {
            Ui.Info("Select a DHCP scope first.");
            return;
        }
        EditorReservation = new DhcpReservation
        {
            DhcpScopeId = SelectedScope.Id,
            IpAddress = NextFreeReservationAddress(),
            MacAddress = "",
            Source = "Manual"
        };
        IsReservationEditorOpen = true;
        IsReservationEditorOpen = true;
        ReservationTitle = "New reservation";
    }

    private string NextFreeReservationAddress()
    {
        if (SelectedScope is null) return "";
        if (!Ipv4Address.TryParse(SelectedScope.StartAddress, out var start) ||
            !Ipv4Address.TryParse(SelectedScope.EndAddress, out var end))
            return SelectedScope.StartAddress;
        var used = Reservations.Select(r => r.IpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var value = start.Value; value <= end.Value && value - start.Value < 4096; value++)
        {
            var candidate = new Ipv4Address(value).ToString();
            if (!used.Contains(candidate)) return candidate;
        }
        return SelectedScope.StartAddress;
    }

    [RelayCommand]
    private async Task SaveReservationAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorReservation is null) return;
        if (SelectedScope is not null && EditorReservation.DhcpScopeId == 0)
            EditorReservation.DhcpScopeId = SelectedScope.Id;
        await _dhcp.SaveReservationAsync(EditorReservation);
        var scopeId = EditorReservation.DhcpScopeId;
        _selectAfterLoad = scopeId;
        IsReservationEditorOpen = false;
        EditorReservation = null;
        await LoadCoreAsync();
        Ui.SetStatus("DHCP reservation saved.");
    }, errorTitle: "Unable to save the reservation");

    [RelayCommand]
    private async Task DeleteReservationAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorReservation is null || EditorReservation.Id == 0) return;
        if (!Ui.Confirm($"Delete the reservation for {EditorReservation.IpAddress}?", "Delete reservation")) return;
        await _dhcp.DeleteReservationAsync(EditorReservation.Id);
        var scopeId = EditorReservation.DhcpScopeId;
        _selectAfterLoad = scopeId;
        IsReservationEditorOpen = false;
        EditorReservation = null;
        await LoadCoreAsync();
    }, "Reservation deleted.", "Unable to delete the reservation");

    [RelayCommand]
    private void CancelReservationEdit()
    {
        IsReservationEditorOpen = false;
        EditorReservation = null;
        SelectedReservation = null;
    }

    [RelayCommand]
    private async Task SaveProviderAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedProvider is null) return;
        await _dhcp.SaveProviderConfigAsync(SelectedProvider.Kind, ProviderPath, ProviderOptions);
        ProviderMessage = "Configuration saved (no secret is stored for DHCP file providers).";
    }, "DHCP provider configuration saved.", "Unable to save the provider configuration");

    [RelayCommand]
    private async Task ReadProviderAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedProvider is null) return;
        RemoteScopes.Clear();
        RemoteLeases.Clear();
        var result = await _dhcp.ReadFromProviderAsync(SelectedProvider.Kind);
        ProviderMessage = result.Message;
        if (!result.Success)
        {
            Ui.Error(result.Message, "DHCP provider read failed");
            return;
        }
        foreach (var scope in result.Scopes) RemoteScopes.Add(scope);
        SelectedRemoteScope = RemoteScopes.FirstOrDefault();
        ProviderMessage = $"{result.Message} · {result.Scopes.Count} scope(s) read.";
    }, errorTitle: "DHCP provider read failed");

    [RelayCommand]
    private async Task ImportProviderAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedProvider is null) return;
        if (!Ui.Confirm("Copy the remote DHCP snapshot into IP Manager documentation?\n\nThis only changes the local " +
                        "database. The DHCP server itself is never modified.", "Import DHCP snapshot")) return;
        var count = await _dhcp.ImportAsync(SelectedProvider.Kind);
        await LoadCoreAsync();
        ProviderMessage = $"Imported {count} record(s) into local documentation.";
    }, "DHCP snapshot imported.", "DHCP import failed");

    [RelayCommand]
    private async Task LoadLocalClientsAsync() => await RunGuardedAsync(async () =>
    {
        var clients = await _dhcp.GetLocalClientInfoAsync();
        LocalClients.Clear();
        foreach (var client in clients) LocalClients.Add(client);
        if (LocalClients.Count == 0)
            Ui.Info("No DHCP-enabled network adapters were reported by Windows.");
        SelectedTabIndex = 3;
    }, errorTitle: "Unable to read local DHCP client information");

    [RelayCommand]
    private void OpenSubnets() => _navigator.Navigate("SubnetManager");
}
