using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class ReportKindOption
{
    public required string Name { get; init; }
    public required ReportKind Value { get; init; }
}

public sealed class ReportSubnetOption
{
    public required string Name { get; init; }
    public int? Value { get; init; }
}

public sealed class ReportCategoryOption
{
    public required string Name { get; init; }
    public DeviceCategory? Value { get; init; }
}

public sealed record ReportRowView(int Index, IReadOnlyList<string> Cells);

public sealed partial class ReportsViewModel : ViewModelBase, IActivatable
{
    private readonly ReportsService _reports;
    private readonly InventoryService _inventory;
    private readonly AppPaths _paths;
    private ReportTable? _current;

    public ReportsViewModel(ReportsService reports, InventoryService inventory, AppPaths paths, IUiService ui) : base(ui)
    {
        _reports = reports;
        _inventory = inventory;
        _paths = paths;

        Kinds = new ObservableCollection<ReportKindOption>
        {
            new() { Name = "Network inventory", Value = ReportKind.NetworkInventory },
            new() { Name = "Subnet utilization", Value = ReportKind.SubnetUtilization },
            new() { Name = "DHCP documentation", Value = ReportKind.Dhcp },
            new() { Name = "IP conflicts", Value = ReportKind.IpConflicts },
            new() { Name = "DNS configuration", Value = ReportKind.Dns },
            new() { Name = "Network availability", Value = ReportKind.Availability }
        };
        SelectedKind = Kinds[0];

        SubnetOptions = new ObservableCollection<ReportSubnetOption> { new() { Name = "All subnets" } };
        SelectedSubnet = SubnetOptions[0];

        CategoryOptions = new ObservableCollection<ReportCategoryOption> { new() { Name = "All categories" } };
        foreach (var category in Enum.GetValues<DeviceCategory>())
            CategoryOptions.Add(new ReportCategoryOption { Name = DeviceClassifier.DisplayName(category), Value = category });
        SelectedCategory = CategoryOptions[0];
    }

    public ObservableCollection<ReportKindOption> Kinds { get; }
    public ObservableCollection<ReportSubnetOption> SubnetOptions { get; }
    public ObservableCollection<ReportCategoryOption> CategoryOptions { get; }
    public ObservableCollection<string> PreviewHeaders { get; } = new();
    public ObservableCollection<ReportRowView> PreviewRows { get; } = new();
    public ObservableCollection<string> Metadata { get; } = new();

    [ObservableProperty] private ReportKindOption? _selectedKind;
    [ObservableProperty] private ReportSubnetOption? _selectedSubnet;
    [ObservableProperty] private ReportCategoryOption? _selectedCategory;
    [ObservableProperty] private string _fromDate = "";
    [ObservableProperty] private string _toDate = "";
    [ObservableProperty] private string _previewTitle = "Report preview";
    [ObservableProperty] private string _previewSubtitle = "Build a report to preview it before exporting.";
    [ObservableProperty] private int _rowCount;
    [ObservableProperty] private int _columnCount;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _exportSummary = "";
    [ObservableProperty] private string _lastExportPath = "";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await RunGuardedAsync(async () =>
    {
        var subnets = await _inventory.GetSubnetsAsync();
        var keep = SelectedSubnet?.Value;
        SubnetOptions.Clear();
        SubnetOptions.Add(new ReportSubnetOption { Name = "All subnets" });
        foreach (var subnet in subnets)
            SubnetOptions.Add(new ReportSubnetOption { Name = $"{subnet.Name} · {subnet.Cidr}", Value = subnet.Id });
        SelectedSubnet = SubnetOptions.FirstOrDefault(s => s.Value == keep) ?? SubnetOptions[0];
        await BuildCoreAsync();
    }, errorTitle: "Unable to load reports");

    partial void OnSelectedKindChanged(ReportKindOption? value)
    {
        if (value is not null) _ = BuildSafeAsync();
    }

    partial void OnSelectedSubnetChanged(ReportSubnetOption? value)
    {
        if (value is not null) _ = BuildSafeAsync();
    }

    partial void OnSelectedCategoryChanged(ReportCategoryOption? value)
    {
        if (value is not null) _ = BuildSafeAsync();
    }

    private async Task BuildSafeAsync()
    {
        try { await BuildCoreAsync(); }
        catch (Exception ex) { Ui.Error(ex.Message, "Unable to build the report"); }
    }

