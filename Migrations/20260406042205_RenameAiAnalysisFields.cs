using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class RenameAiAnalysisFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportantAttachmentClues",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "ImportantCommentClues",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "NextInvestigationStep",
                table: "TicketAiAnalyses");

            migrationBuilder.RenameColumn(
                name: "SuggestedAdminNote",
                table: "TicketAiAnalyses",
                newName: "NextStepSuggestion");

            migrationBuilder.RenameColumn(
                name: "RootCauseHint",
                table: "TicketAiAnalyses",
                newName: "KeyClues");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "NextStepSuggestion",
                table: "TicketAiAnalyses",
                newName: "SuggestedAdminNote");

            migrationBuilder.RenameColumn(
                name: "KeyClues",
                table: "TicketAiAnalyses",
                newName: "RootCauseHint");

            migrationBuilder.AddColumn<string>(
                name: "ImportantAttachmentClues",
                table: "TicketAiAnalyses",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ImportantCommentClues",
                table: "TicketAiAnalyses",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NextInvestigationStep",
                table: "TicketAiAnalyses",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }
    }
}
