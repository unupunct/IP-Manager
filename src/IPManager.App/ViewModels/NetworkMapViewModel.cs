using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;
using IPManager.Services;

namespace IPManager.App.ViewModels;

public sealed class MapGroupingOption
{
    public required string Name { get; init; }
    public required MapGrouping Value { get; init; }
}

public sealed partial class MapNodeView : ObservableObject
{
    public required MapNode Node { get; init; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private bool _isSelected;

    public double Width => 184;
    public double Height => 62;
    public string Id => Node.Id;
    public string Title => Node.Label;
    public string Subtitle => Node.Kind switch
    {
        MapNodeKind.Internet => "External boundary",
        MapNodeKind.Gateway => string.IsNullOrWhiteSpace(Node.IpAddress) ? "Gateway" : Node.IpAddress!,
        _ => string.IsNullOrWhiteSpace(Node.IpAddress) ? Node.Detail : Node.IpAddress!
    };
    public string KindText => Node.Kind.ToString();
    public string StatusText => Node.Kind == MapNodeKind.Device ? Node.Status.ToString() : "Infrastructure";
    public string Tooltip => $"{Node.Label}\n{Node.Detail}\nGroup: {Node.GroupKey}";
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public double BottomY => Y + Height;

    partial void OnXChanged(double value)
    {
        OnPropertyChanged(nameof(CenterX));
    }

    partial void OnYChanged(double value)
    {
        OnPropertyChanged(nameof(CenterY));
        OnPropertyChanged(nameof(BottomY));
    }
}

public sealed record MapEdgeView(
    MapEdge Edge,
    double X1,
    double Y1,
    double X2,
    double Y2)
{
    public string ConfidenceText => Edge.Confidence.ToString();
    public string Tooltip => $"{Edge.Confidence}: {Edge.Label}";
}

public sealed partial class NetworkMapViewModel : ViewModelBase, IActivatable
{
    private readonly NetworkMapService _map;
    private readonly IUiNavigator _navigator;
    private NetworkMapGraph? _graph;

    public NetworkMapViewModel(NetworkMapService map, IUiNavigator navigator, IUiService ui) : base(ui)
    {
        _map = map;
        _navigator = navigator;
        Groupings = new ObservableCollection<MapGroupingOption>
        {
            new() { Name = "Group by subnet", Value = MapGrouping.Subnet },
            new() { Name = "Group by category", Value = MapGrouping.Category }
        };
        SelectedGrouping = Groupings[0];
    }

    public ObservableCollection<MapNodeView> Nodes { get; } = new();
    public ObservableCollection<MapEdgeView> Edges { get; } = new();
    public ObservableCollection<MapEdge> Connections { get; } = new();
    public ObservableCollection<MapGroupingOption> Groupings { get; }

    [ObservableProperty] private MapGroupingOption? _selectedGrouping;
    [ObservableProperty] private MapNodeView? _selectedNode;
    [ObservableProperty] private double _canvasWidth = 1000;
    [ObservableProperty] private double _canvasHeight = 600;
    [ObservableProperty] private int _deviceCount;
    [ObservableProperty] private int _gatewayCount;
    [ObservableProperty] private int _verifiedEdges;
    [ObservableProperty] private int _inferredEdges;
    [ObservableProperty] private int _unknownEdges;
    [ObservableProperty] private string _mapSummary = "Build the map to visualize documented relationships.";
    [ObservableProperty] private string _selectedSummary = "Select a node to inspect it.";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _hasLayout;
    [ObservableProperty] private int _unassignedCount;

    public bool HasNodes => Nodes.Count > 0;
    public bool HasSelection => SelectedNode is not null;

    public async Task ActivateAsync() => await BuildAsync();

    public void Deactivate() { }

    public override async Task LoadAsync() => await BuildAsync();

    partial void OnSelectedGroupingChanged(MapGroupingOption? value)
    {
        if (value is not null) _ = BuildAsync();
    }

