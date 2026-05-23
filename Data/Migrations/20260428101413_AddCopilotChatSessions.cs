using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotChatSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CopilotChatSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastInteractionAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Surface = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopilotChatSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CopilotChatSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CopilotChatMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TraceId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CopilotChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CopilotChatMessages_CopilotChatSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "CopilotChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CopilotChatMessages_CopilotTraceHistories_TraceId",
                        column: x => x.TraceId,
                        principalTable: "CopilotTraceHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CopilotChatMessages_SessionId",
                table: "CopilotChatMessages",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CopilotChatMessages_TraceId",
                table: "CopilotChatMessages",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_CopilotChatSessions_UserId",
                table: "CopilotChatSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CopilotChatMessages");

            migrationBuilder.DropTable(
                name: "CopilotChatSessions");
        }
    }
}
