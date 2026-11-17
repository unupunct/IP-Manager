using System.Collections.Concurrent;
using System.Text;

namespace IPManager.Core;

/// <summary>
/// MAC OUI vendor lookup. The embedded seed list is deliberately small and only contains
/// well-known assignments; the IEEE registry can be imported for complete coverage.
/// A vendor match is a hint, not proof of the device manufacturer (MAC randomization,
/// virtual adapters and re-badged hardware are common).
/// </summary>
public sealed class OuiDatabase
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;
    public string? SourcePath { get; private set; }
    public DateTime? LoadedAt { get; private set; }

    public bool TryGetVendor(string? mac, out string vendor)
    {
        vendor = "";
        var prefix = MacUtil.OuiPrefix(mac);
        if (prefix is null) return false;
        return _entries.TryGetValue(prefix, out vendor!);
    }

    public string? Lookup(string? mac) => TryGetVendor(mac, out var v) ? v : null;

    public void Add(string prefix, string vendor)
    {
        var key = new string(prefix.Where(Uri.IsHexDigit).ToArray());
        if (key.Length != 6 || string.IsNullOrWhiteSpace(vendor)) return;
        _entries[key] = vendor.Trim();
    }

    public void LoadLines(IEnumerable<string> lines)
    {
        foreach (var line in lines) ParseLine(line);
        LoadedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Parses the IEEE MA-L/MA-M/MA-S CSV ("MA-L,28-6F-B9,Vendor,...") and simple
    /// "AA:BB:CC,Vendor" or "AABBCC Vendor" lines.
    /// </summary>
    public void ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return;
        var trimmed = line.Trim();

        if (trimmed.Contains(','))
        {
            var parts = SplitCsv(trimmed);
            if (parts.Count >= 3 && parts[1].Length >= 6 && parts[1].Any(char.IsLetterOrDigit))
            {
                Add(parts[1], parts[2]);
                return;
            }
            if (parts.Count >= 2)
            {
                Add(parts[0], parts[1]);
            }
            return;
        }

        var sep = trimmed.IndexOfAny(new[] { ' ', '\t' });
        if (sep > 0) Add(trimmed[..sep], trimmed[(sep + 1)..].Trim());
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { result.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }

    public async Task<int> LoadFromFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("OUI database file not found.", path);
        var before = _entries.Count;
        using var reader = new StreamReader(path);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            ParseLine(line);
        }
        SourcePath = path;
        LoadedAt = DateTime.UtcNow;
        return _entries.Count - before;
    }

    public async Task<string> SaveAsync(string path, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(path, false);
        await writer.WriteLineAsync("# IP Manager OUI database. Format: AABBCC,Vendor");
        foreach (var kvp in _entries.OrderBy(x => x.Key, StringComparer.Ordinal))
            await writer.WriteLineAsync($"{kvp.Key},{kvp.Value}");
        ct.ThrowIfCancellationRequested();
        return path;
    }

    public static OuiDatabase CreateWithSeed()
    {
        var db = new OuiDatabase();
        foreach (var (prefix, vendor) in Seed) db.Add(prefix, vendor);
        return db;
    }

    /// <summary>Small set of well-known IEEE assignments shipped for offline use.</summary>
    private static readonly (string Prefix, string Vendor)[] Seed =
    {
        ("005056", "VMware, Inc."),
        ("000C29", "VMware, Inc."),
        ("000569", "VMware, Inc."),
        ("00155D", "Microsoft Corporation (Hyper-V)"),
        ("080027", "PCS Systemtechnik GmbH (VirtualBox)"),
        ("001C42", "Parallels, Inc."),
        ("525400", "QEMU / KVM virtual NIC"),
        ("B827EB", "Raspberry Pi Foundation"),
        ("DCA632", "Raspberry Pi Trading Ltd"),
        ("E45F01", "Raspberry Pi Trading Ltd"),
        ("2CCF67", "Raspberry Pi Trading Ltd"),
        ("240AC4", "Espressif Inc."),
        ("840D8E", "Espressif Inc."),
        ("3C71BF", "Espressif Inc."),
        ("001788", "Philips Lighting BV (Hue)"),
        ("000420", "Sonos, Inc."),
        ("18B430", "Nest Labs Inc."),
        ("001B63", "Apple, Inc."),
        ("3C5AB4", "Google, Inc."),
        ("F4F5D8", "Google, Inc."),
        ("44650D", "Amazon Technologies Inc."),
        ("74C246", "Amazon Technologies Inc."),
        ("F0272D", "Amazon Technologies Inc."),
        ("001A11", "Google, Inc."),
        ("0024E4", "Withings"),
        ("0090A9", "Western Digital"),
        ("001132", "Synology Incorporated"),
        ("0011D8", "ASUSTek Computer Inc."),
        ("0026B9", "Dell Inc."),
        ("3C2C30", "Dell Inc."),
        ("001E58", "D-Link Corporation"),
        ("C46E1F", "TP-Link Technologies Co., Ltd."),
        ("50C7BF", "TP-Link Technologies Co., Ltd."),
        ("A42BB0", "TP-Link Technologies Co., Ltd."),
        ("000FB5", "NETGEAR Inc."),
        ("204E7F", "NETGEAR Inc."),
        ("C40415", "NETGEAR Inc."),
        ("0018E7", "Cisco Systems, Inc."),
        ("000D93", "Cisco Systems, Inc."),
        ("F09FC2", "Ubiquiti Inc."),
        ("245A4C", "Ubiquiti Inc."),
        ("744D28", "Routerboard.com (MikroTik)"),
        ("4C5E0C", "Routerboard.com (MikroTik)"),
        ("001CC5", "3Com Corporation"),
        ("001D7E", "Cisco-Linksys LLC"),
        ("001839", "Cisco-Linksys LLC"),
        ("00248C", "ASUSTek Computer Inc."),
        ("2C56DC", "ASUSTek Computer Inc."),
        ("001BFC", "ASUSTek Computer Inc."),
        ("D850E6", "ASUSTek Computer Inc."),
        ("001636", "Apple, Inc."),
        ("0025BC", "Apple, Inc."),
        ("F01898", "Apple, Inc."),
        ("A4C361", "Apple, Inc."),
        ("8C8590", "Apple, Inc."),
        ("BC926B", "Apple, Inc."),
        ("001B21", "Intel Corporate"),
        ("3C970E", "Intel Corporate"),
        ("7CB27D", "Intel Corporate"),
        ("9C7BEF", "Hewlett Packard"),
        ("0026B0", "Hewlett Packard"),
        ("3C4A92", "Hewlett Packard"),
        ("001F29", "Hewlett Packard"),
        ("000E7F", "Hewlett Packard"),
        ("AC1826", "Seiko Epson Corporation"),
        ("0021B7", "Lexmark International Inc."),
        ("008077", "Brother Industries, Ltd."),
        ("00266C", "Canon Inc."),
        ("001E8F", "CANON INC."),
        ("0015E9", "D-Link Corporation"),
        ("0018E8", "Huawei Technologies Co., Ltd."),
        ("247F3C", "Huawei Technologies Co., Ltd."),
        ("0026E8", "Murata Manufacturing Co., Ltd."),
        ("001CF0", "D-Link Corporation"),
        ("F0EE10", "Chongqing Fugui Electronics"),
        ("001DD9", "Hon Hai Precision Ind. Co., Ltd."),
        ("3C5282", "Shenzhen YOUHUA Technology"),
        ("C0EEFB", "OnePlus Technology"),
        ("98DF82", "Hangzhou Hikvision Digital Technology"),
        ("BCAD28", "Hangzhou Hikvision Digital Technology"),
        ("00156D", "Ubiquiti Networks Inc."),
        ("44D9E7", "Ubiquiti Networks Inc."),
        ("78321B", "D-Link International"),
        ("001E06", "Wistron Corporation"),
        ("001A79", "Telecomunication Technologies"),
    };
}

