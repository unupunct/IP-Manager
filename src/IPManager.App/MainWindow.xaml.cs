using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using IPManager.App.Infrastructure;
using IPManager.App.ViewModels;

namespace IPManager.App;

public partial class MainWindow : Window
{
    private readonly IThemeService _theme;

    public MainWindow(ShellViewModel shell, IThemeService theme)
    {
        InitializeComponent();
        DataContext = shell;
        _theme = theme;
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        theme.ThemeChanged += (_, _) => Dispatcher.Invoke(ApplyTitleBarTheme);
    }

    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var dark = _theme.IsDark;
        var darkMode = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref darkMode, sizeof(int));

        // Windows 11 allows the caption and border to match the app surface.
        var caption = dark ? 0x0020120B : 0x00F8FAFC;   // COLORREF = 0x00BBGGRR
        var text = dark ? 0x00F1F5F9 : 0x000F172A;
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
