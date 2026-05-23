using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotToolDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketAiAttachmentSummaries");

            migrationBuilder.DropColumn(
                name: "AttachmentSummariesJson",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "CustomerSentiment",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "IsReviewCompleted",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "RequiresManagerReview",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "ReviewNotes",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "ReviewedOn",
                table: "TicketAiAnalyses");

            migrationBuilder.CreateTable(
                name: "CopilotToolDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ToolKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ToolType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CopilotMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    EndpointUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    KeywordHints = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    QueryExtractionHint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ResponseFormatHint = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopilotToolDefinitions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CopilotToolDefinitions");

            migrationBuilder.AddColumn<string>(
                name: "AttachmentSummariesJson",
                table: "TicketAiAnalyses",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CustomerSentiment",
                table: "TicketAiAnalyses",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsReviewCompleted",
                table: "TicketAiAnalyses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresManagerReview",
                table: "TicketAiAnalyses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNotes",
                table: "TicketAiAnalyses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "TicketAiAnalyses",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedOn",
                table: "TicketAiAnalyses",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TicketAiAttachmentSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketAiAnalysisId = table.Column<int>(type: "int", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Relevance = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAiAttachmentSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketAiAttachmentSummaries_TicketAiAnalyses_TicketAiAnalysisId",
                        column: x => x.TicketAiAnalysisId,
                        principalTable: "TicketAiAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAiAttachmentSummaries_TicketAiAnalysisId",
                table: "TicketAiAttachmentSummaries",
                column: "TicketAiAnalysisId");
        }
    }
}
