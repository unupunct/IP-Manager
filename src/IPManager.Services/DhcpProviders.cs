using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPManager.Core;
using IPManager.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Services;

/// <summary>Parsers for well-known DHCP lease/config text formats. Pure functions so they can be unit tested.</summary>
public static class DhcpFileParsers
{
    /// <summary>dnsmasq lease file: "expiry mac ip hostname client-id".</summary>
    public static List<RemoteDhcpLease> ParseDnsmasqLeases(string content)
    {
        var leases = new List<RemoteDhcpLease>();
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!long.TryParse(parts[0], out var expiry)) continue;
            var mac = MacUtil.Normalize(parts[1]);
            if (mac is null || !Ipv4Address.TryParse(parts[2], out _)) continue;
            DateTime? expires = expiry <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime;
            leases.Add(new RemoteDhcpLease(parts[2], mac, parts.Length > 3 && parts[3] != "*" ? parts[3] : null, expires));
        }
        return leases;
    }

    /// <summary>dnsmasq configuration: dhcp-host=mac,ip[,hostname] reservations and dhcp-range pools.</summary>
    public static (List<RemoteDhcpReservation> Reservations, List<(string Start, string End)> Ranges) ParseDnsmasqConfig(string content)
    {
        var reservations = new List<RemoteDhcpReservation>();
        var ranges = new List<(string, string)>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            if (line.StartsWith("dhcp-host=", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["dhcp-host=".Length..];
                var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                string? mac = null;
                string? ip = null;
                string? host = null;
                foreach (var part in parts)
                {
                    if (MacUtil.IsValid(part)) mac = MacUtil.Normalize(part);
                    else if (Ipv4Address.TryParse(part, out _)) ip = part;
                    else if (!part.Contains(':')) host = part;
                }
                if (mac is not null && ip is not null)
                    reservations.Add(new RemoteDhcpReservation(ip, mac, host));
            }
            else if (line.StartsWith("dhcp-range=", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["dhcp-range=".Length..];
                var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var ips = parts.Where(p => Ipv4Address.TryParse(p, out _)).ToList();
                if (ips.Count >= 2) ranges.Add((ips[0], ips[1]));
            }
        }
        return (reservations, ranges);
    }

    /// <summary>ISC dhcpd.leases format.</summary>
    public static List<RemoteDhcpLease> ParseIscLeases(string content)
    {
        var leases = new List<RemoteDhcpLease>();
        var leaseRegex = new Regex(@"lease\s+(?<ip>\d+\.\d+\.\d+\.\d+)\s*\{(?<body>.*?)\}", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var macRegex = new Regex(@"hardware\s+ethernet\s+(?<mac>[0-9a-f:]{17})", RegexOptions.IgnoreCase);
        var nameRegex = new Regex(@"client-hostname\s+""(?<name>[^""]*)""", RegexOptions.IgnoreCase);
        var endsRegex = new Regex(@"ends\s+\d+\s+(?<date>[^;]+);", RegexOptions.IgnoreCase);

        foreach (Match m in leaseRegex.Matches(content))
        {
            var body = m.Groups["body"].Value;
            var mac = MacUtil.Normalize(macRegex.Match(body).Groups["mac"].Value);
            if (mac is null) continue;
            var host = nameRegex.Match(body).Success ? nameRegex.Match(body).Groups["name"].Value : null;
            DateTime? expires = null;
            var dateText = endsRegex.Match(body).Groups["date"].Value.Trim();
            if (DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                expires = d;
            leases.Add(new RemoteDhcpLease(m.Groups["ip"].Value, mac, host, expires));
        }
        return leases;
    }

    /// <summary>Kea DHCPv4 lease CSV (header names are read from the file).</summary>
    public static List<RemoteDhcpLease> ParseKeaLeasesCsv(string content)
    {
        var leases = new List<RemoteDhcpLease>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0) return leases;

        var header = SplitCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var addressIndex = header.IndexOf("address");
        var macIndex = header.IndexOf("hwaddr");
        var hostIndex = header.IndexOf("hostname");
        var expireIndex = header.IndexOf("expire");
        var validIndex = header.IndexOf("valid_lifetime");
        if (addressIndex < 0 || macIndex < 0) return leases;

        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsvLine(line);
            if (cells.Count <= Math.Max(addressIndex, macIndex)) continue;
            var ip = cells[addressIndex].Trim('"');
            var mac = MacUtil.Normalize(cells[macIndex].Trim('"'));
            if (!Ipv4Address.TryParse(ip, out _) || mac is null) continue;
            var host = hostIndex >= 0 && cells.Count > hostIndex ? cells[hostIndex].Trim('"') : null;
            DateTime? expires = null;
            if (expireIndex >= 0 && cells.Count > expireIndex &&
                DateTime.TryParse(cells[expireIndex].Trim('"').Replace(' ', 'T'), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exp))
                expires = exp;
            else if (validIndex >= 0 && cells.Count > validIndex && long.TryParse(cells[validIndex], out var seconds) && seconds > 0)
                expires = DateTime.UtcNow.AddSeconds(seconds);
            leases.Add(new RemoteDhcpLease(ip, mac, host, expires));
        }
        return leases;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }
}

