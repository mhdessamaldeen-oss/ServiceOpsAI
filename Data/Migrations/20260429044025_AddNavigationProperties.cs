using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNavigationProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistories_SessionId",
                table: "CopilotTraceHistories",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CopilotAssessmentRuns_SessionId",
                table: "CopilotAssessmentRuns",
                column: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_CopilotAssessmentRuns_CopilotChatSessions_SessionId",
                table: "CopilotAssessmentRuns",
                column: "SessionId",
                principalTable: "CopilotChatSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CopilotTraceHistories_CopilotChatSessions_SessionId",
                table: "CopilotTraceHistories",
                column: "SessionId",
                principalTable: "CopilotChatSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CopilotAssessmentRuns_CopilotChatSessions_SessionId",
                table: "CopilotAssessmentRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_CopilotTraceHistories_CopilotChatSessions_SessionId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistories_SessionId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotAssessmentRuns_SessionId",
                table: "CopilotAssessmentRuns");
        }
    }
}
