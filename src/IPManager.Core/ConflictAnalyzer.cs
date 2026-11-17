using System.Text;

namespace IPManager.Core;

public sealed record ConflictFinding
{
    public required string IpAddress { get; init; }
    public required IReadOnlyList<string> MacAddresses { get; init; }
    public required ConflictMethod Method { get; init; }
    public required ConflictState State { get; init; }
    public required int Confidence { get; init; }
    public required string Evidence { get; init; }
    public int? SubnetId { get; init; }
    public DateTime LastSeen { get; init; }
}

public sealed class ConflictAnalyzerOptions
{
    /// <summary>An observation is fresh when it was seen inside this window.</summary>
    public TimeSpan FreshWindow { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Observations older than this are treated as stale ARP leftovers.</summary>
    public TimeSpan StaleWindow { get; set; } = TimeSpan.FromHours(6);
    public int MinObservationsForConfirmation { get; set; } = 2;
    /// <summary>Minimum number of independent detection methods required for a confirmed conflict.</summary>
    public int MinSourcesForConfirmation { get; set; } = 2;
}

/// <summary>
/// Conflict analysis. Two distinct conditions are kept apart:
///  - network observations (one IP answering with several MAC addresses)
///  - documentation duplicates (the same address assigned twice inside IP Manager)
/// Stale ARP entries never produce a confirmed conflict.
/// </summary>
public static class ConflictAnalyzer
{
    public static IReadOnlyList<ConflictFinding> AnalyzeNetwork(
        IEnumerable<MacObservation> observations,
        DateTime nowUtc,
        ConflictAnalyzerOptions? options = null)
    {
        var o = options ?? new ConflictAnalyzerOptions();
        var findings = new List<ConflictFinding>();

        var groups = observations
            .Where(x => !string.IsNullOrWhiteSpace(x.IpAddress) && !string.IsNullOrWhiteSpace(x.MacAddress))
            .GroupBy(x => x.IpAddress.Trim());

        foreach (var group in groups)
        {
            var macs = group
                .Where(x => IsUsableMac(x.MacAddress))
                .GroupBy(x => MacUtil.Normalize(x.MacAddress)!)
                .Select(g => new
                {
                    Mac = g.Key,
                    LastSeen = g.Max(x => x.LastSeen),
                    FirstSeen = g.Min(x => x.FirstSeen),
                    Count = g.Sum(x => x.ObservationCount),
                    Sources = g.Select(x => x.Source ?? "Unknown").Distinct().ToList(),
                    SubnetId = g.Select(x => x.SubnetId).FirstOrDefault(v => v.HasValue)
                })
                .OrderByDescending(x => x.LastSeen)
                .ToList();

            if (macs.Count < 2) continue;

            var fresh = macs.Where(m => nowUtc - m.LastSeen <= o.FreshWindow).ToList();
            var stale = macs.Where(m => nowUtc - m.LastSeen > o.StaleWindow).ToList();
            var allSources = macs.SelectMany(m => m.Sources).Distinct().Count();
            var totalObservations = macs.Sum(m => m.Count);

            var state = ConflictState.Suspected;
            var confidence = 40;

            if (fresh.Count >= 2)
            {
                if (allSources >= o.MinSourcesForConfirmation && totalObservations >= o.MinObservationsForConfirmation)
                {
                    state = ConflictState.Confirmed;
                    confidence = 95;
                }
                else if (totalObservations >= o.MinObservationsForConfirmation)
                {
                    state = ConflictState.Suspected;
                    confidence = 70;
                }
                else
                {
                    confidence = 55;
                }
            }
            else if (stale.Count > 0)
            {
                confidence = 25;
            }

            var evidence = new StringBuilder();
            evidence.AppendLine($"IP {group.Key} was observed with {macs.Count} different MAC addresses.");
            foreach (var m in macs)
            {
                evidence.AppendLine(
                    $"  {m.Mac}  first {m.FirstSeen:yyyy-MM-dd HH:mm:ss}Z  last {m.LastSeen:yyyy-MM-dd HH:mm:ss}Z  observations {m.Count}  source {string.Join(", ", m.Sources)}");
            }
            if (stale.Count > 0)
                evidence.AppendLine("Stale entries are present; a conflict is not confirmed from stale ARP data alone.");
            if (state == ConflictState.Confirmed)
                evidence.AppendLine($"Classification: confirmed (fresh MACs: {fresh.Count}, independent methods: {allSources}, observations: {totalObservations}).");
            else
                evidence.AppendLine($"Classification: suspected (fresh MACs: {fresh.Count}, independent methods: {allSources}).");

            findings.Add(new ConflictFinding
            {
                IpAddress = group.Key,
                MacAddresses = macs.Select(m => m.Mac).ToList(),
                Method = ConflictMethod.MultipleMacObservations,
                State = state,
                Confidence = confidence,
                Evidence = evidence.ToString().TrimEnd(),
                LastSeen = macs.Max(m => m.LastSeen),
                SubnetId = macs.Select(m => m.SubnetId).FirstOrDefault(v => v.HasValue)
            });
        }

        return findings;
    }

