using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Network;

namespace IPManager.App.ViewModels;

public sealed record AdapterRow(NetworkInterfaceInfo Info)
{
    public string Name => Info.Name;
    public string Description => Info.Description ?? "";
    public string Address => Info.Ipv4Address ?? "no IPv4";
    public string Mask => Info.SubnetMask ?? "";
    public string Cidr => Info.SuggestedCidr ?? "";
    public string Gateway => Info.Gateway ?? "";
    public string Dns => Info.DnsServers.Count == 0 ? "none" : string.Join(", ", Info.DnsServers);
    public string Mac => Info.MacAddress ?? "";
    public string Speed => Info.SpeedMbps is null ? "" : $"{Info.SpeedMbps:N0} Mbps";
    public string State => Info.IsUp ? "Up" : "Down";
    public string Kind => Info.Kind.ToString();
    public bool IsUp => Info.IsUp;
}

public sealed partial class DiagnosticsViewModel : ViewModelBase, IActivatable
{
    private CancellationTokenSource? _cts;

    public DiagnosticsViewModel(IUiService ui) : base(ui) { }

    public ObservableCollection<AdapterRow> Adapters { get; } = new();
    public ObservableCollection<PingResult> PingResults { get; } = new();
    public ObservableCollection<TracerouteHop> TracerouteHops { get; } = new();
    public ObservableCollection<ArpEntry> ArpEntries { get; } = new();

    [ObservableProperty] private string _target = "8.8.8.8";
    [ObservableProperty] private string _pingCount = "4";
    [ObservableProperty] private string _pingInterval = "500";
    [ObservableProperty] private string _timeout = "1500";
    [ObservableProperty] private string _maxHops = "20";
    [ObservableProperty] private string _port = "443";
    [ObservableProperty] private string _pingSummary = "Run a ping series to measure reachability and latency.";
    [ObservableProperty] private string _tracerouteSummary = "Traceroute uses TTL-limited echo requests.";
    [ObservableProperty] private string _portSummary = "Probe one TCP port to test service reachability.";
    [ObservableProperty] private string _arpSummary = "Read the Windows neighbor cache.";
    [ObservableProperty] private string _adapterSummary = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _activeOperation = "";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() => Cancel();

    public override async Task LoadAsync() => await RunGuardedAsync(LoadAdaptersCoreAsync, errorTitle: "Unable to read network adapters");

