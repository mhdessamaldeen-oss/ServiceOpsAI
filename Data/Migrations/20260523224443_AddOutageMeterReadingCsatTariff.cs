using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutageMeterReadingCsatTariff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OutageId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CsatResponses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    CommentEn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CommentAr = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Sentiment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResponseChannel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CsatResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CsatResponses_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeterReadings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: false),
                    ReadingDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Consumption = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    ReaderType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MeterNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeterReadings_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeterReadings_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Outages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OutageNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Cause = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsPlanned = table.Column<bool>(type: "bit", nullable: false),
                    AffectedCustomerCount = table.Column<int>(type: "int", nullable: true),
                    TitleEn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TitleAr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Outages_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Outages_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Outages_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tariffs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: false),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BaseMonthlyFee = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    RatePerUnit = table.Column<decimal>(type: "decimal(12,4)", precision: 12, scale: 4, nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ChangeReasonEn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangeReasonAr = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tariffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tariffs_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tariffs_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OutageId",
                table: "Tickets",
                column: "OutageId");

            migrationBuilder.CreateIndex(
                name: "IX_CsatResponses_TicketId",
                table: "CsatResponses",
                column: "TicketId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_CustomerId_ServiceTypeId_ReadingDate",
                table: "MeterReadings",
                columns: new[] { "CustomerId", "ServiceTypeId", "ReadingDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_ServiceTypeId",
                table: "MeterReadings",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Outages_DepartmentId",
                table: "Outages",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Outages_OutageNumber",
                table: "Outages",
                column: "OutageNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outages_RegionId",
                table: "Outages",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Outages_ServiceTypeId_StartedAt",
                table: "Outages",
                columns: new[] { "ServiceTypeId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tariffs_RegionId",
                table: "Tariffs",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Tariffs_ServiceTypeId_RegionId_EffectiveFrom",
                table: "Tariffs",
                columns: new[] { "ServiceTypeId", "RegionId", "EffectiveFrom" });

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Outages_OutageId",
                table: "Tickets",
                column: "OutageId",
                principalTable: "Outages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Outages_OutageId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "CsatResponses");

            migrationBuilder.DropTable(
                name: "MeterReadings");

            migrationBuilder.DropTable(
                name: "Outages");

            migrationBuilder.DropTable(
                name: "Tariffs");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_OutageId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "OutageId",
                table: "Tickets");
        }
    }
}
