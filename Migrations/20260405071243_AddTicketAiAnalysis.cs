using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketAiAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketAiAnalyses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SuggestedClassification = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SuggestedPriority = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RootCauseHint = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImportantCommentClues = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImportantAttachmentClues = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NextInvestigationStep = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SuggestedAdminNote = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConfidenceLevel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AnalysisStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRefreshedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAiAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketAiAnalyses_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAiAnalyses_TicketId",
                table: "TicketAiAnalyses",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketAiAnalyses");
        }
    }
}