/// <summary>Heuristic device classification from vendor, hostname, OS and open ports.</summary>
public static class DeviceClassifier
{
    public static DeviceCategory Guess(string? vendor, string? hostname, string? os, IReadOnlyCollection<int>? openPorts)
    {
        var v = (vendor ?? "").ToLowerInvariant();
        var h = (hostname ?? "").ToLowerInvariant();
        var o = (os ?? "").ToLowerInvariant();
        var ports = openPorts ?? Array.Empty<int>();

        bool Has(params int[] p) => p.Any(ports.Contains);

        if (v.Contains("vmware") || v.Contains("virtualbox") || v.Contains("hyper-v") || v.Contains("qemu") || v.Contains("parallels"))
            return DeviceCategory.VirtualMachine;

        if (Has(9100, 515, 631) || ContainsAny(h, "printer", "laserjet", "officejet", "epson", "brother", "canon") || ContainsAny(v, "epson", "brother", "canon", "lexmark"))
            return DeviceCategory.Printer;

        if (ContainsAny(h, "nas", "synology", "qnap", "truenas", "freenas") || ContainsAny(v, "synology", "qnap") || (Has(445) && Has(5000, 5001)))
            return DeviceCategory.Nas;

        if (ContainsAny(h, "iphone", "android", "galaxy", "pixel", "phone") || o.Contains("ios") && ContainsAny(h, "iphone"))
            return DeviceCategory.Smartphone;

        if (ContainsAny(h, "ipad", "tablet"))
            return DeviceCategory.Tablet;

        if (ContainsAny(h, "tv", "bravia", "roku", "chromecast", "firetv", "appletv", "shield") || ContainsAny(v, "roku", "vizio", "sonos"))
            return DeviceCategory.SmartTv;

        if (ContainsAny(h, "cam", "ipcam", "hikvision", "dahua", "reolink", "doorbell") || ContainsAny(v, "hikvision", "dahua", "reolink"))
            return DeviceCategory.IpCamera;

        if (ContainsAny(h, "xbox", "playstation", "ps4", "ps5", "nintendo") || ContainsAny(v, "sony interactive", "microsoft xbox"))
            return DeviceCategory.GamingConsole;

        if (ContainsAny(h, "router", "gateway", "edgerouter", "unifi", "fritz", "openwrt", "udm") ||
            (Has(80, 443) && ContainsAny(v, "routerboard", "ubiquiti", "tp-link", "netgear", "d-link", "asus")))
            return DeviceCategory.Router;

        if (ContainsAny(h, "switch", "sw-") || (Has(22) && ContainsAny(v, "cisco systems")))
            return DeviceCategory.Switch;

        if (ContainsAny(h, "ap-", "accesspoint", "access-point"))
            return DeviceCategory.AccessPoint;

        if (ContainsAny(v, "espressif", "raspberry") && !Has(22, 80, 443, 445))
            return DeviceCategory.Iot;

        if (ContainsAny(h, "esp", "tasmota", "shelly", "tuya", "hue", "smart") || ContainsAny(v, "philips lighting", "nest labs"))
            return DeviceCategory.Iot;

        if (ContainsAny(h, "laptop", "notebook") || o.Contains("laptop"))
            return DeviceCategory.Laptop;

        if (ContainsAny(h, "server", "srv", "dc0", "dc1", "host") || Has(3389, 22) && Has(80, 443, 445))
            return DeviceCategory.Server;

        if (ContainsAny(h, "desktop", "pc-", "-pc", "workstation") || o.Contains("windows"))
            return DeviceCategory.Desktop;

        return DeviceCategory.Unknown;
    }