    partial void OnSelectedNodeChanged(MapNodeView? oldValue, MapNodeView? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Connections.Clear();
        OnPropertyChanged(nameof(HasSelection));
        if (newValue is null)
        {
            SelectedSummary = "Select a node to inspect it.";
            return;
        }
        if (_graph is not null)
        {
            foreach (var edge in _graph.Edges.Where(e => e.SourceId == newValue.Id || e.TargetId == newValue.Id))
                Connections.Add(edge);
        }
        SelectedSummary = $"{newValue.Node.Label} · {newValue.KindText} · {newValue.Node.Detail}" +
                          (string.IsNullOrWhiteSpace(newValue.Node.SubnetCidr) ? "" : $" · {newValue.Node.SubnetCidr}");
    }

    [RelayCommand]
    private async Task BuildAsync() => await RunGuardedAsync(async () =>
    {
        var grouping = SelectedGrouping?.Value ?? MapGrouping.Subnet;
        var graph = await _map.BuildAsync(grouping);
        _graph = graph;
        Nodes.Clear();
        Edges.Clear();
        Connections.Clear();
        SelectedNode = null;

        Layout(graph);

        DeviceCount = graph.Nodes.Count(n => n.Kind == MapNodeKind.Device);
        GatewayCount = graph.Nodes.Count(n => n.Kind == MapNodeKind.Gateway);
        UnassignedCount = graph.Nodes.Count(n => n.Kind == MapNodeKind.Device && n.SubnetId is null);
        VerifiedEdges = graph.Edges.Count(e => e.Confidence == MapEdgeConfidence.Verified);
        InferredEdges = graph.Edges.Count(e => e.Confidence == MapEdgeConfidence.Inferred);
        UnknownEdges = graph.Edges.Count(e => e.Confidence == MapEdgeConfidence.Unknown);
        Notes = graph.Notes;
        MapSummary = $"{DeviceCount} device(s) · {GatewayCount} gateway(s) · {Edges.Count} edge(s) · generated {graph.GeneratedAt.ToLocalTime():HH:mm:ss}";
        HasLayout = false;
        OnPropertyChanged(nameof(HasNodes));

        try
        {
            var saved = await _map.GetLayoutAsync("Default");
            if (saved is not null && !string.IsNullOrWhiteSpace(saved.Json) && saved.Json != "{}")
            {
                ApplyLayout(saved.Json);
                HasLayout = true;
            }
        }
        catch
        {
            // a malformed saved layout must not prevent displaying the map
        }
    }, errorTitle: "Unable to build the network map");

