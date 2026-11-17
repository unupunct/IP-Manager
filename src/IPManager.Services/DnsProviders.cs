using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IPManager.Core;
using IPManager.Infrastructure;

namespace IPManager.Services;

public sealed record RemoteDnsRecord(string Id, string Zone, string Name, string Type, string Value, int Ttl);

public sealed record DnsProviderResult(bool Success, string Message, IReadOnlyList<RemoteDnsRecord> Records)
{
    public static DnsProviderResult Fail(string message) => new(false, message, Array.Empty<RemoteDnsRecord>());
}

public interface IDnsProvider
{
    DnsProviderKind Kind { get; }
    string DisplayName { get; }
    string Requirements { get; }
    bool CanRead { get; }
    bool CanWrite { get; }
    Task<DnsProviderResult> TestAsync(IntegrationConfig config, ISecretProtector protector, CancellationToken ct);
    Task<DnsProviderResult> GetRecordsAsync(IntegrationConfig config, ISecretProtector protector, string? zone, CancellationToken ct);
    Task CreateRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct);
    Task DeleteRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct);
}

public abstract class DnsProviderBase : IDnsProvider
{
    protected readonly HttpClient Http;
    protected DnsProviderBase(IHttpClientFactory factory) => Http = factory.CreateClient("dns");

    public abstract DnsProviderKind Kind { get; }
    public abstract string DisplayName { get; }
    public abstract string Requirements { get; }
    public virtual bool CanRead => true;
    public virtual bool CanWrite => false;

    public abstract Task<DnsProviderResult> TestAsync(IntegrationConfig config, ISecretProtector protector, CancellationToken ct);
    public abstract Task<DnsProviderResult> GetRecordsAsync(IntegrationConfig config, ISecretProtector protector, string? zone, CancellationToken ct);

    public virtual Task CreateRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct)
        => throw new NotSupportedException($"{DisplayName} is read-only in IP Manager.");

    public virtual Task DeleteRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct)
        => throw new NotSupportedException($"{DisplayName} is read-only in IP Manager.");

    protected static string RequireEndpoint(IntegrationConfig config, string fallback)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.Endpoint) ? fallback : config.Endpoint.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Configure the endpoint URL first.");
        return endpoint;
    }

    protected static void UseBasicAuth(HttpClient http, IntegrationConfig config, ISecretProtector protector)
    {
        var password = protector.TryUnprotect(config.SecretProtected) ?? "";
        var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.Username ?? ""}:{password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    protected static string Describe(HttpResponseMessage response, string body) =>
        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(body) ? "" : ": " + (body.Length > 300 ? body[..300] : body))}";
}

public sealed class PiHoleDnsProvider : DnsProviderBase
{
    public PiHoleDnsProvider(IHttpClientFactory factory) : base(factory) { }

    public override DnsProviderKind Kind => DnsProviderKind.PiHole;
    public override string DisplayName => "Pi-hole (API v5 legacy)";
    public override string Requirements =>
        "Pi-hole web interface reachable over HTTP(S) and an API token from Settings > API/Web interface. " +
        "Local DNS records are read from the custom DNS endpoint; Pi-hole is read-only in IP Manager.";
    public override bool CanWrite => false;

