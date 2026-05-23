using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddSentimentAnalysisAndOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresManagerReview",
                table: "Tickets",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CustomerSentiment",
                table: "TicketAiAnalyses",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RequiresManagerReview",
                table: "TicketAiAnalyses",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresManagerReview",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CustomerSentiment",
                table: "TicketAiAnalyses");

            migrationBuilder.DropColumn(
                name: "RequiresManagerReview",
                table: "TicketAiAnalyses");
        }
    }
}
