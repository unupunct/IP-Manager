using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Network;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class DnsProviderOption
{
    public required DnsProviderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public bool CanWrite { get; init; }
    public string AccessText => CanWrite ? "Read + write" : "Read only";
}

public sealed partial class DnsViewModel : ViewModelBase, IActivatable
{
    private readonly DnsManagerService _dns;
    private int _recordId;

    public DnsViewModel(DnsManagerService dns, IUiService ui) : base(ui)
    {
        _dns = dns;
    }

    public ObservableCollection<string> Servers { get; } = new();
    public ObservableCollection<DnsAnswer> Answers { get; } = new();
    public ObservableCollection<DnsQueryLog> History { get; } = new();
    public ObservableCollection<DnsRecordDoc> LocalRecords { get; } = new();
    public ObservableCollection<IntegrationConfig> Integrations { get; } = new();
    public ObservableCollection<DnsProviderOption> ProviderKinds { get; } = new();
    public ObservableCollection<RemoteDnsRecord> RemoteRecords { get; } = new();
    public IReadOnlyList<string> RecordTypes => DnsManagerService.RecordTypes;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _queryName = "";
    [ObservableProperty] private string _selectedRecordType = "A";
    [ObservableProperty] private string _selectedServer = "System default";
    [ObservableProperty] private bool _isQuerying;
    [ObservableProperty] private bool _querySucceeded;
    [ObservableProperty] private string _querySummary = "Enter a hostname and run a lookup.";
    [ObservableProperty] private string _queryDetails = "";
    [ObservableProperty] private string _queryError = "";

    [ObservableProperty] private DnsRecordDoc? _selectedRecord;
    [ObservableProperty] private DnsRecordDoc? _editorRecord;
    [ObservableProperty] private bool _isRecordEditorOpen;
    [ObservableProperty] private string _recordTitle = "DNS record";
    [ObservableProperty] private string _recordFilter = "";
    [ObservableProperty] private int _recordCount;

    [ObservableProperty] private IntegrationConfig? _selectedIntegration;
    [ObservableProperty] private DnsProviderOption? _providerKind;
    [ObservableProperty] private string _integrationName = "";
    [ObservableProperty] private string _integrationEndpoint = "";
    [ObservableProperty] private string _integrationUsername = "";
    [ObservableProperty] private string _integrationSecret = "";
    [ObservableProperty] private string _integrationZone = "";
    [ObservableProperty] private bool _integrationEnabled = true;
    [ObservableProperty] private string _integrationMessage = "Select an integration to test it, browse records or edit it.";
    [ObservableProperty] private bool _canWriteSelected;
    [ObservableProperty] private RemoteDnsRecord? _selectedRemoteRecord;
    [ObservableProperty] private string _newRemoteName = "";
    [ObservableProperty] private string _newRemoteType = "A";
    [ObservableProperty] private string _newRemoteValue = "";
    [ObservableProperty] private string _newRemoteTtl = "300";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load DNS data");

    private async Task LoadCoreAsync()
    {
        if (ProviderKinds.Count == 0)
        {
            foreach (var provider in _dns.Providers)
            {
                ProviderKinds.Add(new DnsProviderOption
                {
                    Kind = provider.Kind,
                    DisplayName = provider.DisplayName,
                    CanWrite = provider.CanWrite
                });
            }
            ProviderKind = ProviderKinds.FirstOrDefault();
        }

        Servers.Clear();
        Servers.Add("System default");
        foreach (var server in _dns.GetSystemDnsServers().Distinct())
            Servers.Add(server);
        if (!Servers.Contains(SelectedServer)) SelectedServer = "System default";

        await LoadRecordsCoreAsync();
        await LoadIntegrationsCoreAsync();

        var history = await _dns.GetHistoryAsync(200);
        History.Clear();
        foreach (var item in history) History.Add(item);
    }