public sealed class LocalDocumentationDhcpProvider : IDhcpProvider
{
    private readonly IDbContextFactory<IpManagerDbContext> _factory;
    public LocalDocumentationDhcpProvider(IDbContextFactory<IpManagerDbContext> factory) => _factory = factory;

    public DhcpProviderKind Kind => DhcpProviderKind.LocalDocumentation;
    public string DisplayName => "Local documentation (IP Manager database)";
    public string Requirements => "No external requirements. Records are documentation only; no router is contacted.";
    public bool CanRead => true;
    public bool CanWrite => true;

    public async Task<DhcpProviderResult> ReadAsync(IntegrationConfig config, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var scopes = await db.DhcpScopes.AsNoTracking().Include(s => s.Reservations).OrderBy(s => s.Name).ToListAsync(ct);
        var remote = scopes.Select(s => new RemoteDhcpScope
        {
            Id = s.Id.ToString(),
            Name = s.Name,
            StartAddress = s.StartAddress,
            EndAddress = s.EndAddress,
            Server = s.ServerAddress,
            Provider = s.Provider,
            Reservations = s.Reservations.Select(r => new RemoteDhcpReservation(r.IpAddress, r.MacAddress, r.Hostname)).ToList()
        }).ToList();
        return new DhcpProviderResult(true, $"{remote.Count} documented scope(s).", remote);
    }

    public Task CreateReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("Use the DHCP page to edit local documentation.");

    public Task DeleteReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("Use the DHCP page to edit local documentation.");
}

public sealed class DnsmasqFileDhcpProvider : IDhcpProvider
{
    public DhcpProviderKind Kind => DhcpProviderKind.DnsmasqFiles;
    public string DisplayName => "dnsmasq (config and lease files)";
    public string Requirements =>
        "Read access to the dnsmasq lease file (for example /var/lib/misc/dnsmasq.leases) and optionally dnsmasq.conf. " +
        "Windows can read these files if they are exported through a share or copied locally. Read-only.";
    public bool CanRead => true;
    public bool CanWrite => false;

    public async Task<DhcpProviderResult> ReadAsync(IntegrationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            return DhcpProviderResult.Fail("Set the path to the dnsmasq lease file first.");
        if (!File.Exists(config.Endpoint))
            return DhcpProviderResult.Fail($"Lease file not found: {config.Endpoint}");

        var leases = DhcpFileParsers.ParseDnsmasqLeases(await File.ReadAllTextAsync(config.Endpoint, ct));
        var configPath = GetOption(config, "configPath");
        var reservations = new List<RemoteDhcpReservation>();
        var ranges = new List<(string Start, string End)>();
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
            (reservations, ranges) = DhcpFileParsers.ParseDnsmasqConfig(await File.ReadAllTextAsync(configPath, ct));

        if (ranges.Count == 0 && leases.Count > 0)
        {
            var addresses = leases.Select(l => Ipv4Address.Parse(l.IpAddress)).OrderBy(a => a.Value).ToList();
            ranges.Add((addresses.First().ToString(), addresses.Last().ToString()));
        }

        var scopes = ranges.Select((r, i) => new RemoteDhcpScope
        {
            Id = $"range-{i}",
            Name = $"dnsmasq range {r.Start} - {r.End}",
            StartAddress = r.Start,
            EndAddress = r.End,
            Provider = "dnsmasq",
            Leases = leases.Where(l => InventoryService.InRange(l.IpAddress, r.Start, r.End)).ToList(),
            Reservations = reservations.Where(x => InventoryService.InRange(x.IpAddress, r.Start, r.End)).ToList()
        }).ToList();

        if (scopes.Count == 0 && leases.Count > 0)
        {
            scopes.Add(new RemoteDhcpScope
            {
                Id = "leases",
                Name = "dnsmasq leases",
                StartAddress = leases.Min(l => l.IpAddress),
                EndAddress = leases.Max(l => l.IpAddress),
                Provider = "dnsmasq",
                Leases = leases
            });
        }

        return new DhcpProviderResult(true, $"Read {leases.Count} lease(s) and {reservations.Count} reservation(s).", scopes);
    }

