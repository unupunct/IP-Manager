using System.Windows;
using System.Windows.Controls;
using IPManager.App.ViewModels;

namespace IPManager.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnTelegramSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box) vm.TelegramToken = box.Password;
    }

    private void OnEmailSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box) vm.EmailPassword = box.Password;
    }
}
