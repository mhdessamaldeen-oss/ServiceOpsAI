using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTicketIdFromTraceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CopilotTraceHistories_Tickets_TicketId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistories_TicketId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "TicketId",
                table: "CopilotTraceHistories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TicketId",
                table: "CopilotTraceHistories",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistories_TicketId",
                table: "CopilotTraceHistories",
                column: "TicketId");

            migrationBuilder.AddForeignKey(
                name: "FK_CopilotTraceHistories_Tickets_TicketId",
                table: "CopilotTraceHistories",
                column: "TicketId",
                principalTable: "Tickets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
