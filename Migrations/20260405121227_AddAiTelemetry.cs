using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddAiTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiagnosticMetadata",
                table: "TicketAiAnalyses",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "InputPromptSize",
                table: "TicketAiAnalyses",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ProcessingDurationMs",
                table: "TicketAiAnalyses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiagnosticMetadata",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "InputPromptSize",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "ProcessingDurationMs",
                table: "TicketAiAnalyses");
        }
    }
}