    public Task CreateReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("dnsmasq configuration files are read-only in IP Manager. Edit dnsmasq.conf on the server.");

    public Task DeleteReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("dnsmasq configuration files are read-only in IP Manager.");

    internal static string? GetOption(IntegrationConfig config, string key)
    {
        if (string.IsNullOrWhiteSpace(config.OptionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(config.OptionsJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class IscDhcpFileProvider : IDhcpProvider
{
    public DhcpProviderKind Kind => DhcpProviderKind.IscDhcpFiles;
    public string DisplayName => "ISC DHCP (dhcpd.leases file)";
    public string Requirements => "Read access to dhcpd.leases (usually /var/lib/dhcp/dhcpd.leases). Read-only; reservations are not parsed from dhcpd.conf.";
    public bool CanRead => true;
    public bool CanWrite => false;

    public async Task<DhcpProviderResult> ReadAsync(IntegrationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            return DhcpProviderResult.Fail("Set the path to dhcpd.leases first.");
        if (!File.Exists(config.Endpoint))
            return DhcpProviderResult.Fail($"Lease file not found: {config.Endpoint}");

        var leases = DhcpFileParsers.ParseIscLeases(await File.ReadAllTextAsync(config.Endpoint, ct));
        if (leases.Count == 0) return new DhcpProviderResult(true, "No active leases were found in the file.", Array.Empty<RemoteDhcpScope>());

        var addresses = leases.Select(l => Ipv4Address.Parse(l.IpAddress)).OrderBy(a => a.Value).ToList();
        var scope = new RemoteDhcpScope
        {
            Id = "isc",
            Name = "ISC DHCP leases",
            StartAddress = addresses.First().ToString(),
            EndAddress = addresses.Last().ToString(),
            Provider = "ISC DHCP",
            Leases = leases
        };
        return new DhcpProviderResult(true, $"Read {leases.Count} lease(s).", new[] { scope });
    }

    public Task CreateReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("ISC DHCP files are read-only in IP Manager.");

    public Task DeleteReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("ISC DHCP files are read-only in IP Manager.");
}

public sealed class KeaDhcpFileProvider : IDhcpProvider
{
    public DhcpProviderKind Kind => DhcpProviderKind.KeaDhcp;
    public string DisplayName => "Kea DHCP (lease CSV)";
    public string Requirements => "Read access to the Kea memfile lease CSV (for example kea-leases4.csv). Read-only.";
    public bool CanRead => true;
    public bool CanWrite => false;

    public async Task<DhcpProviderResult> ReadAsync(IntegrationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            return DhcpProviderResult.Fail("Set the path to the Kea lease CSV first.");
        if (!File.Exists(config.Endpoint))
            return DhcpProviderResult.Fail($"Lease file not found: {config.Endpoint}");

        var leases = DhcpFileParsers.ParseKeaLeasesCsv(await File.ReadAllTextAsync(config.Endpoint, ct));
        if (leases.Count == 0) return new DhcpProviderResult(true, "No leases were found in the CSV.", Array.Empty<RemoteDhcpScope>());

        var addresses = leases.Select(l => Ipv4Address.Parse(l.IpAddress)).OrderBy(a => a.Value).ToList();
        var scope = new RemoteDhcpScope
        {
            Id = "kea",
            Name = "Kea leases",
            StartAddress = addresses.First().ToString(),
            EndAddress = addresses.Last().ToString(),
            Provider = "Kea DHCP",
            Leases = leases
        };
        return new DhcpProviderResult(true, $"Read {leases.Count} lease(s).", new[] { scope });
    }

    public Task CreateReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("Kea lease files are read-only in IP Manager.");

    public Task DeleteReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("Kea lease files are read-only in IP Manager.");
}

/// <summary>
/// Read-only Windows Server DHCP integration through the DhcpServer PowerShell module.
/// Requires RSAT / the DHCP Server tools and permissions on the target server.
/// </summary>
public sealed class WindowsServerDhcpProvider : IDhcpProvider
{
    public DhcpProviderKind Kind => DhcpProviderKind.WindowsServerDhcp;
    public string DisplayName => "Windows Server DHCP (read-only, PowerShell)";
    public string Requirements =>
        "Windows Server with the DHCP Server role, the DhcpServer PowerShell module on this computer (RSAT), " +
        "and permissions to read scopes and leases. Reservation changes are intentionally not automated; use the Windows DHCP console.";
    public bool CanRead => true;
    public bool CanWrite => false;

    public async Task<DhcpProviderResult> ReadAsync(IntegrationConfig config, CancellationToken ct)
    {
        var server = string.IsNullOrWhiteSpace(config.Endpoint) ? "" : $"-ComputerName '{Escape(config.Endpoint)}' ";
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            return DhcpProviderResult.Fail("Set the DHCP server name (or 'localhost') first.");

        var script =
            "$ErrorActionPreference='Stop'; " +
            $"$scopes = Get-DhcpServerv4Scope {server}| Select-Object ScopeId,Name,StartRange,EndRange,SubnetMask,State; " +
            "$out = @(); foreach ($s in $scopes) { " +
            $"$res = Get-DhcpServerv4Reservation {server}-ScopeId $s.ScopeId -ErrorAction SilentlyContinue | Select-Object IPAddress,ClientId,Name; " +
            $"$leases = Get-DhcpServerv4Lease {server}-ScopeId $s.ScopeId -ErrorAction SilentlyContinue | Select-Object IPAddress,ClientId,HostName,LeaseExpiryTime; " +
            "$out += [pscustomobject]@{ Scope=$s; Reservations=$res; Leases=$leases } }; " +
            "$out | ConvertTo-Json -Depth 5 -Compress";

        string json;
        try { json = await DhcpService.RunPowerShellAsync(script, ct); }
        catch (Exception ex) { return DhcpProviderResult.Fail($"PowerShell failed: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(json))
            return DhcpProviderResult.Fail("No DHCP data was returned. Check that the DhcpServer module is installed and the server is reachable.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            var scopes = new List<RemoteDhcpScope>();
            foreach (var el in elements)
            {
                if (!el.TryGetProperty("Scope", out var s)) continue;
                var id = GetString(s, "ScopeId") ?? "";
                var reservations = new List<RemoteDhcpReservation>();
                if (el.TryGetProperty("Reservations", out var res) && res.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in res.EnumerateArray())
                    {
                        var mac = MacUtil.Normalize(GetString(r, "ClientId"));
                        var ip = GetString(r, "IPAddress");
                        if (mac is null || ip is null) continue;
                        reservations.Add(new RemoteDhcpReservation(ip, mac, GetString(r, "Name")));
                    }
                }

                var leases = new List<RemoteDhcpLease>();
                if (el.TryGetProperty("Leases", out var ls) && ls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in ls.EnumerateArray())
                    {
                        var mac = MacUtil.Normalize(GetString(l, "ClientId"));
                        var ip = GetString(l, "IPAddress");
                        if (mac is null || ip is null) continue;
                        DateTime? expires = DateTime.TryParse(GetString(l, "LeaseExpiryTime"), out var exp) ? exp.ToUniversalTime() : null;
                        leases.Add(new RemoteDhcpLease(ip, mac, GetString(l, "HostName"), expires));
                    }
                }

                scopes.Add(new RemoteDhcpScope
                {
                    Id = id,
                    Name = GetString(s, "Name") ?? id,
                    StartAddress = GetString(s, "StartRange") ?? "",
                    EndAddress = GetString(s, "EndRange") ?? "",
                    SubnetMask = GetString(s, "SubnetMask"),
                    Server = config.Endpoint,
                    Provider = "Windows Server DHCP",
                    Reservations = reservations,
                    Leases = leases
                });
            }

            return scopes.Count == 0
                ? DhcpProviderResult.Fail("The DHCP server returned no IPv4 scopes.")
                : new DhcpProviderResult(true, $"Read {scopes.Count} scope(s) from {config.Endpoint}.", scopes);
        }
        catch (Exception ex)
        {
            return DhcpProviderResult.Fail($"Unable to parse DHCP output: {ex.Message}");
        }
    }

    public Task CreateReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("Windows Server DHCP is read-only in this version. Create reservations in the Windows DHCP console.");

    public Task DeleteReservationAsync(IntegrationConfig config, string scopeId, RemoteDhcpReservation reservation, CancellationToken ct)
        => throw new NotSupportedException("Windows Server DHCP is read-only in this version.");

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