    public override async Task<DnsProviderResult> TestAsync(IntegrationConfig config, ISecretProtector protector, CancellationToken ct)
    {
        var endpoint = RequireEndpoint(config, "");
        var token = protector.TryUnprotect(config.SecretProtected) ?? "";
        using var response = await Http.GetAsync($"{endpoint}/admin/api.php?summaryRaw&auth={Uri.EscapeDataString(token)}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return DnsProviderResult.Fail(Describe(response, body));
        return new DnsProviderResult(true, "Pi-hole API responded.", Array.Empty<RemoteDnsRecord>());
    }

    public override async Task<DnsProviderResult> GetRecordsAsync(IntegrationConfig config, ISecretProtector protector, string? zone, CancellationToken ct)
    {
        var endpoint = RequireEndpoint(config, "");
        var token = protector.TryUnprotect(config.SecretProtected) ?? "";
        using var response = await Http.GetAsync($"{endpoint}/admin/api.php?customdns&auth={Uri.EscapeDataString(token)}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return DnsProviderResult.Fail(Describe(response, body));

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return DnsProviderResult.Fail("Pi-hole returned no custom DNS data. The API token may be invalid or the version is v6 (use the v6 API in a future release).");

            var records = new List<RemoteDnsRecord>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var cells = item.EnumerateArray().Select(x => x.ToString()).ToList();
                if (cells.Count < 2) continue;
                var ip = cells[0];
                var type = IPAddress.TryParse(ip, out var parsed)
                    ? (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "A" : "AAAA")
                    : "CNAME";
                records.Add(new RemoteDnsRecord($"{cells[0]}|{cells[1]}", zone ?? "", cells[1], type, cells[0], 0));
            }
            return new DnsProviderResult(true, $"Read {records.Count} local DNS record(s) from Pi-hole.", records);
        }
        catch (Exception ex)
        {
            return DnsProviderResult.Fail($"Unable to parse Pi-hole response: {ex.Message}");
        }
    }
}

public sealed class AdGuardHomeDnsProvider : DnsProviderBase
{
    public AdGuardHomeDnsProvider(IHttpClientFactory factory) : base(factory) { }

    public override DnsProviderKind Kind => DnsProviderKind.AdGuardHome;
    public override string DisplayName => "AdGuard Home (rewrite API)";
    public override string Requirements =>
        "AdGuard Home reachable over HTTP(S), a username and password with access to the admin API. " +
        "DNS rewrites can be read, created and deleted.";
    public override bool CanWrite => true;

    public override async Task<DnsProviderResult> TestAsync(IntegrationConfig config, ISecretProtector protector, CancellationToken ct)
    {
        var endpoint = RequireEndpoint(config, "");
        UseBasicAuth(Http, config, protector);
        using var response = await Http.GetAsync($"{endpoint}/control/status", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode
            ? new DnsProviderResult(true, "AdGuard Home API responded.", Array.Empty<RemoteDnsRecord>())
            : DnsProviderResult.Fail(Describe(response, body));
    }

    public override async Task<DnsProviderResult> GetRecordsAsync(IntegrationConfig config, ISecretProtector protector, string? zone, CancellationToken ct)
    {
        var endpoint = RequireEndpoint(config, "");
        UseBasicAuth(Http, config, protector);
        using var response = await Http.GetAsync($"{endpoint}/control/rewrite/list", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return DnsProviderResult.Fail(Describe(response, body));

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return DnsProviderResult.Fail("Unexpected AdGuard Home response.");
            var records = new List<RemoteDnsRecord>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var domain = item.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
                var answer = item.TryGetProperty("answer", out var a) ? a.GetString() ?? "" : "";
                if (domain.Length == 0) continue;
                var type = IPAddress.TryParse(answer, out var ip) ? "A" : "CNAME";
                records.Add(new RemoteDnsRecord($"{domain}|{answer}", zone ?? "", domain, type, answer, 0));
            }
            return new DnsProviderResult(true, $"Read {records.Count} DNS rewrite(s) from AdGuard Home.", records);
        }
        catch (Exception ex)
        {
            return DnsProviderResult.Fail($"Unable to parse AdGuard Home response: {ex.Message}");
        }
    }

    public override async Task CreateRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.Value))
            throw new ArgumentException("A domain and an answer value are required.");
        var endpoint = RequireEndpoint(config, "");
        UseBasicAuth(Http, config, protector);
        using var response = await Http.PostAsJsonAsync($"{endpoint}/control/rewrite/add", new { domain = record.Name, answer = record.Value }, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Describe(response, await response.Content.ReadAsStringAsync(ct)));
    }

    public override async Task DeleteRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct)
    {
        var endpoint = RequireEndpoint(config, "");
        UseBasicAuth(Http, config, protector);
        using var response = await Http.PostAsJsonAsync($"{endpoint}/control/rewrite/delete", new { domain = record.Name, answer = record.Value }, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Describe(response, await response.Content.ReadAsStringAsync(ct)));
    }
}