    /// <summary>Duplicate address assignments inside the application database.</summary>
    public static IReadOnlyList<ConflictFinding> AnalyzeDatabase(IEnumerable<IpAddressEntry> entries)
    {
        var findings = new List<ConflictFinding>();
        var groups = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Address))
            .Where(e => e.Status is IpStatus.Assigned or IpStatus.Reserved or IpStatus.Gateway or IpStatus.Conflict)
            .GroupBy(e => e.Address.Trim());

        foreach (var group in groups)
        {
            var list = group.ToList();
            if (list.Count < 2) continue;

            var owners = list.Select(e =>
                $"{e.Address} status={e.Status} device={(e.Device?.Name ?? e.Hostname ?? "unassigned")} mac={e.MacAddress ?? "-"} record#{e.Id}")
                .ToList();

            findings.Add(new ConflictFinding
            {
                IpAddress = group.Key,
                MacAddresses = list.Select(e => e.MacAddress).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => MacUtil.Normalize(m!)!).Distinct().ToList(),
                Method = ConflictMethod.DuplicateDatabaseAssignment,
                State = ConflictState.Suspected,
                Confidence = 100,
                Evidence = "The address is assigned to more than one record in IP Manager (documentation duplicate, not a live network conflict):\n  " +
                           string.Join("\n  ", owners),
                SubnetId = list.Select(e => e.SubnetId).FirstOrDefault(v => v.HasValue),
                LastSeen = list.Max(e => e.UpdatedAt)
            });
        }

        return findings;
    }

    private static bool IsUsableMac(string? mac)
    {
        var normalized = MacUtil.Normalize(mac);
        if (normalized is null) return false;
        if (normalized == "00:00:00:00:00:00" || normalized == "FF:FF:FF:FF:FF:FF") return false;
        return true;
    }
}

public static class MacUtil
{
    public static string? Normalize(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    public static bool IsValid(string? mac) => Normalize(mac) is not null;

    /// <summary>Locally administered (randomized) MAC addresses have bit 1 of the first octet set.</summary>
    public static bool IsLocallyAdministered(string? mac)
    {
        var n = Normalize(mac);
        if (n is null) return false;
        var first = Convert.ToByte(n[..2], 16);
        return (first & 0x02) != 0;
    }

    public static bool IsMulticast(string? mac)
    {
        var n = Normalize(mac);
        if (n is null) return false;
        var first = Convert.ToByte(n[..2], 16);
        return (first & 0x01) != 0;
    }

    public static string? OuiPrefix(string? mac)
    {
        var n = Normalize(mac);
        return n is null ? null : n[..8].Replace(":", "");
    }
}