    public static string? GuessOperatingSystem(IReadOnlyCollection<int>? openPorts, string? hostname, string? vendor)
    {
        var ports = openPorts ?? Array.Empty<int>();
        var h = (hostname ?? "").ToLowerInvariant();
        var v = (vendor ?? "").ToLowerInvariant();

        if (ports.Contains(3389) || ports.Contains(445) || ports.Contains(135)) return "Windows";
        if (ports.Contains(22) && ports.Contains(548)) return "macOS / Unix";
        if (ports.Contains(22)) return "Linux / Unix";
        if (ports.Contains(62078) || h.Contains("iphone") || h.Contains("ipad")) return "iOS / iPadOS";
        if (ports.Contains(5555) || h.Contains("android")) return "Android";
        if (v.Contains("raspberry")) return "Linux (Raspberry Pi)";
        return null;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        !string.IsNullOrEmpty(text) && needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(DeviceCategory category) => category switch
    {
        DeviceCategory.Desktop => "Desktop computer",
        DeviceCategory.Laptop => "Laptop",
        DeviceCategory.Server => "Server",
        DeviceCategory.Router => "Router",
        DeviceCategory.Switch => "Switch",
        DeviceCategory.AccessPoint => "Access point",
        DeviceCategory.Printer => "Printer",
        DeviceCategory.Nas => "NAS",
        DeviceCategory.Smartphone => "Smartphone",
        DeviceCategory.Tablet => "Tablet",
        DeviceCategory.SmartTv => "Smart TV",
        DeviceCategory.IpCamera => "IP camera",
        DeviceCategory.Iot => "IoT device",
        DeviceCategory.GamingConsole => "Gaming console",
        DeviceCategory.VirtualMachine => "Virtual machine",
        _ => "Unknown device"
    };
}