    private async Task LoadAdaptersCoreAsync()
    {
        var adapters = await Task.Run(NetworkInterfaceService.GetActive);
        Adapters.Clear();
        foreach (var info in adapters) Adapters.Add(new AdapterRow(info));
        var usable = adapters.Count(a => a.IsUp && a.HasUsableIpv4);
        AdapterSummary = $"{adapters.Count} adapter(s) · {usable} with a usable IPv4 configuration · " +
                         $"DNS: {(NetworkInterfaceService.GetSystemDnsServers().Count == 0 ? "none" : string.Join(", ", NetworkInterfaceService.GetSystemDnsServers()))}";
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private bool Begin(string operation)
    {
        if (IsRunning) return false;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ActiveOperation = operation;
        return true;
    }

    private void End()
    {
        IsRunning = false;
        ActiveOperation = "";
        _cts?.Dispose();
        _cts = null;
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
    }

    [RelayCommand]
    private void Stop() => Cancel();

    [RelayCommand]
    private async Task PingAsync() => await RunGuardedAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            Ui.Info("Enter a host name or IP address.");
            return;
        }
        if (!Begin("Ping")) return;
        try
        {
            PingResults.Clear();
            var count = ParseInt(PingCount, 4, 1, 100);
            var interval = ParseInt(PingInterval, 500, 0, 10000);
            var timeout = ParseInt(Timeout, 1500, 100, 20000);
            var progress = new Progress<PingResult>(result => Ui.RunOnUi(() => PingResults.Add(result)));
            var results = await DiagnosticsService.PingSeriesAsync(Target.Trim(), count, interval, timeout, progress, _cts!.Token);
            var success = results.Count(r => r.Success);
            var average = results.Where(r => r.RoundtripMs is not null).Select(r => r.RoundtripMs!.Value).DefaultIfEmpty(0).Average();
            PingSummary = $"{success}/{results.Count} replies · average {average:0} ms · " +
                          (success == 0 ? "no reply (host may be offline, filtered, or ICMP blocked)" : "destination responded");
        }
        finally
        {
            End();
        }
    }, errorTitle: "Ping failed");

    [RelayCommand]
    private async Task TracerouteAsync() => await RunGuardedAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            Ui.Info("Enter a host name or IP address.");
            return;
        }
        if (!Begin("Traceroute")) return;
        try
        {
            TracerouteHops.Clear();
            var hops = ParseInt(MaxHops, 20, 1, 64);
            var timeout = ParseInt(Timeout, 1500, 100, 20000);
            var progress = new Progress<TracerouteHop>(hop => Ui.RunOnUi(() => TracerouteHops.Add(hop)));
            var results = await DiagnosticsService.TracerouteAsync(Target.Trim(), hops, timeout, progress, _cts!.Token);
            var reached = results.LastOrDefault(r => r.Status == "Destination reached");
            TracerouteSummary = reached is not null
                ? $"Destination reached after {results.Count} hop(s) at {reached.RoundtripMs} ms."
                : $"{results.Count} hop(s) probed; destination was not confirmed (filtered routers are shown as timeouts).";
        }
        finally
        {
            End();
        }
    }, errorTitle: "Traceroute failed");

    [RelayCommand]
    private async Task PortCheckAsync() => await RunGuardedAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            Ui.Info("Enter a host name or IP address.");
            return;
        }
        var port = ParseInt(Port, 443, 1, 65535);
        if (!Begin("Port probe")) return;
        try
        {
            var timeout = ParseInt(Timeout, 1500, 100, 20000);
            var result = await DiagnosticsService.PortCheckAsync(Target.Trim(), port, timeout, _cts!.Token);
            PortSummary = $"TCP {Target.Trim()}:{port} · {(result.Open ? "open" : "closed / filtered")} · {result.Status} · " +
                          (result.ConnectMs is null ? "no timing" : $"{result.ConnectMs} ms");
        }
        finally
        {
            End();
        }
    }, errorTitle: "Port probe failed");

    [RelayCommand]
    private async Task ReadArpAsync() => await RunGuardedAsync(async () =>
    {
        if (!Begin("ARP cache")) return;
        try
        {
            var preferred = Adapters.FirstOrDefault(a => a.IsUp && a.Info.HasUsableIpv4)?.Info.Ipv4Address;
            var entries = await ArpTableReader.ReadAsync(preferred, 4000, _cts!.Token);
            ArpEntries.Clear();
            foreach (var entry in entries.OrderBy(e => e.IpAddress, StringComparer.OrdinalIgnoreCase)) ArpEntries.Add(entry);
            ArpSummary = $"{entries.Count} neighbor entr(y/ies) read" +
                         (string.IsNullOrWhiteSpace(preferred) ? "." : $" for interface {preferred}.");
        }
        finally
        {
            End();
        }
    }, errorTitle: "Unable to read the ARP cache");

    [RelayCommand]
    private async Task RefreshAdaptersAsync() => await LoadAsync();

    [RelayCommand]
    private void UseGateway()
    {
        var gateway = Adapters.FirstOrDefault(a => a.IsUp && !string.IsNullOrWhiteSpace(a.Gateway))?.Gateway;
        if (string.IsNullOrWhiteSpace(gateway))
        {
            Ui.Info("No default gateway was detected on the active adapters.");
            return;
        }
        Target = gateway;
    }

    [RelayCommand]
    private void UseAdapterAddress()
    {
        var address = Adapters.FirstOrDefault(a => a.IsUp && a.Info.HasUsableIpv4)?.Address;
        if (string.IsNullOrWhiteSpace(address))
        {
            Ui.Info("No usable IPv4 adapter address was detected.");
            return;
        }
        Target = address;
    }

    [RelayCommand]
    private void UseDnsServer()
    {
        var server = NetworkInterfaceService.GetSystemDnsServers().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(server))
        {
            Ui.Info("No system DNS server was detected.");
            return;
        }
        Target = server;
    }
}
