using System.Diagnostics;
using IPManager.Core;
using IPManager.Infrastructure;
using IPManager.Network;
using Microsoft.Extensions.DependencyInjection;

namespace IPManager.Services;

public static class ServicesServiceCollectionExtensions
{
    public static IServiceCollection AddIpManagerServices(this IServiceCollection services)
    {
        services.AddHttpClient("dns", c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("IPManager/1.0"); });
        services.AddHttpClient("notifications", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("IPManager/1.0"); });
        services.AddHttpClient("downloads", c => { c.Timeout = TimeSpan.FromSeconds(120); c.DefaultRequestHeaders.UserAgent.ParseAdd("IPManager/1.0"); });

        services.AddSingleton<OuiDatabase>(_ => OuiDatabase.CreateWithSeed());
        services.AddSingleton<OuiService>();
        services.AddSingleton<IDnsResolver, DnsResolver>();
        services.AddSingleton<ScannerService>();

        services.AddSingleton<INotificationChannel, TelegramNotificationChannel>();
        services.AddSingleton<INotificationChannel, EmailNotificationChannel>();
        services.AddSingleton<IAlertService, AlertService>();

        services.AddSingleton<InventoryService>();
        services.AddSingleton<ConflictService>();
        services.AddSingleton<MonitoringService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<ReportsService>();
        services.AddSingleton<NetworkMapService>();
        services.AddSingleton<DiscoveryService>();
        services.AddSingleton<MaintenanceService>();
        services.AddSingleton<StartupService>();

        services.AddSingleton<IDhcpProvider, LocalDocumentationDhcpProvider>();
        services.AddSingleton<IDhcpProvider, DnsmasqFileDhcpProvider>();
        services.AddSingleton<IDhcpProvider, IscDhcpFileProvider>();
        services.AddSingleton<IDhcpProvider, KeaDhcpFileProvider>();
        services.AddSingleton<IDhcpProvider, WindowsServerDhcpProvider>();
        services.AddSingleton<DhcpService>();

        services.AddSingleton<IDnsProvider, PiHoleDnsProvider>();
        services.AddSingleton<IDnsProvider, AdGuardHomeDnsProvider>();
        services.AddSingleton<IDnsProvider, CloudflareDnsProvider>();
        services.AddSingleton<IDnsProvider, WindowsDnsServerProvider>();
        services.AddSingleton<DnsManagerService>();

        services.AddHostedService<MonitoringBackgroundService>();
        return services;
    }
}

/// <summary>Manages the per-user Windows startup entry (HKCU Run key). No service is installed.</summary>
public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IPManager";

    public string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "IPManager.exe");

    public bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var result = RunReg($"query \"HKCU\\{RunKey}\" /v {ValueName}");
            return result.exitCode == 0 && result.output.Contains(ValueName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public (bool Success, string Message) SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Windows startup registration is only available on Windows.");
        try
        {
            if (enabled)
            {
                var command = $"\"{ExecutablePath}\"";
                var result = RunReg($"add \"HKCU\\{RunKey}\" /v {ValueName} /t REG_SZ /d \"{command}\" /f");
                return (result.exitCode == 0, result.exitCode == 0 ? "IP Manager will start with Windows." : result.output);
            }
            else
            {
                var result = RunReg($"delete \"HKCU\\{RunKey}\" /v {ValueName} /f");
                return (result.exitCode == 0 || result.output.Contains("unable to find", StringComparison.OrdinalIgnoreCase),
                    "Startup entry removed.");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (int exitCode, string output) RunReg(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start reg.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }
}
