using CommunityToolkit.Mvvm.ComponentModel;

namespace IPManager.App.Infrastructure;

/// <summary>Common page lifecycle and guarded work helper.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    protected ViewModelBase(IUiService ui) => Ui = ui;

    protected IUiService Ui { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task RunGuardedAsync(Func<Task> action, string? success = null, string? errorTitle = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "";
        try
        {
            await action();
            if (!string.IsNullOrWhiteSpace(success))
            {
                StatusText = success;
                Ui.SetStatus(success);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation cancelled.";
            Ui.SetStatus(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            Ui.Error(ex.Message, errorTitle ?? "Something went wrong");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Pages that want to refresh while visible and stop timers when hidden.</summary>
public interface IActivatable
{
    Task ActivateAsync();
    void Deactivate();
}

/// <summary>Receives an argument from the shell (a device id, a search term, ...).</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(object? argument);
}

public sealed class NullUiService : IUiService
{
    public event EventHandler<string>? StatusChanged;
    public void SetStatus(string message) => StatusChanged?.Invoke(this, message);
    public void Info(string message, string? title = null) { }
    public void Error(string message, string? title = null) { }
    public bool Confirm(string message, string? title = null) => true;
    public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null) => null;
    public string? OpenFile(string filter, string? initialDirectory = null) => null;
    public string? PickFolder(string? initialDirectory = null) => null;
    public void OpenInExplorer(string path) { }
    public void OpenUrl(string url) { }
    public void RunOnUi(Action action) => action();
}
