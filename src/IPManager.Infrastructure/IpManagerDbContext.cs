using IPManager.Core;
using Microsoft.EntityFrameworkCore;

namespace IPManager.Infrastructure;

public class IpManagerDbContext : DbContext
{
    public IpManagerDbContext(DbContextOptions<IpManagerDbContext> options) : base(options) { }

    public DbSet<NetworkInterfaceRecord> NetworkInterfaces => Set<NetworkInterfaceRecord>();
    public DbSet<Subnet> Subnets => Set<Subnet>();
    public DbSet<IpAddressEntry> IpAddresses => Set<IpAddressEntry>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceHistory> DeviceHistory => Set<DeviceHistory>();
    public DbSet<MacObservation> MacObservations => Set<MacObservation>();
    public DbSet<IpConflict> IpConflicts => Set<IpConflict>();
    public DbSet<DhcpScope> DhcpScopes => Set<DhcpScope>();
    public DbSet<DhcpReservation> DhcpReservations => Set<DhcpReservation>();
    public DbSet<DnsRecordDoc> DnsRecords => Set<DnsRecordDoc>();
    public DbSet<DnsQueryLog> DnsQueryLogs => Set<DnsQueryLog>();
    public DbSet<MonitoringTarget> MonitoringTargets => Set<MonitoringTarget>();
    public DbSet<MonitoringResult> MonitoringResults => Set<MonitoringResult>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<TopologyLayout> TopologyLayouts => Set<TopologyLayout>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<IntegrationConfig> Integrations => Set<IntegrationConfig>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ScanSession> ScanSessions => Set<ScanSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Subnet>(e =>
        {
            e.HasIndex(x => new { x.NetworkAddress, x.PrefixLength }).IsUnique();
            e.HasMany(x => x.Addresses).WithOne(x => x.Subnet!).HasForeignKey(x => x.SubnetId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Device>(e =>
        {
            e.HasIndex(x => x.MacAddress);
            e.HasIndex(x => x.IpAddress);
            e.HasIndex(x => x.Name);
            e.HasOne(x => x.Subnet).WithMany().HasForeignKey(x => x.SubnetId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.History).WithOne(x => x.Device!).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.MonitoringTargets).WithOne(x => x.Device!).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IpAddressEntry>(e =>
        {
            e.HasIndex(x => x.Address);
            e.HasIndex(x => new { x.SubnetId, x.Address });
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MacObservation>(e =>
        {
            e.HasIndex(x => x.IpAddress);
            e.HasIndex(x => new { x.IpAddress, x.MacAddress }).IsUnique();
        });

        b.Entity<IpConflict>(e =>
        {
            e.HasIndex(x => x.IpAddress);
            e.HasIndex(x => x.State);
        });

        b.Entity<MonitoringResult>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.Timestamp });
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Alert>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.IsAcknowledged);
        });

        b.Entity<DhcpReservation>(e =>
        {
            e.HasIndex(x => new { x.DhcpScopeId, x.IpAddress });
            e.HasOne(x => x.DhcpScope).WithMany(x => x.Reservations).HasForeignKey(x => x.DhcpScopeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DnsQueryLog>(e => e.HasIndex(x => x.Timestamp));
        b.Entity<AuditEvent>(e => e.HasIndex(x => x.Timestamp));
        b.Entity<ScanSession>(e => e.HasIndex(x => x.StartedAt));

        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<IntegrationConfig>().HasIndex(x => new { x.Kind, x.Name });
        b.Entity<DeviceHistory>().HasIndex(x => new { x.DeviceId, x.ChangedAt });
        b.Entity<DnsRecordDoc>().HasIndex(x => new { x.Zone, x.Name, x.Type });
    }
}
