using System.Windows;
using System.Windows.Controls;
using IPManager.App.ViewModels;

namespace IPManager.App.Views;

public partial class DnsView : UserControl
{
    public DnsView() => InitializeComponent();

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DnsViewModel vm && sender is PasswordBox box) vm.IntegrationSecret = box.Password;
    }
}
