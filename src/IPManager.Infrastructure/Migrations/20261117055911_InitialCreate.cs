using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IPManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EntityId = table.Column<int>(type: "INTEGER", nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRecovered = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecoveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    CooldownKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    User = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DnsQueryLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueryName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RecordType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Response = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Ttl = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<double>(type: "REAL", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsQueryLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DnsRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Zone = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Ttl = table.Column<int>(type: "INTEGER", nullable: false),
                    Server = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SecretProtected = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IpConflicts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MacAddresses = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MacObservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ObservationCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NetworkInterfaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Ipv4Address = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Ipv6Address = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SubnetMask = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PrefixLength = table.Column<int>(type: "INTEGER", nullable: true),
                    Gateway = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DnsServers = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SpeedMbps = table.Column<long>(type: "INTEGER", nullable: true),
                    IsUp = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSelected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkInterfaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Targets = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Methods = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AddressesChecked = table.Column<int>(type: "INTEGER", nullable: false),
                    DevicesFound = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Subnets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NetworkAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrefixLength = table.Column<int>(type: "INTEGER", nullable: false),
                    Gateway = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DnsServers = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    VlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    DhcpStart = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DhcpEnd = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StaticStart = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StaticEnd = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExcludedAddresses = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    MonitoringEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAutoDiscovered = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subnets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TopologyLayouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", maxLength: 200000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopologyLayouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Manufacturer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DnsName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    InterfaceName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsTrusted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsImportant = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExcludeFromMonitoring = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsManuallyManaged = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DiscoverySource = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OpenPorts = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LastSeenOnline = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Subnets_SubnetId",
                        column: x => x.SubnetId,
                        principalTable: "Subnets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DhcpScopes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: true),
                    ServerAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EndAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LeaseMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ExcludedAddresses = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DhcpScopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DhcpScopes_Subnets_SubnetId",
                        column: x => x.SubnetId,
                        principalTable: "Subnets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DeviceHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceHistory_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IpAddresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubnetId = table.Column<int>(type: "INTEGER", nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: true),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AllocationType = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    IsManuallyManaged = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IpAddresses_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IpAddresses_Subnets_SubnetId",
                        column: x => x.SubnetId,
                        principalTable: "Subnets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResponseMs = table.Column<double>(type: "REAL", nullable: true),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringResults_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringTargets_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DhcpReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DhcpScopeId = table.Column<int>(type: "INTEGER", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MacAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DhcpReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DhcpReservations_DhcpScopes_DhcpScopeId",
                        column: x => x.DhcpScopeId,
                        principalTable: "DhcpScopes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_CreatedAt",
                table: "Alerts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_IsAcknowledged",
                table: "Alerts",
                column: "IsAcknowledged");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Timestamp",
                table: "AuditEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHistory_DeviceId_ChangedAt",
                table: "DeviceHistory",
                columns: new[] { "DeviceId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_IpAddress",
                table: "Devices",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_MacAddress",
                table: "Devices",
                column: "MacAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Name",
                table: "Devices",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SubnetId",
                table: "Devices",
                column: "SubnetId");

            migrationBuilder.CreateIndex(
                name: "IX_DhcpReservations_DhcpScopeId_IpAddress",
                table: "DhcpReservations",
                columns: new[] { "DhcpScopeId", "IpAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_DhcpScopes_SubnetId",
                table: "DhcpScopes",
                column: "SubnetId");

            migrationBuilder.CreateIndex(
                name: "IX_DnsQueryLogs_Timestamp",
                table: "DnsQueryLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_DnsRecords_Zone_Name_Type",
                table: "DnsRecords",
                columns: new[] { "Zone", "Name", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_Kind_Name",
                table: "Integrations",
                columns: new[] { "Kind", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_IpAddresses_Address",
                table: "IpAddresses",
                column: "Address");

            migrationBuilder.CreateIndex(
                name: "IX_IpAddresses_DeviceId",
                table: "IpAddresses",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_IpAddresses_SubnetId_Address",
                table: "IpAddresses",
                columns: new[] { "SubnetId", "Address" });

            migrationBuilder.CreateIndex(
                name: "IX_IpConflicts_IpAddress",
                table: "IpConflicts",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_IpConflicts_State",
                table: "IpConflicts",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_MacObservations_IpAddress",
                table: "MacObservations",
                column: "IpAddress");

            migrationBuilder.CreateIndex(
                name: "IX_MacObservations_IpAddress_MacAddress",
                table: "MacObservations",
                columns: new[] { "IpAddress", "MacAddress" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringResults_DeviceId_Timestamp",
                table: "MonitoringResults",
                columns: new[] { "DeviceId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTargets_DeviceId",
                table: "MonitoringTargets",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanSessions_StartedAt",
                table: "ScanSessions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Subnets_NetworkAddress_PrefixLength",
                table: "Subnets",
                columns: new[] { "NetworkAddress", "PrefixLength" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "DeviceHistory");

            migrationBuilder.DropTable(
                name: "DhcpReservations");

            migrationBuilder.DropTable(
                name: "DnsQueryLogs");

            migrationBuilder.DropTable(
                name: "DnsRecords");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropTable(
                name: "IpAddresses");

            migrationBuilder.DropTable(
                name: "IpConflicts");

            migrationBuilder.DropTable(
                name: "MacObservations");

            migrationBuilder.DropTable(
                name: "MonitoringResults");

            migrationBuilder.DropTable(
                name: "MonitoringTargets");

            migrationBuilder.DropTable(
                name: "NetworkInterfaces");

            migrationBuilder.DropTable(
                name: "ScanSessions");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TopologyLayouts");

            migrationBuilder.DropTable(
                name: "DhcpScopes");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Subnets");
        }
    }
}
