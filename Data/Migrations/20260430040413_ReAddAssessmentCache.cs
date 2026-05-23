using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReAddAssessmentCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistories_SessionId",
                table: "CopilotTraceHistories");

            migrationBuilder.AlterColumn<string>(
                name: "CaseCode",
                table: "CopilotTraceHistories",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssessmentActualIntent",
                table: "CopilotTraceHistories",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssessmentActualMode",
                table: "CopilotTraceHistories",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssessmentActualTool",
                table: "CopilotTraceHistories",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            /*
            migrationBuilder.AddColumn<string>(
                name: "AssessmentDetail",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuccess",
                table: "CopilotTraceHistories",
                type: "bit",
                nullable: true);
            */

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistories_CaseCode_CreatedAt",
                table: "CopilotTraceHistories",
                columns: new[] { "CaseCode", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistories_SessionId_CaseCode_CreatedAt",
                table: "CopilotTraceHistories",
                columns: new[] { "SessionId", "CaseCode", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistories_CaseCode_CreatedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistories_SessionId_CaseCode_CreatedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "AssessmentActualIntent",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "AssessmentActualMode",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "AssessmentActualTool",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "AssessmentDetail",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "IsSuccess",
                table: "CopilotTraceHistories");

            migrationBuilder.AlterColumn<string>(
                name: "CaseCode",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistories_SessionId",
                table: "CopilotTraceHistories",
                column: "SessionId");
        }
    }
}
