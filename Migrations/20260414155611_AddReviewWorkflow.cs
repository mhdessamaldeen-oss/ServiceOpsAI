using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReviewCompleted",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsReviewCompleted",
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
        }
    }
}
