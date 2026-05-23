using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AiMultiRunAndAttachmentSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RunNumber",
                table: "TicketAiAnalyses",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TicketAiAttachmentSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketAiAnalysisId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Relevance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketAiAttachmentSummaries");

            migrationBuilder.DropColumn(
                name: "RunNumber",
                table: "TicketAiAnalyses");
        }
    }
}