public sealed class CloudflareDnsProvider : DnsProviderBase
{
    public CloudflareDnsProvider(IHttpClientFactory factory) : base(factory) { }

    public override DnsProviderKind Kind => DnsProviderKind.Cloudflare;
    public override string DisplayName => "Cloudflare DNS (API token)";
    public override string Requirements =>
        "A Cloudflare API token with Zone:Read and DNS:Edit permissions for the target zone, plus the zone name " +
        "(for example example.com). Record changes are sent to Cloudflare after explicit confirmation.";
    public override bool CanWrite => true;

    private const string DefaultEndpoint = "https://api.cloudflare.com/client/v4";

    private static HttpClient Authorized(HttpClient http, IntegrationConfig config, ISecretProtector protector)
    {
        var token = protector.TryUnprotect(config.SecretProtected)
                    ?? throw new InvalidOperationException("The Cloudflare API token could not be decrypted. Re-enter it in the DNS page.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public override async Task<DnsProviderResult> TestAsync(IntegrationConfig config, ISecretProtector protector, CancellationToken ct)
    {
        Authorized(Http, config, protector);
        var endpoint = RequireEndpoint(config, DefaultEndpoint);
        using var response = await Http.GetAsync($"{endpoint}/user/tokens/verify", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return DnsProviderResult.Fail(Describe(response, body));
        var ok = body.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase);
        return ok
            ? new DnsProviderResult(true, "Cloudflare API token is valid.", Array.Empty<RemoteDnsRecord>())
            : DnsProviderResult.Fail(Describe(response, body));
    }

    private async Task<string> GetZoneIdAsync(IntegrationConfig config, string zone, CancellationToken ct)
    {
        var endpoint = RequireEndpoint(config, DefaultEndpoint);
        using var response = await Http.GetAsync($"{endpoint}/zones?name={Uri.EscapeDataString(zone)}&per_page=1", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Describe(response, body));
        using var doc = JsonDocument.Parse(body);
        var results = doc.RootElement.GetProperty("result");
        if (results.GetArrayLength() == 0) throw new InvalidOperationException($"Zone '{zone}' was not found for this token.");
        return results[0].GetProperty("id").GetString() ?? throw new InvalidOperationException("Cloudflare returned a zone without an id.");
    }

    public override async Task<DnsProviderResult> GetRecordsAsync(IntegrationConfig config, ISecretProtector protector, string? zone, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zone)) return DnsProviderResult.Fail("Enter the Cloudflare zone name (for example example.com).");
        Authorized(Http, config, protector);
        var endpoint = RequireEndpoint(config, DefaultEndpoint);
        try
        {
            var zoneId = await GetZoneIdAsync(config, zone, ct);
            using var response = await Http.GetAsync($"{endpoint}/zones/{zoneId}/dns_records?per_page=200", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return DnsProviderResult.Fail(Describe(response, body));

            using var doc = JsonDocument.Parse(body);
            var records = new List<RemoteDnsRecord>();
            foreach (var item in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                records.Add(new RemoteDnsRecord(
                    item.GetProperty("id").GetString() ?? "",
                    zone,
                    item.GetProperty("name").GetString() ?? "",
                    item.GetProperty("type").GetString() ?? "",
                    item.GetProperty("content").GetString() ?? "",
                    item.TryGetProperty("ttl", out var ttl) ? ttl.GetInt32() : 1));
            }
            return new DnsProviderResult(true, $"Read {records.Count} record(s) from Cloudflare zone {zone}.", records);
        }
        catch (Exception ex)
        {
            return DnsProviderResult.Fail(ex.Message);
        }
    }

    public override async Task CreateRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.Zone)) throw new ArgumentException("A Cloudflare zone is required.");
        Authorized(Http, config, protector);
        var endpoint = RequireEndpoint(config, DefaultEndpoint);
        var zoneId = await GetZoneIdAsync(config, record.Zone, ct);
        var payload = new { type = record.Type.ToUpperInvariant(), name = record.Name, content = record.Value, ttl = record.Ttl <= 0 ? 1 : record.Ttl };
        using var response = await Http.PostAsJsonAsync($"{endpoint}/zones/{zoneId}/dns_records", payload, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Describe(response, await response.Content.ReadAsStringAsync(ct)));
    }

    public override async Task DeleteRecordAsync(IntegrationConfig config, ISecretProtector protector, RemoteDnsRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.Zone) || string.IsNullOrWhiteSpace(record.Id))
            throw new ArgumentException("The Cloudflare zone and record id are required.");
        Authorized(Http, config, protector);
        var endpoint = RequireEndpoint(config, DefaultEndpoint);
        var zoneId = await GetZoneIdAsync(config, record.Zone, ct);
        using var response = await Http.DeleteAsync($"{endpoint}/zones/{zoneId}/dns_records/{record.Id}", ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Describe(response, await response.Content.ReadAsStringAsync(ct)));
    }
}

