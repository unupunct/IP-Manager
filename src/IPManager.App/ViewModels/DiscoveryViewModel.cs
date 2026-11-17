using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed partial class DiscoveryViewModel : ViewModelBase, IActivatable, INavigationAware
{
    private readonly DiscoveryService _discovery;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _cts;
    private bool _autoScanDone;

    public DiscoveryViewModel(DiscoveryService discovery, ISettingsService settings, IUiService ui) : base(ui)
    {
        _discovery = discovery;
        _settings = settings;
    }

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = new();
    public ObservableCollection<DiscoveredHost> Results { get; } = new();
    public ObservableCollection<ScanSession> History { get; } = new();

    [ObservableProperty] private NetworkInterfaceInfo? _selectedInterface;
    [ObservableProperty] private string _targets = "";
    [ObservableProperty] private bool _allowExternal;
    [ObservableProperty] private bool _enableArp = true;
    [ObservableProperty] private bool _enableIcmp = true;
    [ObservableProperty] private bool _enableTcp = true;
    [ObservableProperty] private bool _enableDns = true;
    [ObservableProperty] private bool _enableMdns = true;
    [ObservableProperty] private bool _enableNetBios = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "Not started";
    [ObservableProperty] private int _foundCount;
    [ObservableProperty] private int _checkedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _currentTarget = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private DiscoveredHost? _selectedHost;
    [ObservableProperty] private ScanSession? _selectedSession;

    public async Task ActivateAsync()
    {
        await LoadAsync();
        if (!_autoScanDone && _settings.Current.AutoScanOnStartup && !string.IsNullOrWhiteSpace(Targets))
        {
            _autoScanDone = true;
            _ = ScanAsync();
        }
    }

    public async Task OnNavigatedToAsync(object? argument)
    {
        await ActivateAsync();
        if (argument is string target && !string.IsNullOrWhiteSpace(target))
        {
            Targets = target;
            _autoScanDone = true;
            _ = ScanAsync();
        }
    }

    public void Deactivate()
    {
        if (IsScanning) _cts?.Cancel();
    }

    public override async Task LoadAsync() => await RunGuardedAsync(async () =>
    {
        var loaded = _discovery.GetInterfaces();
        var previous = SelectedInterface?.Id;
        Interfaces.Clear();
        foreach (var item in loaded) Interfaces.Add(item);
        SelectedInterface = Interfaces.FirstOrDefault(i => i.Id == previous) ?? Interfaces.FirstOrDefault(i => i.HasUsableIpv4);
        ApplySettings();
        if (string.IsNullOrWhiteSpace(Targets) && SelectedInterface?.SuggestedCidr is { } cidr) Targets = cidr;
        await LoadHistoryAsync();
    }, errorTitle: "Unable to load discovery");

    private void ApplySettings()
    {
        var s = _settings.Current;
        EnableArp = s.EnableArp;
        EnableIcmp = s.EnableIcmp;
        EnableTcp = s.EnableTcp;
        EnableDns = s.EnableReverseDns;
        EnableMdns = s.EnableMdns;
        EnableNetBios = s.EnableNetBios;
    }

    private async Task LoadHistoryAsync()
    {
        var sessions = await _discovery.GetHistoryAsync(20);
        History.Clear();
        foreach (var session in sessions) History.Add(session);
    }

    partial void OnSelectedInterfaceChanged(NetworkInterfaceInfo? value)
    {
        if (value?.SuggestedCidr is { } cidr && string.IsNullOrWhiteSpace(Targets)) Targets = cidr;
    }

    [RelayCommand]
    private async Task ReloadInterfacesAsync() => await LoadAsync();

    [RelayCommand]
    private void UseInterface()
    {
        if (SelectedInterface?.SuggestedCidr is { } cidr)
        {
            Targets = cidr;
            AllowExternal = false;
        }
    }

    [RelayCommand]
    private async Task ScanAsync() => await RunGuardedAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Targets))
            throw new ArgumentException("Enter at least one IP address, range or CIDR.");

        var tokens = Targets.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) throw new ArgumentException("Enter at least one IP address, range or CIDR.");

        if (AllowExternal && _settings.Current.RequireConfirmationForExternalRanges &&
            !Ui.Confirm("The target list may contain addresses outside your private LAN. Scanning public or " +
                        "third-party addresses can be intrusive. Continue?", "Confirm external scan"))
            return;

        await SyncOptionsAsync();

        Results.Clear();
        Summary = "";
        Warnings = "";
        ProgressPercent = 0;
        FoundCount = 0;
        CheckedCount = 0;
        TotalCount = 0;
        ProgressText = "Starting…";
        IsScanning = true;
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressText = p.Message ?? $"{p.AddressesChecked:N0} / {p.AddressesTotal:N0} addresses checked";
                FoundCount = p.DevicesFound;
                CheckedCount = p.AddressesChecked;
                TotalCount = p.AddressesTotal;
                CurrentTarget = p.CurrentTarget;
            });

            var outcome = await _discovery.RunAsync(tokens, AllowExternal, progress, _cts.Token);
            foreach (var host in outcome.Scan.Hosts.OrderBy(h => Ipv4Address.TryParse(h.IpAddress, out var a) ? a.Value : uint.MaxValue))
                Results.Add(host);

            Summary = outcome.Summary;
            Warnings = string.Join(Environment.NewLine, outcome.Warnings);
            FoundCount = outcome.Scan.Hosts.Count;
            CheckedCount = outcome.Scan.AddressesChecked;
            TotalCount = outcome.Scan.AddressesChecked;
            ProgressPercent = 100;
            ProgressText = outcome.Scan.Cancelled
                ? $"Cancelled after {outcome.Scan.AddressesChecked:N0} addresses"
                : $"Completed in {outcome.Scan.Duration.TotalSeconds:0.0}s";
            await LoadHistoryAsync();
            Ui.SetStatus(Summary);
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }, errorTitle: "Discovery failed");

    private async Task SyncOptionsAsync()
    {
        var s = _settings.Current;
        s.EnableArp = EnableArp;
        s.EnableIcmp = EnableIcmp;
        s.EnableTcp = EnableTcp;
        s.EnableReverseDns = EnableDns;
        s.EnableMdns = EnableMdns;
        s.EnableNetBios = EnableNetBios;
        await _settings.SaveAsync();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task PurgeHistoryAsync() => await RunGuardedAsync(async () =>
    {
        if (!Ui.Confirm("Remove scan sessions older than the retention window? Inventory and devices are not deleted.",
                "Purge scan history")) return;
        await _discovery.PurgeStaleSessionsAsync();
        await LoadHistoryAsync();
    }, "Scan history purged.");

    [RelayCommand]
    private void CopyResults()
    {
        if (Results.Count == 0) return;
        var text = string.Join(Environment.NewLine,
            Results.Select(h => $"{h.IpAddress}\t{h.MacAddress}\t{h.BestName}\t{h.Vendor}\t{h.SourceText}"));
        try
        {
            System.Windows.Clipboard.SetText(text);
            Ui.SetStatus($"{Results.Count} discovery result(s) copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message, "Clipboard unavailable");
        }
    }
}
