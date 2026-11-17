using System.IO;
using System.Windows;
using System.Windows.Threading;
using IPManager.App.Infrastructure;
using IPManager.App.ViewModels;
using IPManager.Infrastructure;
using IPManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace IPManager.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled application exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            var paths = AppPaths.Load();
            Directory.CreateDirectory(paths.LogDirectory);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(paths.LogDirectory, "ipmanager-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true)
                .CreateLogger();
            Log.Information("IP Manager starting. Data directory: {DataDirectory}", paths.DataDirectory);

            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Services.AddSingleton(paths);
            builder.Services.AddIpManagerInfrastructure(paths);
            builder.Services.AddIpManagerServices();
            builder.Services.AddSingleton<IUiService, UiService>();
            builder.Services.AddSingleton<IThemeService, ThemeService>();
            builder.Services.AddSingleton<ShellViewModel>();
            builder.Services.AddSingleton<IUiNavigator>(sp => sp.GetRequiredService<ShellViewModel>());

            builder.Services.AddSingleton<DashboardViewModel>();
            builder.Services.AddSingleton<LocalAddressesViewModel>();
            builder.Services.AddSingleton<DiscoveryViewModel>();
            builder.Services.AddSingleton<DevicesViewModel>();
            builder.Services.AddSingleton<IpManagerViewModel>();
            builder.Services.AddSingleton<SubnetCalculatorViewModel>();
            builder.Services.AddSingleton<SubnetManagerViewModel>();
            builder.Services.AddSingleton<DhcpViewModel>();
            builder.Services.AddSingleton<DnsViewModel>();
            builder.Services.AddSingleton<ConflictsViewModel>();
            builder.Services.AddSingleton<NetworkMapViewModel>();
            builder.Services.AddSingleton<MonitoringViewModel>();
            builder.Services.AddSingleton<DiagnosticsViewModel>();
            builder.Services.AddSingleton<AlertsViewModel>();
            builder.Services.AddSingleton<ReportsViewModel>();
            builder.Services.AddSingleton<SettingsViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();
            await _host.StartAsync();

            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            var settings = _host.Services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync();
            _host.Services.GetRequiredService<IThemeService>().ApplyCurrent();

            var oui = _host.Services.GetRequiredService<OuiService>();
            await oui.InitializeAsync();

            if (settings.Current.BackupOnStartup)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var backup = _host.Services.GetRequiredService<IBackupService>();
                        var info = await backup.CreateBackupAsync("startup");
                        await backup.PruneAsync(settings.Current.BackupRetentionCount);
                        Log.Information("Startup backup created: {Path}", info.Path);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Startup backup failed");
                    }
                });
            }

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();

            Log.Information("Main window shown");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show($"IP Manager could not start.\n\n{ex.Message}\n\nSee the log for details.",
                "IP Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show($"An unexpected error occurred.\n\n{e.Exception.Message}\n\nDetails were written to the log file.",
            "IP Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while shutting down");
        }
        finally
        {
            Log.CloseAndFlush();
        }
        base.OnExit(e);
    }
}
