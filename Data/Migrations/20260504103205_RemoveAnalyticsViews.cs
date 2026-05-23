using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAnalyticsViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_TicketAnalytics;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_EntityPerformance;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_AgentPerformance;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
