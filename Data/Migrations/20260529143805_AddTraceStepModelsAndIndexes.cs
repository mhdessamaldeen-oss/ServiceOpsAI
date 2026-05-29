using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceStepModelsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StepModelsJson",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistory_CaseCode_CreatedAt",
                table: "CopilotTraceHistories",
                columns: new[] { "CaseCode", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistory_PipelineTraceId",
                table: "CopilotTraceHistories",
                column: "PipelineTraceId");

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistory_Session_CreatedAt",
                table: "CopilotTraceHistories",
                columns: new[] { "SessionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistory_CaseCode_CreatedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistory_PipelineTraceId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistory_Session_CreatedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "StepModelsJson",
                table: "CopilotTraceHistories");
        }
    }
}