    private ReportFilter CurrentFilter()
    {
        var filter = new ReportFilter
        {
            SubnetId = SelectedSubnet?.Value,
            Category = SelectedCategory?.Value
        };
        if (DateTime.TryParse(FromDate, CultureInfo.CurrentCulture, DateTimeStyles.None, out var from))
            filter.From = DateTime.SpecifyKind(from, DateTimeKind.Local).ToUniversalTime();
        if (DateTime.TryParse(ToDate, CultureInfo.CurrentCulture, DateTimeStyles.None, out var to))
            filter.To = DateTime.SpecifyKind(to.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime();
        return filter;
    }

    private async Task BuildCoreAsync()
    {
        if (SelectedKind is null) return;
        var table = await _reports.BuildAsync(SelectedKind.Value, CurrentFilter());
        _current = table;
        Show(table);
    }

    private void Show(ReportTable table)
    {
        PreviewTitle = table.Title;
        PreviewSubtitle = table.Subtitle;
        PreviewHeaders.Clear();
        foreach (var header in table.Headers) PreviewHeaders.Add(header);
        PreviewRows.Clear();
        for (var i = 0; i < table.Rows.Count; i++) PreviewRows.Add(new ReportRowView(i + 1, table.Rows[i]));
        Metadata.Clear();
        foreach (var pair in table.Metadata) Metadata.Add($"{pair.Key}: {pair.Value}");
        RowCount = table.Rows.Count;
        ColumnCount = table.Headers.Count;
        HasPreview = true;
        ExportSummary = $"{table.Headers.Count} columns · {table.Rows.Count:N0} row(s)";
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task BuildAsync() => await RunGuardedAsync(BuildCoreAsync, errorTitle: "Unable to build the report");

    [RelayCommand]
    private async Task ExportCsvAsync() => await ExportAsync(ExportFormat.Csv);

    [RelayCommand]
    private async Task ExportJsonAsync() => await ExportAsync(ExportFormat.Json);

    [RelayCommand]
    private async Task ExportPdfAsync() => await ExportAsync(ExportFormat.Pdf);

    private async Task ExportAsync(ExportFormat format)
    {
        await RunGuardedAsync(async () =>
        {
            if (_current is null) await BuildCoreAsync();
            if (_current is null) return;
            var extension = format switch
            {
                ExportFormat.Csv => "csv",
                ExportFormat.Json => "json",
                _ => "pdf"
            };
            var filter = $"{extension.ToUpperInvariant()} file (*.{extension})|*.{extension}";
            var safe = new string(_current.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray())
                .Replace(' ', '-');
            var path = Ui.SaveFile(filter, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}", _paths.ExportDirectory);
            if (string.IsNullOrWhiteSpace(path)) return;
            var written = await _reports.ExportAsync(_current, format, path);
            LastExportPath = written;
            ExportSummary = $"Exported {_current.Rows.Count:N0} row(s) to {written}";
            Ui.SetStatus($"Report exported to {written}");
        }, errorTitle: "Unable to export the report");
    }

    [RelayCommand]
    private async Task ExportAllAsync() => await RunGuardedAsync(async () =>
    {
        var folder = Ui.PickFolder(_paths.ExportDirectory);
        if (string.IsNullOrWhiteSpace(folder)) return;
        var filter = CurrentFilter();
        var tables = await _reports.BuildAllAsync(filter);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var table in tables)
        {
            var safe = new string(table.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray())
                .Replace(' ', '-');
            var path = Path.Combine(folder, $"{safe}-{stamp}.csv");
            await _reports.ExportAsync(table, ExportFormat.Csv, path);
        }
        LastExportPath = folder;
        ExportSummary = $"Exported {tables.Count} CSV report(s) to {folder}";
    }, "All reports exported.", "Unable to export all reports");

    [RelayCommand]
    private void OpenExports() => Ui.OpenInExplorer(Directory.Exists(_paths.ExportDirectory)
        ? _paths.ExportDirectory
        : _paths.DataDirectory);

    [RelayCommand]
    private void OpenLastExport()
    {
        if (string.IsNullOrWhiteSpace(LastExportPath)) return;
        Ui.OpenInExplorer(LastExportPath);
    }

    [RelayCommand]
    private void ClearDates()
    {
        FromDate = "";
        ToDate = "";
        _ = BuildSafeAsync();
    }
}