/// <summary>Read-only Windows DNS Server integration through the DnsServer PowerShell module.</summary>
public sealed class WindowsDnsServerProvider : DnsProviderBase
{
    public WindowsDnsServerProvider(IHttpClientFactory factory) : base(factory) { }

    public override DnsProviderKind Kind => DnsProviderKind.WindowsDnsServer;
    public override string DisplayName => "Windows DNS Server (PowerShell, read-only)";
    public override string Requirements =>
        "The DnsServer PowerShell module (RSAT) and read permission on the target DNS server. " +
        "Records are read only; modify them in the Windows DNS console.";
    public override bool CanWrite => false;

    public override async Task<DnsProviderResult> TestAsync(IntegrationConfig config, ISecretProtector protector, CancellationToken ct)
    {
        var json = await DhcpService.RunPowerShellAsync("if (Get-Module -ListAvailable -Name DnsServer) { 'available' } else { 'missing' }", ct);
        return json.Trim().Contains("available", StringComparison.OrdinalIgnoreCase)
            ? new DnsProviderResult(true, "The DnsServer PowerShell module is available.", Array.Empty<RemoteDnsRecord>())
            : DnsProviderResult.Fail("The DnsServer module is not installed. Install the DNS Server tools (RSAT) on this computer.");
    }

    public override async Task<DnsProviderResult> GetRecordsAsync(IntegrationConfig config, ISecretProtector protector, string? zone, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(zone)) return DnsProviderResult.Fail("Enter the DNS zone name to read (for example corp.local).");
        var server = string.IsNullOrWhiteSpace(config.Endpoint) ? "" : $"-ComputerName '{config.Endpoint.Replace("'", "''")}' ";
        var script =
            "$ErrorActionPreference='Stop'; " +
            $"Get-DnsServerResourceRecord {server}-ZoneName '{zone.Replace("'", "''")}' | ForEach-Object {{ " +
            "[pscustomobject]@{ HostName=$_.HostName; Type=$_.RecordType.ToString(); TTL=[int]$_.TimeToLive.TotalSeconds; " +
            "Value=(($_.RecordData | Format-List * | Out-String) -replace '\\s+',' ').Trim() } } | ConvertTo-Json -Compress";

        string json;
        try { json = await DhcpService.RunPowerShellAsync(script, ct); }
        catch (Exception ex) { return DnsProviderResult.Fail(ex.Message); }
        if (string.IsNullOrWhiteSpace(json)) return DnsProviderResult.Fail("No records were returned.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            var records = new List<RemoteDnsRecord>();
            foreach (var item in items)
            {
                var name = item.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : "";
                var type = item.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                var value = item.TryGetProperty("Value", out var v) ? v.GetString() ?? "" : "";
                var ttl = item.TryGetProperty("TTL", out var ttlEl) && ttlEl.TryGetInt32(out var seconds) ? seconds : 0;
                if (name == "@") name = zone;
                else if (!name.EndsWith(zone, StringComparison.OrdinalIgnoreCase)) name = $"{name}.{zone}";
                records.Add(new RemoteDnsRecord($"{name}|{type}|{value}", zone, name, type, value, ttl));
            }
            return new DnsProviderResult(true, $"Read {records.Count} record(s) from Windows DNS zone {zone}.", records);
        }
        catch (Exception ex)
        {
            return DnsProviderResult.Fail($"Unable to parse DNS output: {ex.Message}");
        }
    }
}