    private async Task LoadRecordsCoreAsync()
    {
        var records = await _dns.GetLocalRecordsAsync();
        LocalRecords.Clear();
        var filter = RecordFilter.Trim();
        foreach (var record in records)
        {
            if (filter.Length > 0 &&
                !record.Zone.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !record.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !record.Value.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !record.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            LocalRecords.Add(record);
        }
        RecordCount = LocalRecords.Count;
    }

    private async Task LoadIntegrationsCoreAsync()
    {
        var selectedId = SelectedIntegration?.Id ?? 0;
        var integrations = await _dns.GetIntegrationsAsync();
        Integrations.Clear();
        foreach (var integration in integrations) Integrations.Add(integration);
        SelectedIntegration = Integrations.FirstOrDefault(i => i.Id == selectedId) ?? Integrations.FirstOrDefault();
    }

    partial void OnRecordFilterChanged(string value) => _ = LoadRecordsFilteredAsync();

    private async Task LoadRecordsFilteredAsync()
    {
        try { await LoadRecordsCoreAsync(); }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    partial void OnSelectedIntegrationChanged(IntegrationConfig? value)
    {
        RemoteRecords.Clear();
        CanWriteSelected = false;
        if (value is null)
        {
            IntegrationMessage = "No integration selected.";
            return;
        }
        IntegrationName = value.Name;
        IntegrationEndpoint = value.Endpoint ?? "";
        IntegrationUsername = value.Username ?? "";
        IntegrationSecret = "";
        IntegrationZone = ExtractZone(value.OptionsJson);
        IntegrationEnabled = value.Enabled;
        var provider = _dns.Providers.FirstOrDefault(p => p.Kind.ToString() == value.Kind);
        CanWriteSelected = provider?.CanWrite == true;
        ProviderKind = ProviderKinds.FirstOrDefault(p => p.Kind.ToString() == value.Kind) ?? ProviderKind;
        IntegrationMessage = $"{provider?.DisplayName ?? value.Kind} · {value.State}" +
                             (string.IsNullOrWhiteSpace(value.LastError) ? "" : $" · {value.LastError}");
    }

    private static string ExtractZone(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("zone", out var zone) ? zone.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    partial void OnSelectedRecordChanged(DnsRecordDoc? value)
    {
        IsRecordEditorOpen = value is not null;
        if (value is null)
        {
            EditorRecord = null;
            return;
        }
        _recordId = value.Id;
        RecordTitle = value.Id == 0 ? "New DNS record" : $"{value.Name} {value.Type}";
        EditorRecord = new DnsRecordDoc
        {
            Id = value.Id,
            Zone = value.Zone,
            Name = value.Name,
            Type = value.Type,
            Value = value.Value,
            Ttl = value.Ttl,
            Server = value.Server,
            Notes = value.Notes,
            CreatedAt = value.CreatedAt,
            UpdatedAt = value.UpdatedAt
        };
    }

    [RelayCommand]
    private async Task LookupAsync() => await RunGuardedAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(QueryName))
        {
            Ui.Info("Enter a hostname or IP address first.");
            return;
        }
        IsQuerying = true;
        Answers.Clear();
        QueryError = "";
        try
        {
            var server = SelectedServer == "System default" ? null : SelectedServer;
            var result = await _dns.LookupAsync(QueryName.Trim(), SelectedRecordType, server);
            QuerySucceeded = result.Success;
            QuerySummary = $"{result.Server} · {result.RecordType} · {result.DurationMs:0} ms · response code {result.ResponseCode}";
            QueryDetails = result.ResponseText;
            QueryError = result.Error ?? "";
            foreach (var answer in result.Answers) Answers.Add(answer);
            var history = await _dns.GetHistoryAsync(200);
            History.Clear();
            foreach (var item in history) History.Add(item);
        }
        finally
        {
            IsQuerying = false;
        }
    }, errorTitle: "DNS lookup failed");

