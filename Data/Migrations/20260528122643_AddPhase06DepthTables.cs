using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase06DepthTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: false),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    AssetType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CommissionedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecommissionedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Specification = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                    ParentAssetId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assets_Assets_ParentAssetId",
                        column: x => x.ParentAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CallLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CallReference = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    CallerPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HandledByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationSeconds = table.Column<int>(type: "int", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RelatedTicketId = table.Column<int>(type: "int", nullable: true),
                    RelatedOutageId = table.Column<int>(type: "int", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CallLogs_AspNetUsers_HandledByUserId",
                        column: x => x.HandledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CallLogs_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CallLogs_Outages_RelatedOutageId",
                        column: x => x.RelatedOutageId,
                        principalTable: "Outages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CallLogs_Tickets_RelatedTicketId",
                        column: x => x.RelatedTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Currencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    IsBase = table.Column<bool>(type: "bit", nullable: false),
                    ExchangeRateToBase = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    LastRateUpdate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currencies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsSubsidyEligible = table.Column<bool>(type: "bit", nullable: false),
                    DefaultPriorityFloor = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerSegments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMethods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsDigital = table.Column<bool>(type: "bit", nullable: false),
                    FeePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServicePoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PointCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    AddressLineEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AddressLineAr = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MeterNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(10,6)", precision: 10, scale: 6, nullable: true),
                    PointType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    InstalledAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServicePoints_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TariffTiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TariffId = table.Column<int>(type: "int", nullable: false),
                    TierNumber = table.Column<int>(type: "int", nullable: false),
                    FromUnit = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    ToUnit = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    RatePerUnit = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    LabelEn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    LabelAr = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TariffTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TariffTiers_Tariffs_TariffId",
                        column: x => x.TariffId,
                        principalTable: "Tariffs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Technicians",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FullNameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    FullNameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    PrimaryRegionId = table.Column<int>(type: "int", nullable: true),
                    Specialty = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    YearsOfExperience = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    HiredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Technicians", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Technicians_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Technicians_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Technicians_Regions_PrimaryRegionId",
                        column: x => x.PrimaryRegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScheduleNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AssetId = table.Column<int>(type: "int", nullable: true),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    ScheduledStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ScheduledEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActualStart = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualEnd = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    MaintenanceType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TitleEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TitleAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ExpectedAffectedCustomers = table.Column<int>(type: "int", nullable: true),
                    CustomersNotified = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceSchedules_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceSchedules_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceSchedules_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PolicyCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CustomerSegmentId = table.Column<int>(type: "int", nullable: true),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: true),
                    PriorityId = table.Column<int>(type: "int", nullable: true),
                    FirstResponseMinutes = table.Column<int>(type: "int", nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "int", nullable: false),
                    BusinessHoursOnly = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_CustomerSegments_CustomerSegmentId",
                        column: x => x.CustomerSegmentId,
                        principalTable: "CustomerSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_TicketPriorities_PriorityId",
                        column: x => x.PriorityId,
                        principalTable: "TicketPriorities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subsidies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BillId = table.Column<int>(type: "int", nullable: true),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    CustomerSegmentId = table.Column<int>(type: "int", nullable: true),
                    ProgramCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProgramNameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    ProgramNameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    AppliedPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthorizedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subsidies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subsidies_AspNetUsers_AuthorizedByUserId",
                        column: x => x.AuthorizedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subsidies_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subsidies_CustomerSegments_CustomerSegmentId",
                        column: x => x.CustomerSegmentId,
                        principalTable: "CustomerSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subsidies_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: false),
                    ServicePointId = table.Column<int>(type: "int", nullable: false),
                    CustomerSegmentId = table.Column<int>(type: "int", nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ContractedCapacity = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    CapacityUnit = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceAccounts_CustomerSegments_CustomerSegmentId",
                        column: x => x.CustomerSegmentId,
                        principalTable: "CustomerSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceAccounts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceAccounts_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceAccounts_ServicePoints_ServicePointId",
                        column: x => x.ServicePointId,
                        principalTable: "ServicePoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServiceAccounts_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OrderType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TicketId = table.Column<int>(type: "int", nullable: true),
                    OutageId = table.Column<int>(type: "int", nullable: true),
                    AssetId = table.Column<int>(type: "int", nullable: true),
                    ServicePointId = table.Column<int>(type: "int", nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    AssignedTechnicianId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DispatchedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ArrivedOnSiteAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TitleEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TitleAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RequiredSecondVisit = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Outages_OutageId",
                        column: x => x.OutageId,
                        principalTable: "Outages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_ServicePoints_ServicePointId",
                        column: x => x.ServicePointId,
                        principalTable: "ServicePoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Technicians_AssignedTechnicianId",
                        column: x => x.AssignedTechnicianId,
                        principalTable: "Technicians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutageNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OutageId = table.Column<int>(type: "int", nullable: true),
                    MaintenanceScheduleId = table.Column<int>(type: "int", nullable: true),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    ServiceAccountId = table.Column<int>(type: "int", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SentToPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SentToEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MessageEn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MessageAr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutageNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutageNotifications_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutageNotifications_MaintenanceSchedules_MaintenanceScheduleId",
                        column: x => x.MaintenanceScheduleId,
                        principalTable: "MaintenanceSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutageNotifications_Outages_OutageId",
                        column: x => x.OutageId,
                        principalTable: "Outages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutageNotifications_ServiceAccounts_ServiceAccountId",
                        column: x => x.ServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PaymentReference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BillId = table.Column<int>(type: "int", nullable: false),
                    ServiceAccountId = table.Column<int>(type: "int", nullable: true),
                    PaymentMethodId = table.Column<int>(type: "int", nullable: true),
                    CurrencyId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    ExchangeRateToBase = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    AmountInBase = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExternalTransactionId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ReceivedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_AspNetUsers_ReceivedByUserId",
                        column: x => x.ReceivedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Currencies_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_ServiceAccounts_ServiceAccountId",
                        column: x => x.ServiceAccountId,
                        principalTable: "ServiceAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_AssetCode",
                table: "Assets",
                column: "AssetCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_DepartmentId",
                table: "Assets",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_ParentAssetId",
                table: "Assets",
                column: "ParentAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_RegionId",
                table: "Assets",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_ServiceTypeId_AssetType",
                table: "Assets",
                columns: new[] { "ServiceTypeId", "AssetType" });

            migrationBuilder.CreateIndex(
                name: "IX_CallLogs_CallReference",
                table: "CallLogs",
                column: "CallReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CallLogs_CustomerId_StartedAt",
                table: "CallLogs",
                columns: new[] { "CustomerId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CallLogs_HandledByUserId",
                table: "CallLogs",
                column: "HandledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CallLogs_RelatedOutageId",
                table: "CallLogs",
                column: "RelatedOutageId");

            migrationBuilder.CreateIndex(
                name: "IX_CallLogs_RelatedTicketId",
                table: "CallLogs",
                column: "RelatedTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_CallLogs_StartedAt",
                table: "CallLogs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Currencies_Code",
                table: "Currencies",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSegments_Code",
                table: "CustomerSegments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_AssetId",
                table: "MaintenanceSchedules",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_DepartmentId",
                table: "MaintenanceSchedules",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_RegionId",
                table: "MaintenanceSchedules",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_ScheduledStart_Status",
                table: "MaintenanceSchedules",
                columns: new[] { "ScheduledStart", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceSchedules_ScheduleNumber",
                table: "MaintenanceSchedules",
                column: "ScheduleNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutageNotifications_CustomerId_SentAt",
                table: "OutageNotifications",
                columns: new[] { "CustomerId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutageNotifications_MaintenanceScheduleId",
                table: "OutageNotifications",
                column: "MaintenanceScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_OutageNotifications_OutageId",
                table: "OutageNotifications",
                column: "OutageId");

            migrationBuilder.CreateIndex(
                name: "IX_OutageNotifications_ServiceAccountId",
                table: "OutageNotifications",
                column: "ServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethods_Code",
                table: "PaymentMethods",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BillId_Status",
                table: "Payments",
                columns: new[] { "BillId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CurrencyId",
                table: "Payments",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaidAt",
                table: "Payments",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentMethodId",
                table: "Payments",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaymentReference",
                table: "Payments",
                column: "PaymentReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ReceivedByUserId",
                table: "Payments",
                column: "ReceivedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ServiceAccountId",
                table: "Payments",
                column: "ServiceAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_AccountNumber",
                table: "ServiceAccounts",
                column: "AccountNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_CustomerId_Status",
                table: "ServiceAccounts",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_CustomerSegmentId",
                table: "ServiceAccounts",
                column: "CustomerSegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_DepartmentId",
                table: "ServiceAccounts",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_ServicePointId",
                table: "ServiceAccounts",
                column: "ServicePointId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_ServiceTypeId_Status",
                table: "ServiceAccounts",
                columns: new[] { "ServiceTypeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePoints_PointCode",
                table: "ServicePoints",
                column: "PointCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServicePoints_RegionId",
                table: "ServicePoints",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_CustomerSegmentId_ServiceTypeId_PriorityId",
                table: "SlaPolicies",
                columns: new[] { "CustomerSegmentId", "ServiceTypeId", "PriorityId" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_PolicyCode",
                table: "SlaPolicies",
                column: "PolicyCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_PriorityId",
                table: "SlaPolicies",
                column: "PriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_ServiceTypeId",
                table: "SlaPolicies",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Subsidies_AuthorizedByUserId",
                table: "Subsidies",
                column: "AuthorizedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Subsidies_BillId",
                table: "Subsidies",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_Subsidies_CustomerId_IssuedAt",
                table: "Subsidies",
                columns: new[] { "CustomerId", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Subsidies_CustomerSegmentId",
                table: "Subsidies",
                column: "CustomerSegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Subsidies_ProgramCode",
                table: "Subsidies",
                column: "ProgramCode");

            migrationBuilder.CreateIndex(
                name: "IX_TariffTiers_TariffId_TierNumber",
                table: "TariffTiers",
                columns: new[] { "TariffId", "TierNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Technicians_DepartmentId_IsActive",
                table: "Technicians",
                columns: new[] { "DepartmentId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Technicians_EmployeeCode",
                table: "Technicians",
                column: "EmployeeCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Technicians_PrimaryRegionId",
                table: "Technicians",
                column: "PrimaryRegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Technicians_UserId",
                table: "Technicians",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_AssetId",
                table: "WorkOrders",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_AssignedTechnicianId_Status",
                table: "WorkOrders",
                columns: new[] { "AssignedTechnicianId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_DepartmentId",
                table: "WorkOrders",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_OrderNumber",
                table: "WorkOrders",
                column: "OrderNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_OutageId",
                table: "WorkOrders",
                column: "OutageId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_RegionId",
                table: "WorkOrders",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_ServicePointId",
                table: "WorkOrders",
                column: "ServicePointId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_Status_CreatedAt",
                table: "WorkOrders",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TicketId",
                table: "WorkOrders",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CallLogs");

            migrationBuilder.DropTable(
                name: "OutageNotifications");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "SlaPolicies");

            migrationBuilder.DropTable(
                name: "Subsidies");

            migrationBuilder.DropTable(
                name: "TariffTiers");

            migrationBuilder.DropTable(
                name: "WorkOrders");

            migrationBuilder.DropTable(
                name: "MaintenanceSchedules");

            migrationBuilder.DropTable(
                name: "Currencies");

            migrationBuilder.DropTable(
                name: "PaymentMethods");

            migrationBuilder.DropTable(
                name: "ServiceAccounts");

            migrationBuilder.DropTable(
                name: "Technicians");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "CustomerSegments");

            migrationBuilder.DropTable(
                name: "ServicePoints");
        }
    }
}
