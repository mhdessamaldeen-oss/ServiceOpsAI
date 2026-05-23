using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVersioningAndCleanupTraceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CopilotTraceHistories_AspNetUsers_UserId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistories_UserId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "TicketId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "CopilotTraceHistories");

            migrationBuilder.AddColumn<Guid>(
                name: "AssessmentRunId",
                table: "CopilotTraceHistories",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemVersion",
                table: "CopilotTraceHistories",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssessmentRunId",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "SystemVersion",
                table: "CopilotTraceHistories");

            migrationBuilder.AddColumn<int>(
                name: "TicketId",
                table: "CopilotTraceHistories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "CopilotTraceHistories",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistories_UserId",
                table: "CopilotTraceHistories",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CopilotTraceHistories_AspNetUsers_UserId",
                table: "CopilotTraceHistories",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
