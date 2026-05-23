using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketOperationalContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AffectedUsersCount",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrowserName",
                table: "Tickets",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentName",
                table: "Tickets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalReferenceId",
                table: "Tickets",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSystemName",
                table: "Tickets",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImpactScope",
                table: "Tickets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatingSystem",
                table: "Tickets",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductArea",
                table: "Tickets",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE t
                SET
                    ProductArea =
                        CASE
                            WHEN c.Name IN ('Authentication', 'Authorization / Access') THEN 'Identity and Access'
                            WHEN c.Name = 'Attachment / File Handling' THEN 'Document Processing'
                            WHEN c.Name = 'Reporting' THEN 'Reporting and Analytics'
                            WHEN c.Name = 'Integration' THEN 'External Integrations'
                            WHEN c.Name = 'Performance' THEN 'Platform Performance'
                            WHEN c.Name = 'UI / UX' THEN 'Portal Experience'
                            WHEN t.Title LIKE '%Dashboard%' THEN 'Dashboard Workspace'
                            WHEN t.Title LIKE '%ticket%' OR t.Description LIKE '%ticket%' THEN 'Ticket Operations'
                            ELSE 'Core Support Platform'
                        END,
                    EnvironmentName =
                        CASE
                            WHEN t.Title LIKE '%UAT%' OR t.Description LIKE '%staging%' THEN 'UAT'
                            WHEN t.Description LIKE '%deployment%' OR t.Description LIKE '%nightly%' OR t.Description LIKE '%production%' THEN 'Production'
                            WHEN c.Name = 'Reporting' THEN 'Reporting Cluster'
                            ELSE 'Production'
                        END,
                    BrowserName =
                        CASE
                            WHEN t.Title LIKE '%Safari%' OR t.Description LIKE '%Safari%' THEN 'Safari'
                            WHEN t.Title LIKE '%Firefox%' OR t.Description LIKE '%Firefox%' THEN 'Firefox'
                            WHEN t.Title LIKE '%Chrome%' OR t.Description LIKE '%Chrome%' THEN 'Chrome'
                            WHEN t.Title LIKE '%Edge%' OR t.Description LIKE '%Edge%' THEN 'Edge'
                            ELSE NULL
                        END,
                    OperatingSystem =
                        CASE
                            WHEN c.Name IN ('Performance', 'Integration') THEN 'Ubuntu Server 22.04'
                            WHEN t.Title LIKE '%Safari%' OR t.Description LIKE '%Safari%' THEN 'macOS'
                            ELSE 'Windows 11'
                        END,
                    ExternalReferenceId = CONCAT('EXT-2026-', RIGHT(CONCAT('000000', CAST(t.Id AS varchar(6))), 6)),
                    ExternalSystemName =
                        CASE
                            WHEN t.Title LIKE '%HR%' OR t.Description LIKE '%HR%' THEN 'HR Sync API'
                            WHEN t.Title LIKE '%inventory%' OR t.Description LIKE '%inventory%' THEN 'Inventory Gateway'
                            WHEN t.Title LIKE '%SSO%' OR t.Description LIKE '%SSO%' THEN 'SSO Identity Provider'
                            WHEN t.Title LIKE '%Excel%' OR t.Description LIKE '%Excel%' THEN 'Excel Export Service'
                            WHEN t.Title LIKE '%upload%' OR t.Description LIKE '%upload%' OR t.Description LIKE '%CSV%' THEN 'Upload Processing API'
                            WHEN t.Title LIKE '%Dashboard%' OR t.Description LIKE '%dashboard%' THEN 'Dashboard API'
                            WHEN c.Name = 'Reporting' THEN 'Reporting Engine'
                            ELSE NULL
                        END,
                    ImpactScope =
                        CASE
                            WHEN t.Title LIKE '%Cross-entity%' OR t.Description LIKE '%cross-entity%' OR t.Description LIKE '%multiple systems%' OR t.Description LIKE '%Multiple systems%' THEN 'Cross-Entity'
                            WHEN t.Description LIKE '%Several users%' OR t.Description LIKE '%several users%' OR t.Description LIKE '%users%' THEN 'Department'
                            WHEN t.Description LIKE '%team%' THEN 'Team'
                            ELSE 'Single User'
                        END,
                    AffectedUsersCount =
                        CASE
                            WHEN t.Title LIKE '%Cross-entity%' OR t.Description LIKE '%cross-entity%' OR t.Description LIKE '%multiple systems%' OR t.Description LIKE '%Multiple systems%' THEN 120
                            WHEN t.Description LIKE '%Several users%' OR t.Description LIKE '%several users%' OR t.Description LIKE '%users%' THEN 35
                            WHEN t.Description LIKE '%team%' THEN 8
                            ELSE 1
                        END
                FROM Tickets t
                LEFT JOIN TicketCategories c ON c.Id = t.CategoryId
                WHERE t.ProductArea IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AffectedUsersCount",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "BrowserName",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "EnvironmentName",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalReferenceId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalSystemName",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ImpactScope",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "OperatingSystem",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ProductArea",
                table: "Tickets");
        }
    }
}
