using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class ConflictStateOption
{
    public required string Name { get; init; }
    public ConflictState? Value { get; init; }
}

public sealed partial class ConflictsViewModel : ViewModelBase, IActivatable
{
    private readonly ConflictService _conflicts;
    private readonly IUiNavigator _navigator;

    public ConflictsViewModel(ConflictService conflicts, IUiNavigator navigator, IUiService ui) : base(ui)
    {
        _conflicts = conflicts;
        _navigator = navigator;
        StateOptions = new ObservableCollection<ConflictStateOption>
        {
            new() { Name = "Active conflicts" },
            new() { Name = "Confirmed", Value = ConflictState.Confirmed },
            new() { Name = "Suspected", Value = ConflictState.Suspected },
            new() { Name = "Resolved", Value = ConflictState.Resolved }
        };
        SelectedState = StateOptions[0];
    }

    public ObservableCollection<IpConflict> Conflicts { get; } = new();
    public ObservableCollection<IpConflict> VisibleConflicts { get; } = new();
    public ObservableCollection<MacObservation> Observations { get; } = new();
    public ObservableCollection<ConflictStateOption> StateOptions { get; }

    [ObservableProperty] private IpConflict? _selectedConflict;
    [ObservableProperty] private ConflictStateOption? _selectedState;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _confirmedCount;
    [ObservableProperty] private int _suspectedCount;
    [ObservableProperty] private int _resolvedCount;
    [ObservableProperty] private int _databaseDuplicateCount;
    [ObservableProperty] private string _lastRunText = "Analysis has not run in this session.";
    [ObservableProperty] private string _selectionSummary = "Select a conflict to inspect the evidence.";
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string _resolutionNote = "";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await RunGuardedAsync(LoadCoreAsync, errorTitle: "Unable to load conflicts");

    private async Task LoadCoreAsync()
    {
        var all = await _conflicts.GetConflictsAsync(includeResolved: true);
        Conflicts.Clear();
        foreach (var conflict in all) Conflicts.Add(conflict);

        ConfirmedCount = all.Count(c => c.State == ConflictState.Confirmed);
        SuspectedCount = all.Count(c => c.State == ConflictState.Suspected);
        ResolvedCount = all.Count(c => c.State == ConflictState.Resolved);
        DatabaseDuplicateCount = all.Count(c => c.Method == ConflictMethod.DuplicateDatabaseAssignment);
        LastRunText = all.Count == 0
            ? "No conflict analysis results are stored yet."
            : $"Latest evidence recorded {all.Max(c => c.LastSeenAt).ToLocalTime():yyyy-MM-dd HH:mm}";

        ApplyFilter();
        if (SelectedConflict is { } selected)
            SelectedConflict = VisibleConflicts.FirstOrDefault(c => c.Id == selected.Id);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedStateChanged(ConflictStateOption? value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = Conflicts.AsEnumerable();
        if (SelectedState?.Value is { } state) query = query.Where(c => c.State == state);
        else query = query.Where(c => c.State != ConflictState.Resolved);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(c => c.IpAddress.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     c.MacAddresses.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     c.Evidence.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        VisibleConflicts.Clear();
        foreach (var conflict in query.OrderByDescending(c => c.State).ThenByDescending(c => c.LastSeenAt))
            VisibleConflicts.Add(conflict);
    }

    partial void OnSelectedConflictChanged(IpConflict? value)
    {
        Observations.Clear();
        ResolutionNote = value?.Notes ?? "";
        if (value is null)
        {
            SelectionSummary = "Select a conflict to inspect the evidence.";
            return;
        }
        SelectionSummary = $"{value.IpAddress} · {value.State} · {value.Confidence}% confidence · {value.Method}";
        _ = LoadObservationsAsync(value.IpAddress);
    }

    private async Task LoadObservationsAsync(string ip)
    {
        try
        {
            var rows = await _conflicts.GetObservationsAsync(ip);
            Observations.Clear();
            foreach (var row in rows) Observations.Add(row);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunAsync() => await RunGuardedAsync(async () =>
    {
        IsResolving = true;
        try
        {
            var result = await _conflicts.RunAsync();
            await LoadCoreAsync();
            LastRunText = $"Last analysis {DateTime.Now:HH:mm:ss} · {result.Active} active " +
                          $"({result.Confirmed} confirmed, {result.Suspected} suspected) · " +
                          $"{result.New} new, {result.Resolved} resolved, {result.DatabaseDuplicates} database duplicate(s).";
            Ui.SetStatus($"Conflict analysis complete: {result.Active} active conflict(s).");
        }
        finally
        {
            IsResolving = false;
        }
    }, errorTitle: "Conflict analysis failed");

    [RelayCommand]
    private async Task ResolveAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedConflict is null) return;
        if (!Ui.Confirm($"Mark {SelectedConflict.IpAddress} as resolved?\n\nThe evidence is kept for the audit trail. " +
                        "Future scans can reopen the conflict if the evidence returns.", "Resolve conflict")) return;
        await _conflicts.ResolveAsync(SelectedConflict.Id, string.IsNullOrWhiteSpace(ResolutionNote) ? null : ResolutionNote.Trim());
        await LoadCoreAsync();
    }, "Conflict resolved.", "Unable to resolve the conflict");

    [RelayCommand]
    private async Task ReopenAsync() => await RunGuardedAsync(async () =>
    {
        if (SelectedConflict is null) return;
        await _conflicts.ReopenAsync(SelectedConflict.Id);
        await LoadCoreAsync();
    }, "Conflict reopened as suspected.", "Unable to reopen the conflict");

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void OpenAddressMap()
    {
        if (SelectedConflict?.SubnetId is > 0) _navigator.Navigate("IpManager", SelectedConflict.SubnetId.Value);
        else _navigator.Navigate("IpManager");
    }

    [RelayCommand]
    private void FindDevice()
    {
        if (SelectedConflict is null) return;
        _navigator.Navigate("Devices", SelectedConflict.IpAddress);
    }

    [RelayCommand]
    private void ClearFilter()
    {
        SearchText = "";
        SelectedState = StateOptions[0];
    }
}
