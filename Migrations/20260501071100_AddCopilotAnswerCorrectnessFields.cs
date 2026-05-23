using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotAnswerCorrectnessFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoCorrectnessScore",
                table: "CopilotTraceHistories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectnessIssueCategories",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectnessNotes",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAnswerCorrect",
                table: "CopilotTraceHistories",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReviewed",
                table: "CopilotTraceHistories",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                table: "CopilotTraceHistories",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCorrectnessScore",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "CorrectnessIssueCategories",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "CorrectnessNotes",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "IsAnswerCorrect",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "IsReviewed",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "CopilotTraceHistories");
        }
    }
}
