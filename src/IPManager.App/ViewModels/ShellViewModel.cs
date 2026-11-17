using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.App.ViewModels;
using IPManager.Infrastructure;
using IPManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace IPManager.App.ViewModels;

public sealed record NavigationItem(string Key, string Title, string Glyph);

public sealed partial class ShellViewModel : ObservableObject, IUiNavigator
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IUiService _ui;
    private readonly IAlertService _alerts;
    private bool _navigating;
    private string _currentKey = "";
    private string? _pendingKey;
    private object? _pendingArgument;

    public ShellViewModel(IServiceProvider services, ISettingsService settings, IThemeService theme,
        IUiService ui, IAlertService alerts)
    {
        _services = services;
        _settings = settings;
        _theme = theme;
        _ui = ui;
        _alerts = alerts;
        _ui.StatusChanged += (_, message) => _ui.RunOnUi(() =>
        {
            StatusText = message;
            StatusTime = DateTime.Now;
        });
        _alerts.AlertRaised += (_, _) => _ui.RunOnUi(() => _ = RefreshAlertCountAsync());

        Navigation = new ObservableCollection<NavigationItem>(new[]
        {
            new NavigationItem("Dashboard", "Dashboard", "\uE80F"),
            new NavigationItem("LocalAddresses", "Local Addresses", "\uE71D"),
            new NavigationItem("Discovery", "Discovery", "\uE721"),
            new NavigationItem("Devices", "Devices", "\uE772"),
            new NavigationItem("IpManager", "IP Manager", "\uE8EF"),
            new NavigationItem("SubnetCalculator", "Subnet Calculator", "\uE8EF"),
            new NavigationItem("SubnetManager", "Subnet Manager", "\uE968"),
            new NavigationItem("Dhcp", "DHCP", "\uE968"),
            new NavigationItem("Dns", "DNS", "\uE774"),
            new NavigationItem("Conflicts", "Conflicts", "\uE7BA"),
            new NavigationItem("NetworkMap", "Network Map", "\uE809"),
            new NavigationItem("Monitoring", "Monitoring", "\uE9D9"),
            new NavigationItem("Diagnostics", "Diagnostics", "\uE9D9"),
            new NavigationItem("Alerts", "Alerts", "\uEA8F"),
            new NavigationItem("Reports", "Reports", "\uE8A5"),
            new NavigationItem("Settings", "Settings", "\uE713")
        });

        IsDarkTheme = _theme.IsDark;
    }

    public ObservableCollection<NavigationItem> Navigation { get; }

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _pageTitle = "Dashboard";

    [ObservableProperty]
    private string _pageSubtitle = "Network overview and address space health";

    [ObservableProperty]
    private NavigationItem? _selectedNavigation;

    [ObservableProperty]
    private string _globalSearch = "";

    [ObservableProperty]
    private int _unreadAlerts;

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private DateTime _statusTime = DateTime.Now;

    public event EventHandler<NavigationRequest>? NavigationRequested;

    public async Task InitializeAsync()
    {
        var last = _settings.Current.LastPage;
        var key = Navigation.Any(n => n.Key == last) ? last : "Dashboard";
        await NavigateCoreAsync(key, null);
        await RefreshAlertCountAsync();
    }

    public void Navigate(string pageKey, object? argument = null) => _ui.RunOnUi(() => RequestNavigation(pageKey, argument));

    private void RequestNavigation(string pageKey, object? argument)
    {
        Log.Information("Navigation requested: {PageKey} (busy={Busy}, current={Current})", pageKey, _navigating, _currentKey);
        if (_navigating)
        {
            _pendingKey = pageKey;
            _pendingArgument = argument;
            if (CurrentPage is IActivatable running) running.Deactivate();
            return;
        }
        _ = NavigateCoreAsync(pageKey, argument);
    }

    partial void OnSelectedNavigationChanged(NavigationItem? value)
    {
        Log.Information("Selected navigation changed: {Key}", value?.Key ?? "(null)");
        if (value is null || value.Key == _currentKey) return;
        RequestNavigation(value.Key, null);
    }

    private async Task NavigateCoreAsync(string key, object? argument)
    {
        var item = Navigation.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        Log.Information("Navigating to {Key}", item.Key);

        _navigating = true;
        try
        {
            if (CurrentPage is IActivatable previous) previous.Deactivate();

            var page = ResolvePage(item.Key);
            if (page is null)
            {
                _ui.Error($"The page '{item.Title}' is not available.");
                return;
            }

            _currentKey = item.Key;
            PageTitle = item.Title;
            PageSubtitle = Subtitles.TryGetValue(item.Key, out var subtitle) ? subtitle : "";
            SelectedNavigation = item;
            CurrentPage = page;

            if (argument is not null && page is INavigationAware aware)
                await aware.OnNavigatedToAsync(argument);
            else if (page is IActivatable activatable)
                await activatable.ActivateAsync();
            else
                await page.LoadAsync();

            _settings.Current.LastPage = item.Key;
            try { await _settings.SaveAsync(); } catch { /* persistence must not block navigation */ }
            NavigationRequested?.Invoke(this, new NavigationRequest(item.Key, argument));
            Log.Information("Navigation completed: {Key}", item.Key);
        }
        catch (Exception ex)
        {
            _ui.Error(ex.Message, "Unable to open page");
        }
        finally
        {
            _navigating = false;
            if (_pendingKey is { } pendingKey)
            {
                var pendingArgument = _pendingArgument;
                _pendingKey = null;
                _pendingArgument = null;
                if (!string.Equals(pendingKey, _currentKey, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Running pending navigation: {Key}", pendingKey);
                    _ = NavigateCoreAsync(pendingKey, pendingArgument);
                }
            }
        }
    }

    private ViewModelBase? ResolvePage(string key) => key switch
    {
        "Dashboard" => _services.GetRequiredService<DashboardViewModel>(),
        "LocalAddresses" => _services.GetRequiredService<LocalAddressesViewModel>(),
        "Discovery" => _services.GetRequiredService<DiscoveryViewModel>(),
        "Devices" => _services.GetRequiredService<DevicesViewModel>(),
        "IpManager" => _services.GetRequiredService<IpManagerViewModel>(),
        "SubnetCalculator" => _services.GetRequiredService<SubnetCalculatorViewModel>(),
        "SubnetManager" => _services.GetRequiredService<SubnetManagerViewModel>(),
        "Dhcp" => _services.GetRequiredService<DhcpViewModel>(),
        "Dns" => _services.GetRequiredService<DnsViewModel>(),
        "Conflicts" => _services.GetRequiredService<ConflictsViewModel>(),
        "NetworkMap" => _services.GetRequiredService<NetworkMapViewModel>(),
        "Monitoring" => _services.GetRequiredService<MonitoringViewModel>(),
        "Diagnostics" => _services.GetRequiredService<DiagnosticsViewModel>(),
        "Alerts" => _services.GetRequiredService<AlertsViewModel>(),
        "Reports" => _services.GetRequiredService<ReportsViewModel>(),
        "Settings" => _services.GetRequiredService<SettingsViewModel>(),
        _ => null
    };

    [RelayCommand]
    private async Task RefreshAlertCountAsync()
    {
        try { UnreadAlerts = await _alerts.GetUnreadCountAsync(); }
        catch { UnreadAlerts = 0; }
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        _settings.Current.Theme = _theme.IsDark ? "Light" : "Dark";
        _theme.Apply(_settings.Current.Theme);
        IsDarkTheme = _theme.IsDark;
        await _settings.SaveAsync();
    }

    [RelayCommand]
    private void GoAlerts() => Navigate("Alerts");

    [RelayCommand]
    private void Search()
    {
        var term = GlobalSearch.Trim();
        Navigate("Devices", term.Length == 0 ? null : term);
    }

    [RelayCommand]
    private void ClearSearch() => GlobalSearch = "";

    private static readonly Dictionary<string, string> Subtitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dashboard"] = "Live inventory, allocation health and recent activity",
        ["LocalAddresses"] = "A 256-square /24 map with documented, reserved and conflicting addresses",
        ["Discovery"] = "Conservative LAN discovery over ARP, ICMP, TCP and name services",
        ["Devices"] = "Every known device, with history and monitoring controls",
        ["IpManager"] = "Address-by-address documentation for the selected subnet",
        ["SubnetCalculator"] = "IPv4 and IPv6 subnet math, masks and division",
        ["SubnetManager"] = "Subnets, gateways, DHCP pools and static ranges",
        ["Dhcp"] = "DHCP documentation plus read-only provider imports",
        ["Dns"] = "Resolver lookups, local records and DNS integrations",
        ["Conflicts"] = "Evidence-based duplicate and live address conflicts",
        ["NetworkMap"] = "Verified, inferred and unknown relationships only",
        ["Monitoring"] = "Availability checks, uptime history and targets",
        ["Diagnostics"] = "Ping, traceroute, port probes, ARP cache and adapters",
        ["Alerts"] = "Triage, acknowledge and retain operational alerts",
        ["Reports"] = "Inventory, utilization, DHCP, DNS and availability exports",
        ["Settings"] = "Discovery, monitoring, storage, notifications and appearance"
    };
}