    [RelayCommand]
    private async Task ReverseAsync() => await RunGuardedAsync(async () =>
    {
        if (!System.Net.IPAddress.TryParse(QueryName.Trim(), out _))
        {
            Ui.Info("Enter an IPv4 or IPv6 address to reverse.");
            return;
        }
        IsQuerying = true;
        Answers.Clear();
        try
        {
            var server = SelectedServer == "System default" ? null : SelectedServer;
            var result = await _dns.ReverseAsync(QueryName.Trim(), server);
            QuerySucceeded = result.Success;
            QuerySummary = $"Reverse {result.QueryName} · {result.Server} · {result.DurationMs:0} ms";
            QueryDetails = result.ResponseText;
            QueryError = result.Error ?? "";
            foreach (var answer in result.Answers) Answers.Add(answer);
        }
        finally
        {
            IsQuerying = false;
        }
    }, errorTitle: "Reverse lookup failed");

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void NewRecord()
    {
        SelectedRecord = new DnsRecordDoc
        {
            Type = "A",
            Ttl = 3600,
            Zone = "",
            Name = "",
            Value = ""
        };
    }

    [RelayCommand]
    private async Task SaveRecordAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorRecord is null) return;
        var saved = await _dns.SaveLocalRecordAsync(EditorRecord);
        _recordId = saved.Id;
        await LoadRecordsCoreAsync();
        SelectedRecord = LocalRecords.FirstOrDefault(r => r.Id == _recordId);
        Ui.SetStatus($"Saved DNS record {saved.Name}.");
    }, errorTitle: "Unable to save the DNS record");

    [RelayCommand]
    private async Task DeleteRecordAsync() => await RunGuardedAsync(async () =>
    {
        if (EditorRecord is null || EditorRecord.Id == 0) return;
        if (!Ui.Confirm($"Delete the documented record '{EditorRecord.Name} {EditorRecord.Type}'?", "Delete DNS record")) return;
        await _dns.DeleteLocalRecordAsync(EditorRecord.Id);
        SelectedRecord = null;
        EditorRecord = null;
        IsRecordEditorOpen = false;
        await LoadRecordsCoreAsync();
    }, "DNS record deleted.", "Unable to delete the DNS record");

    [RelayCommand]
    private void CancelRecordEdit()
    {
        IsRecordEditorOpen = false;
        EditorRecord = null;
        SelectedRecord = null;
    }

    [RelayCommand]
    private async Task SaveIntegrationAsync() => await RunGuardedAsync(async () =>
    {
        if (ProviderKind is null) return;
        var name = IntegrationName.Trim();
        if (name.Length == 0) name = ProviderKind.DisplayName;
        var saved = await _dns.SaveIntegrationAsync(ProviderKind.Kind, name, IntegrationEndpoint.Trim(),
            IntegrationUsername.Trim(), IntegrationSecret, IntegrationZone.Trim(), IntegrationEnabled);
        IntegrationSecret = "";
        await LoadIntegrationsCoreAsync();
        SelectedIntegration = Integrations.FirstOrDefault(i => i.Id == saved.Id);
        IntegrationMessage = $"Saved {saved.Name}. The secret is DPAPI-protected.";
    }, "DNS integration saved.", "Unable to save the DNS integration");

    [RelayCommand]
    private async Task TestIntegrationAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedIntegration is null)
        {
            Ui.Info("Select an integration first.");
            return;
        }
        var result = await _dns.TestProviderAsync(SelectedIntegration.Id);
        IntegrationMessage = result.Message;
        var id = SelectedIntegration.Id;
        await LoadIntegrationsCoreAsync();
        SelectedIntegration = Integrations.FirstOrDefault(i => i.Id == id);
        if (result.Success)
        {
            var records = await _dns.GetRemoteRecordsAsync(id, IntegrationZone.Trim());
            RemoteRecords.Clear();
            foreach (var record in records.Records) RemoteRecords.Add(record);
            IntegrationMessage = $"{result.Message} · {records.Records.Count} record(s)";
        }
        else
        {
            Ui.Error(result.Message, "DNS provider test failed");
        }
    }, errorTitle: "DNS provider test failed");

    [RelayCommand]
    private async Task LoadRemoteRecordsAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedIntegration is null) return;
        var result = await _dns.GetRemoteRecordsAsync(SelectedIntegration.Id, IntegrationZone.Trim());
        RemoteRecords.Clear();
        foreach (var record in result.Records) RemoteRecords.Add(record);
        IntegrationMessage = result.Message;
        if (!result.Success) Ui.Error(result.Message, "Unable to read DNS records");
    }, errorTitle: "Unable to read remote DNS records");

    [RelayCommand]
    private async Task CreateRemoteRecordAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedIntegration is null) return;
        if (!CanWriteSelected)
        {
            Ui.Info("This DNS provider is read-only in IP Manager.");
            return;
        }
        if (string.IsNullOrWhiteSpace(NewRemoteName) || string.IsNullOrWhiteSpace(NewRemoteValue))
        {
            Ui.Info("Enter a record name and value.");
            return;
        }
        if (!int.TryParse(NewRemoteTtl, out var ttl)) ttl = 300;
        var record = new RemoteDnsRecord("", IntegrationZone.Trim(), NewRemoteName.Trim(), NewRemoteType, NewRemoteValue.Trim(), ttl);
        if (!Ui.Confirm($"Create {record.Type} {record.Name} -> {record.Value} in {SelectedIntegration.Name}?\n\n" +
                        "This changes the remote DNS server.", "Create remote DNS record")) return;
        await _dns.CreateRemoteRecordAsync(SelectedIntegration.Id, record);
        NewRemoteName = "";
        NewRemoteValue = "";
        var result = await _dns.GetRemoteRecordsAsync(SelectedIntegration.Id, IntegrationZone.Trim());
        RemoteRecords.Clear();
        foreach (var item in result.Records) RemoteRecords.Add(item);
        IntegrationMessage = $"Record created. {result.Message}";
    }, "Remote DNS record created.", "Unable to create the remote record");

    [RelayCommand]
    private async Task DeleteRemoteRecordAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedIntegration is null || SelectedRemoteRecord is null) return;
        if (!CanWriteSelected)
        {
            Ui.Info("This DNS provider is read-only in IP Manager.");
            return;
        }
        if (!Ui.Confirm($"Delete {SelectedRemoteRecord.Type} {SelectedRemoteRecord.Name} -> {SelectedRemoteRecord.Value} " +
                        $"from {SelectedIntegration.Name}?", "Delete remote DNS record")) return;
        await _dns.DeleteRemoteRecordAsync(SelectedIntegration.Id, SelectedRemoteRecord);
        var id = SelectedIntegration.Id;
        var result = await _dns.GetRemoteRecordsAsync(id, IntegrationZone.Trim());
        RemoteRecords.Clear();
        foreach (var item in result.Records) RemoteRecords.Add(item);
        IntegrationMessage = $"Record deleted. {result.Message}";
    }, "Remote DNS record deleted.", "Unable to delete the remote record");

    [RelayCommand]
    private async Task DeleteIntegrationAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedIntegration is null) return;
        if (!Ui.Confirm($"Remove the integration configuration '{SelectedIntegration.Name}'?\n\nNo remote records are changed.",
                "Remove DNS integration")) return;
        await _dns.DeleteIntegrationAsync(SelectedIntegration.Id);
        SelectedIntegration = null;
        RemoteRecords.Clear();
        await LoadIntegrationsCoreAsync();
        IntegrationMessage = "Integration removed.";
    }, "DNS integration removed.", "Unable to remove the integration");

    [RelayCommand]
    private void NewIntegration()
    {
        SelectedIntegration = null;
        RemoteRecords.Clear();
        IntegrationName = "";
        IntegrationEndpoint = "";
        IntegrationUsername = "";
        IntegrationSecret = "";
        IntegrationZone = "";
        IntegrationEnabled = true;
        IntegrationMessage = "New integration. Pick a provider, enter the endpoint and credentials, then save.";
        ProviderKind = ProviderKinds.FirstOrDefault();
    }
}
