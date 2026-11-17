namespace IPManager.Core;

public enum IpStatus
{
    Available = 0,
    Assigned = 1,
    Reserved = 2,
    Excluded = 3,
    Gateway = 4,
    Conflict = 5,
    Unknown = 6
}

public enum AllocationType
{
    Unknown = 0,
    Static = 1,
    Dynamic = 2,
    Reserved = 3,
    Infrastructure = 4
}

public enum DeviceCategory
{
    Unknown = 0,
    Desktop = 1,
    Laptop = 2,
    Server = 3,
    Router = 4,
    Switch = 5,
    AccessPoint = 6,
    Printer = 7,
    Nas = 8,
    Smartphone = 9,
    Tablet = 10,
    SmartTv = 11,
    IpCamera = 12,
    Iot = 13,
    GamingConsole = 14,
    VirtualMachine = 15
}

public enum DeviceStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public enum AlertType
{
    NewDevice = 0,
    UnknownDevice = 1,
    DeviceOffline = 2,
    DeviceOnline = 3,
    IpAddressChanged = 4,
    MacAddressChanged = 5,
    IpConflict = 6,
    DnsResolutionFailure = 7,
    GatewayUnavailable = 8,
    SubnetUtilization = 9,
    DhcpExhaustion = 10,
    PortUnavailable = 11,
    ScanFailed = 12
}

public enum ConflictState
{
    Suspected = 0,
    Confirmed = 1,
    Resolved = 2
}

public enum ConflictMethod
{
    MultipleMacObservations = 0,
    ArpChange = 1,
    DuplicateDatabaseAssignment = 2,
    DuplicateReservation = 3,
    ManualReport = 4,
    StaticConflict = 5
}

public enum DiscoveryMethod
{
    Arp = 0,
    NeighborTable = 1,
    Icmp = 2,
    Tcp = 3,
    Dns = 4,
    Mdns = 5,
    NetBios = 6,
    Manual = 7
}

public enum InterfaceKind
{
    Other = 0,
    Ethernet = 1,
    WiFi = 2,
    Loopback = 3,
    Virtual = 4
}

public enum MonitoringMethod
{
    Icmp = 0,
    Tcp = 1,
    Http = 2,
    Dns = 3
}

public enum DhcpProviderKind
{
    LocalDocumentation = 0,
    WindowsDhcpClient = 1,
    DnsmasqFiles = 2,
    IscDhcpFiles = 3,
    KeaDhcp = 4,
    WindowsServerDhcp = 5,
    UniFi = 6,
    MikroTik = 7
}

public enum DnsProviderKind
{
    SystemResolver = 0,
    LocalDocumentation = 1,
    WindowsDnsServer = 2,
    PiHole = 3,
    AdGuardHome = 4,
    Dnsmasq = 5,
    Cloudflare = 6
}

public enum IntegrationState
{
    NotConfigured = 0,
    Configured = 1,
    Connected = 2,
    Unavailable = 3,
    Error = 4
}

public enum ThemeMode
{
    Dark = 0,
    Light = 1
}

public enum ScanStatus
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3
}

public enum NotificationChannel
{
    InApp = 0,
    WindowsToast = 1,
    Email = 2,
    Telegram = 3
}
