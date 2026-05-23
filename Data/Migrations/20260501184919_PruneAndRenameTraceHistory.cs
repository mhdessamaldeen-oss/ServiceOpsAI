using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class PruneAndRenameTraceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "AssessmentRunId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "AutoCorrectnessScore",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "BuildFingerprint",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "CorrectnessIssueCategories",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "CorrectnessNotes",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "Intent",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "IsAnswerCorrect",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "IsReviewed",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "IsSuccess",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "SystemVersion",
                table: "CopilotTraceHistories");

            migrationBuilder.RenameColumn(
                name: "ExecutionDetailsJson",
                table: "CopilotTraceHistories",
                newName: "ExecutionPlan");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionTimes",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionTimes",
                table: "CopilotTraceHistories");

            migrationBuilder.RenameColumn(
                name: "ExecutionPlan",
                table: "CopilotTraceHistories",
                newName: "ExecutionDetailsJson");

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

            migrationBuilder.AddColumn<string>(
                name: "AssessmentDetail",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssessmentRunId",
                table: "CopilotTraceHistories",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoCorrectnessScore",
                table: "CopilotTraceHistories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildFingerprint",
                table: "CopilotTraceHistories",
                type: "nvarchar(256)",
                maxLength: 256,
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

            migrationBuilder.AddColumn<string>(
                name: "Intent",
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

            migrationBuilder.AddColumn<bool>(
                name: "IsSuccess",
                table: "CopilotTraceHistories",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
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

            migrationBuilder.AddColumn<string>(
                name: "SystemVersion",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
