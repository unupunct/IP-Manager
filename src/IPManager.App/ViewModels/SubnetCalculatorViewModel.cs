using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPManager.App.Infrastructure;
using IPManager.Core;

namespace IPManager.App.ViewModels;

public sealed partial class SubnetCalculatorViewModel : ViewModelBase, IActivatable
{
    public SubnetCalculatorViewModel(IUiService ui) : base(ui)
    {
        PrefixOptions = Enumerable.Range(0, 33).ToList();
        Address = "192.168.1.10";
        PrefixLength = 24;
    }

    public IReadOnlyList<int> PrefixOptions { get; }
    public ObservableCollection<SubnetDivisionRow> Division { get; } = new();
    public ObservableCollection<string> Ipv6Subnets { get; } = new();

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private int _prefixLength = 24;
    [ObservableProperty] private SubnetCalculation? _calculation;
    [ObservableProperty] private string _divisionCount = "4";
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private int _actualDivisionCount;

    [ObservableProperty] private string _ipv6Address = "2001:db8::1";
    [ObservableProperty] private string _ipv6Prefix = "64";
    [ObservableProperty] private string _ipv6Network = "";
    [ObservableProperty] private string _ipv6Total = "";
    [ObservableProperty] private string _ipv6Usable = "";
    [ObservableProperty] private string _ipv6Type = "";
    [ObservableProperty] private string _ipv6Binary = "";
    [ObservableProperty] private string _ipv6Error = "";
    [ObservableProperty] private string _ipv6NewPrefix = "68";
    [ObservableProperty] private string _ipv6DivisionMessage = "";

    public async Task ActivateAsync() => await LoadAsync();

    public void Deactivate() { }

    public override Task LoadAsync()
    {
        Calculate();
        CalculateIpv6();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Calculate()
    {
        ValidationMessage = "";
        Calculation = null;
        try
        {
            var input = Address.Trim();
            var text = input;
            var prefix = PrefixLength;
            if (input.Contains('/'))
            {
                var parts = input.Split('/', 2);
                text = parts[0].Trim();
                prefix = int.Parse(parts[1]);
            }
            if (!Ipv4Network.TryParseCidr($"{text}/{prefix}", out var network, out var error))
            {
                ValidationMessage = error;
                return;
            }
            Calculation = SubnetCalculator.Calculate(text, prefix);
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Divide()
    {
        if (Calculation is null)
        {
            ValidationMessage = "Calculate a subnet before dividing it.";
            return;
        }
        if (!int.TryParse(DivisionCount, out var requested) || requested < 1 || requested > 65536)
        {
            ValidationMessage = "Enter a division count between 1 and 65536.";
            return;
        }
        try
        {
            var result = SubnetCalculator.Divide(Calculation.Network, requested);
            Division.Clear();
            foreach (var row in result.Rows) Division.Add(row);
            ActualDivisionCount = result.ActualCount;
            ValidationMessage = result.ActualCount == requested
                ? $"Divided into {result.ActualCount} /{result.NewPrefix} subnets."
                : $"Requested {requested}; equal division produces {result.ActualCount} /{result.NewPrefix} subnets.";
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void CalculateIpv6()
    {
        Ipv6Error = "";
        if (!Ipv6Utils.TryParse(Ipv6Address, out var address, out var error))
        {
            Ipv6Error = error;
            Ipv6Network = Ipv6Total = Ipv6Usable = Ipv6Type = Ipv6Binary = "";
            return;
        }
        if (!int.TryParse(Ipv6Prefix, out var prefix) || prefix is < 0 or > 128)
        {
            Ipv6Error = "The prefix must be a number between 0 and 128.";
            return;
        }

        Ipv6Network = $"{Ipv6Utils.NetworkAddress(address, prefix)}/{prefix}";
        Ipv6Total = Ipv6Utils.TotalAddresses(prefix);
        Ipv6Usable = Ipv6Utils.UsableHosts(prefix);
        Ipv6Type = Ipv6Utils.AddressType(address);
        Ipv6Binary = Ipv6Utils.ToBinaryString(address, prefix);
    }

    [RelayCommand]
    private void DivideIpv6()
    {
        Ipv6DivisionMessage = "";
        Ipv6Subnets.Clear();
        if (!Ipv6Utils.TryParse(Ipv6Address, out var address, out var error))
        {
            Ipv6DivisionMessage = error;
            return;
        }
        if (!int.TryParse(Ipv6Prefix, out var prefix) || !int.TryParse(Ipv6NewPrefix, out var newPrefix))
        {
            Ipv6DivisionMessage = "Enter numeric prefixes.";
            return;
        }
        try
        {
            var parts = Ipv6Utils.Divide(address, prefix, newPrefix, 256);
            foreach (var part in parts) Ipv6Subnets.Add(part);
            Ipv6DivisionMessage = $"{parts.Count:N0} subnet(s). Display limit: 256. Use the calculator for larger plans.";
        }
        catch (Exception ex)
        {
            Ipv6DivisionMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void CopySummary()
    {
        if (Calculation is null) return;
        var c = Calculation;
        var text = $"""
                   {c.Cidr}
                   Network:      {c.NetworkAddress}
                   Broadcast:    {c.BroadcastAddress}
                   Mask:         {c.SubnetMask}
                   Wildcard:     {c.WildcardMask}
                   First usable: {c.FirstUsable}
                   Last usable:  {c.LastUsable}
                   Total:        {c.TotalAddresses:N0}
                   Usable:       {c.UsableHosts:N0}
                   Class:        {c.LegacyClass}
                   Binary:       {c.BinaryAddress}
                   """;
        try
        {
            System.Windows.Clipboard.SetText(text);
            Ui.SetStatus("Subnet summary copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message, "Clipboard unavailable");
        }
    }
}