    private void Layout(NetworkMapGraph graph)
    {
        const double columnWidth = 232;
        const double rowHeight = 78;
        const double margin = 24;

        var internet = graph.Nodes.Where(n => n.Kind == MapNodeKind.Internet).ToList();
        var gateways = graph.Nodes.Where(n => n.Kind == MapNodeKind.Gateway)
            .OrderBy(n => n.GroupKey, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(n => n.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var devices = graph.Nodes.Where(n => n.Kind == MapNodeKind.Device).ToList();

        var index = 0;
        foreach (var node in internet)
            Nodes.Add(new MapNodeView { Node = node, X = margin, Y = margin });
        index = internet.Count;

        var gatewayY = internet.Count > 0 ? 120 : margin;
        for (var i = 0; i < gateways.Count; i++)
        {
            Nodes.Add(new MapNodeView
            {
                Node = gateways[i],
                X = margin + i * columnWidth,
                Y = gatewayY
            });
        }

        var groups = devices
            .GroupBy(d => string.IsNullOrWhiteSpace(d.GroupKey) ? "Unassigned" : d.GroupKey)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var deviceTop = gatewayY + (gateways.Count > 0 ? 130 : 100);
        var maxRows = 0;
        var column = 0;
        foreach (var group in groups)
        {
            var row = 0;
            foreach (var node in group.OrderBy(d => d.IpAddress, StringComparer.OrdinalIgnoreCase))
            {
                Nodes.Add(new MapNodeView
                {
                    Node = node,
                    X = margin + column * columnWidth,
                    Y = deviceTop + row * rowHeight
                });
                row++;
            }
            maxRows = Math.Max(maxRows, row);
            column++;
        }

        var columns = Math.Max(groups.Count, gateways.Count);
        if (columns == 0) columns = 1;
        CanvasWidth = Math.Max(900, margin * 2 + columns * columnWidth);
        CanvasHeight = Math.Max(480, deviceTop + Math.Max(1, maxRows) * rowHeight + 100);

        // Recompute edges after all node positions are known.
        foreach (var edge in graph.Edges)
        {
            var source = Nodes.FirstOrDefault(n => n.Id == edge.SourceId);
            var target = Nodes.FirstOrDefault(n => n.Id == edge.TargetId);
            if (source is null || target is null) continue;
            var (x1, y1, x2, y2) = EdgePoints(source, target);
            Edges.Add(new MapEdgeView(edge, x1, y1, x2, y2));
        }
    }

    private static (double, double, double, double) EdgePoints(MapNodeView source, MapNodeView target)
    {
        if (target.Y >= source.BottomY)
            return (source.CenterX, source.BottomY, target.CenterX, target.Y);
        if (source.Y >= target.BottomY)
            return (source.CenterX, source.Y, target.CenterX, target.BottomY);
        if (source.CenterX <= target.CenterX)
            return (source.X + source.Width, source.CenterY, target.X, target.CenterY);
        return (source.X, source.CenterY, target.X + target.Width, target.CenterY);
    }

    private void ApplyLayout(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return;
            foreach (var item in nodes.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.GetString();
                var view = Nodes.FirstOrDefault(n => n.Id == id);
                if (view is null) continue;
                if (item.TryGetProperty("x", out var x)) view.X = x.GetDouble();
                if (item.TryGetProperty("y", out var y)) view.Y = y.GetDouble();
            }
            RebuildEdges();
        }
        catch (Exception ex)
        {
            Ui.Error("The saved map layout could not be read; the automatic layout is shown.\n" + ex.Message,
                "Saved map layout");
        }
    }

    private void RebuildEdges()
    {
        if (_graph is null) return;
        Edges.Clear();
        foreach (var edge in _graph.Edges)
        {
            var source = Nodes.FirstOrDefault(n => n.Id == edge.SourceId);
            var target = Nodes.FirstOrDefault(n => n.Id == edge.TargetId);
            if (source is null || target is null) continue;
            var (x1, y1, x2, y2) = EdgePoints(source, target);
            Edges.Add(new MapEdgeView(edge, x1, y1, x2, y2));
        }
    }

    [RelayCommand]
    private async Task SaveLayoutAsync() => await RunGuardedAsync(async () =>
    {
        var payload = JsonSerializer.Serialize(new
        {
            version = 1,
            savedAt = DateTime.UtcNow,
            nodes = Nodes.Select(n => new { id = n.Id, x = Math.Round(n.X, 1), y = Math.Round(n.Y, 1) }).ToList()
        });
        await _map.SaveLayoutAsync(payload, "Default");
        HasLayout = true;
    }, "Map layout saved.", "Unable to save the map layout");

    [RelayCommand]
    private async Task ResetLayoutAsync() => await RunGuardedAsync(async () =>
    {
        if (_graph is null) return;
        Nodes.Clear();
        Edges.Clear();
        Layout(_graph);
        await _map.SaveLayoutAsync("{}", "Default");
        HasLayout = false;
    }, "Automatic layout restored.", "Unable to reset the map layout");

    [RelayCommand]
    private void FitToCanvas()
    {
        if (Nodes.Count == 0) return;
        CanvasWidth = Math.Max(900, Nodes.Max(n => n.X + n.Width) + 40);
        CanvasHeight = Math.Max(480, Nodes.Max(n => n.Y + n.Height) + 60);
    }

    [RelayCommand]
    private void OpenDevice()
    {
        if (SelectedNode?.Node.DeviceId is > 0) _navigator.Navigate("Devices", SelectedNode.Node.DeviceId.Value);
    }

    [RelayCommand]
    private void Refresh() => _ = BuildAsync();

    public void MoveNode(MapNodeView node, double x, double y)
    {
        node.X = Math.Max(0, x);
        node.Y = Math.Max(0, y);
        CanvasWidth = Math.Max(CanvasWidth, node.X + node.Width + 40);
        CanvasHeight = Math.Max(CanvasHeight, node.Y + node.Height + 40);
        RebuildEdges();
    }
}
