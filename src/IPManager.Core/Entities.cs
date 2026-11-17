using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPManager.Core;

public class NetworkInterfaceRecord
{
    public int Id { get; set; }
    [MaxLength(128)] public string Name { get; set; } = "";
    [MaxLength(256)] public string? Description { get; set; }
    [MaxLength(64)] public string? MacAddress { get; set; }
    [MaxLength(64)] public string? Ipv4Address { get; set; }
    [MaxLength(64)] public string? Ipv6Address { get; set; }
    [MaxLength(64)] public string? SubnetMask { get; set; }
    public int? PrefixLength { get; set; }
    [MaxLength(64)] public string? Gateway { get; set; }
    [MaxLength(256)] public string? DnsServers { get; set; }
    public long? SpeedMbps { get; set; }
    public bool IsUp { get; set; }
    public InterfaceKind Kind { get; set; }
    public bool IsSelected { get; set; }
    public DateTime CapturedAt { get; set; }
}

public class Subnet
{
    public int Id { get; set; }
    [MaxLength(128)] public string Name { get; set; } = "";
    [MaxLength(64)] public string NetworkAddress { get; set; } = "";
    public int PrefixLength { get; set; }
    [MaxLength(64)] public string? Gateway { get; set; }
    [MaxLength(512)] public string? DnsServers { get; set; }
    public int? VlanId { get; set; }
    [MaxLength(128)] public string? Location { get; set; }
    [MaxLength(1024)] public string? Description { get; set; }
    [MaxLength(64)] public string? DhcpStart { get; set; }
    [MaxLength(64)] public string? DhcpEnd { get; set; }
    [MaxLength(64)] public string? StaticStart { get; set; }
    [MaxLength(64)] public string? StaticEnd { get; set; }
    [MaxLength(1024)] public string? ExcludedAddresses { get; set; }
    public bool MonitoringEnabled { get; set; } = true;
    public bool IsAutoDiscovered { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [NotMapped] public string Cidr => $"{NetworkAddress}/{PrefixLength}";

    [NotMapped]
    public Ipv4Network? NetworkOrNull
    {
        get
        {
            try { return Ipv4Network.Parse(NetworkAddress, PrefixLength); }
            catch { return null; }
        }
    }

    public List<IpAddressEntry> Addresses { get; set; } = new();
}

public class IpAddressEntry
{
    public int Id { get; set; }
    public int? SubnetId { get; set; }
    public Subnet? Subnet { get; set; }
    [MaxLength(64)] public string Address { get; set; } = "";
    public int? DeviceId { get; set; }
    public Device? Device { get; set; }
    [MaxLength(64)] public string? MacAddress { get; set; }
    [MaxLength(256)] public string? Hostname { get; set; }
    public AllocationType AllocationType { get; set; }
    public IpStatus Status { get; set; }
    [MaxLength(512)] public string? Description { get; set; }
    [MaxLength(2048)] public string? Notes { get; set; }
    public bool IsManuallyManaged { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Device
{
    public int Id { get; set; }
    [MaxLength(128)] public string Name { get; set; } = "";
    [MaxLength(64)] public string? IpAddress { get; set; }
    [MaxLength(64)] public string? MacAddress { get; set; }
    [MaxLength(256)] public string? Manufacturer { get; set; }
    [MaxLength(256)] public string? Vendor { get; set; }
    public DeviceCategory Category { get; set; }
    [MaxLength(256)] public string? OperatingSystem { get; set; }
    [MaxLength(256)] public string? Hostname { get; set; }
    [MaxLength(256)] public string? DnsName { get; set; }
    [MaxLength(256)] public string? InterfaceName { get; set; }
    public int? SubnetId { get; set; }
    public Subnet? Subnet { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public DeviceStatus Status { get; set; }
    public bool IsTrusted { get; set; }
    public bool IsImportant { get; set; }
    public bool ExcludeFromMonitoring { get; set; }
    public bool IsManuallyManaged { get; set; }
    [MaxLength(2048)] public string? Notes { get; set; }
    [MaxLength(512)] public string? Tags { get; set; }
    [MaxLength(128)] public string? Location { get; set; }
    [MaxLength(128)] public string? DiscoverySource { get; set; }
    [MaxLength(256)] public string? OpenPorts { get; set; }
    public DateTime? LastSeenOnline { get; set; }

    public List<DeviceHistory> History { get; set; } = new();
    public List<MonitoringTarget> MonitoringTargets { get; set; } = new();
}

public class DeviceHistory
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
    [MaxLength(64)] public string Field { get; set; } = "";
    [MaxLength(512)] public string? OldValue { get; set; }
    [MaxLength(512)] public string? NewValue { get; set; }
    public DateTime ChangedAt { get; set; }
    [MaxLength(64)] public string? Source { get; set; }
    [MaxLength(512)] public string? Note { get; set; }
}

/// <summary>An IP/MAC pair seen on the wire. Used for conflict evidence and history.</summary>
public class MacObservation
{
    public int Id { get; set; }
    [MaxLength(64)] public string IpAddress { get; set; } = "";
    [MaxLength(64)] public string MacAddress { get; set; } = "";
    public int? SubnetId { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    [MaxLength(64)] public string? Source { get; set; }
    public int ObservationCount { get; set; } = 1;
}

public class IpConflict
{
    public int Id { get; set; }
    [MaxLength(64)] public string IpAddress { get; set; } = "";
    [MaxLength(512)] public string MacAddresses { get; set; } = "";
    public ConflictMethod Method { get; set; }
    public ConflictState State { get; set; }
    public int Confidence { get; set; }
    [MaxLength(2048)] public string Evidence { get; set; } = "";
    public DateTime DetectedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    [MaxLength(2048)] public string? Notes { get; set; }
    public int? SubnetId { get; set; }
}

public class DhcpScope
{
    public int Id { get; set; }
    [MaxLength(128)] public string Name { get; set; } = "";
    public int? SubnetId { get; set; }
    public Subnet? Subnet { get; set; }
    [MaxLength(64)] public string? ServerAddress { get; set; }
    [MaxLength(64)] public string Provider { get; set; } = DhcpProviderKind.LocalDocumentation.ToString();
    [MaxLength(64)] public string StartAddress { get; set; } = "";
    [MaxLength(64)] public string EndAddress { get; set; } = "";
    public int LeaseMinutes { get; set; } = 1440;
    [MaxLength(1024)] public string? ExcludedAddresses { get; set; }
    [MaxLength(2048)] public string? Notes { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<DhcpReservation> Reservations { get; set; } = new();
    [NotMapped] public string Range => $"{StartAddress} - {EndAddress}";
}

public class DhcpReservation
{
    public int Id { get; set; }
    public int DhcpScopeId { get; set; }
    public DhcpScope? DhcpScope { get; set; }
    [MaxLength(64)] public string IpAddress { get; set; } = "";
    [MaxLength(64)] public string MacAddress { get; set; } = "";
    [MaxLength(256)] public string? Hostname { get; set; }
    [MaxLength(512)] public string? Description { get; set; }
    [MaxLength(64)] public string Source { get; set; } = "Manual";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class DnsRecordDoc
{
    public int Id { get; set; }
    [MaxLength(256)] public string Zone { get; set; } = "";
    [MaxLength(256)] public string Name { get; set; } = "";
    [MaxLength(16)] public string Type { get; set; } = "A";
    [MaxLength(1024)] public string Value { get; set; } = "";
    public int Ttl { get; set; } = 3600;
    [MaxLength(256)] public string? Server { get; set; }
    [MaxLength(1024)] public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class DnsQueryLog
{
    public int Id { get; set; }
    [MaxLength(256)] public string QueryName { get; set; } = "";
    [MaxLength(16)] public string RecordType { get; set; } = "A";
    [MaxLength(256)] public string Server { get; set; } = "";
    [MaxLength(2048)] public string? Response { get; set; }
    public int? Ttl { get; set; }
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public DateTime Timestamp { get; set; }
}

public class MonitoringTarget
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
    public MonitoringMethod Method { get; set; }
    public int? Port { get; set; }
    [MaxLength(512)] public string? Url { get; set; }
    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutMs { get; set; } = 2000;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class MonitoringResult
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
    public int? TargetId { get; set; }
    public DateTime Timestamp { get; set; }
    public bool Success { get; set; }
    public double? ResponseMs { get; set; }
    public MonitoringMethod Method { get; set; }
    [MaxLength(512)] public string? Detail { get; set; }
}

public class Alert
{
    public int Id { get; set; }
    public AlertType Type { get; set; }
    public AlertSeverity Severity { get; set; }
    [MaxLength(256)] public string Title { get; set; } = "";
    [MaxLength(2048)] public string Message { get; set; } = "";
    [MaxLength(64)] public string? EntityType { get; set; }
    public int? EntityId { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsAcknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public bool IsRecovered { get; set; }
    public DateTime? RecoveredAt { get; set; }
    public bool IsRead { get; set; }
    [MaxLength(256)] public string? CooldownKey { get; set; }
}

public class TopologyLayout
{
    public int Id { get; set; }
    [MaxLength(128)] public string Name { get; set; } = "Default";
    public int? SubnetId { get; set; }
    public bool IsDefault { get; set; }
    [MaxLength(200000)] public string Json { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AppSetting
{
    [MaxLength(128)] public string Key { get; set; } = "";
    [MaxLength(4096)] public string Value { get; set; } = "";
}

public class IntegrationConfig
{
    public int Id { get; set; }
    [MaxLength(64)] public string Kind { get; set; } = "";
    [MaxLength(128)] public string Name { get; set; } = "";
    [MaxLength(512)] public string? Endpoint { get; set; }
    [MaxLength(256)] public string? Username { get; set; }
    /// <summary>DPAPI-protected secret, base64. Never stored in plain text.</summary>
    [MaxLength(4096)] public string? SecretProtected { get; set; }
    public bool Enabled { get; set; }
    [MaxLength(2048)] public string? OptionsJson { get; set; }
    public IntegrationState State { get; set; }
    [MaxLength(1024)] public string? LastError { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AuditEvent
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    [MaxLength(64)] public string Category { get; set; } = "";
    [MaxLength(128)] public string Action { get; set; } = "";
    [MaxLength(2048)] public string? Detail { get; set; }
    [MaxLength(128)] public string? User { get; set; }
}

public class ScanSession
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    [MaxLength(1024)] public string Targets { get; set; } = "";
    [MaxLength(256)] public string Methods { get; set; } = "";
    public int AddressesChecked { get; set; }
    public int DevicesFound { get; set; }
    public int ErrorCount { get; set; }
    public ScanStatus Status { get; set; }
    [MaxLength(4096)] public string? ErrorSummary { get; set; }
}
