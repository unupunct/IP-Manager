using System.IO;
using System.Windows;
using IPManager.Infrastructure;
using Microsoft.Win32;

namespace IPManager.App.Infrastructure;

/// <summary>Small abstraction over dialogs and the shell status bar so view models stay testable and UI-free.</summary>
public interface IUiService
{
    event EventHandler<string>? StatusChanged;
    void SetStatus(string message);
    void Info(string message, string? title = null);
    void Error(string message, string? title = null);
    bool Confirm(string message, string? title = null);
    string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null);
    string? OpenFile(string filter, string? initialDirectory = null);
    string? PickFolder(string? initialDirectory = null);
    void OpenInExplorer(string path);
    void OpenUrl(string url);
    void RunOnUi(Action action);
}

public sealed class UiService : IUiService
{
    public event EventHandler<string>? StatusChanged;

    public void SetStatus(string message)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => SetStatus(message));
            return;
        }
        StatusChanged?.Invoke(this, message);
    }

    public void Info(string message, string? title = null)
        => Show(message, title ?? "IP Manager", MessageBoxImage.Information);

    public void Error(string message, string? title = null)
        => Show(message, title ?? "IP Manager", MessageBoxImage.Error);

    public bool Confirm(string message, string? title = null)
        => Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(Application.Current.MainWindow, message, title ?? "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);

    public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null)
        => Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new SaveFileDialog
            {
                Filter = filter,
                FileName = defaultFileName,
                AddExtension = true,
                OverwritePrompt = true
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        });

    public string? OpenFile(string filter, string? initialDirectory = null)
        => Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (!string.IsNullOrWhiteSpace(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
        });

    public string? PickFolder(string? initialDirectory = null)
        => Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new OpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
        });

    public void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Info($"The path does not exist yet:\n{path}");
        }
        catch (Exception ex)
        {
            Error(ex.Message, "Unable to open Explorer");
        }
    }

    public void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error(ex.Message, "Unable to open the link");
        }
    }

    public void RunOnUi(Action action)
    {
        if (Application.Current.Dispatcher.CheckAccess()) action();
        else Application.Current.Dispatcher.BeginInvoke(action);
    }

    private static void Show(string message, string title, MessageBoxImage icon)
        => Application.Current.Dispatcher.Invoke(() =>
        {
            var owner = Application.Current.MainWindow;
            if (owner is not null && owner.IsLoaded)
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
            else
                MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        });
}

public sealed record NavigationRequest(string PageKey, object? Argument);

/// <summary>Lets page view models ask the shell to navigate (for example Dashboard -> Conflicts).</summary>
public interface IUiNavigator
{
    event EventHandler<NavigationRequest>? NavigationRequested;
    void Navigate(string pageKey, object? argument = null);
}

public interface IThemeService
{
    event EventHandler? ThemeChanged;
    bool IsDark { get; }
    void Apply(string theme);
    void ApplyCurrent();
}

public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;

    public ThemeService(ISettingsService settings) => _settings = settings;

    public event EventHandler? ThemeChanged;

    public bool IsDark => !string.Equals(_settings.Current.Theme, "Light", StringComparison.OrdinalIgnoreCase);

    public void ApplyCurrent() => Apply(_settings.Current.Theme);

    public void Apply(string theme)
    {
        var name = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var app = Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Themes/{name}.xaml", UriKind.Relative)
        };

        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                                 d.Source?.OriginalString.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true);

        if (existing is null)
        {
            app.Resources.MergedDictionaries.Insert(0, dictionary);
        }
        else
        {
            var index = app.Resources.MergedDictionaries.IndexOf(existing);
            app.Resources.MergedDictionaries.RemoveAt(index);
            app.Resources.MergedDictionaries.Insert(index, dictionary);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}
