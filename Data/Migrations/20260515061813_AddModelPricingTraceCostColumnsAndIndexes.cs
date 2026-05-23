using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelPricingTraceCostColumnsAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedCostUsd",
                table: "CopilotTraceHistories",
                type: "decimal(12,6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LlmCallCount",
                table: "CopilotTraceHistories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalCompletionTokens",
                table: "CopilotTraceHistories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalPromptTokens",
                table: "CopilotTraceHistories",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ModelPricings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    InputPer1K = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    OutputPer1K = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ContextTokens = table.Column<int>(type: "int", nullable: true),
                    IsLocal = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPricings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistory_Cost_CreatedAt",
                table: "CopilotTraceHistories",
                columns: new[] { "EstimatedCostUsd", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CopilotTraceHistory_SourceSuite_CreatedAt",
                table: "CopilotTraceHistories",
                columns: new[] { "SourceSuite", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricing_Provider_Model",
                table: "ModelPricings",
                columns: new[] { "Provider", "Model" },
                unique: true);

            // Seed default pricing rows — admins edit / disable / extend from /Admin/Settings/Pricing.
            // Rates as of mid-2026 published list prices; "*" model = provider-wide fallback.
            var now = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "ModelPricings",
                columns: new[] { "Provider", "Model", "DisplayName", "InputPer1K", "OutputPer1K", "Currency", "ContextTokens", "IsLocal", "IsActive", "Notes", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { "Gemini", "gemini-2.0-flash",       "Gemini 2.0 Flash",        0.000075m, 0.000300m, "USD", 1048576, false, true, "Google AI Studio list price", now, now },
                    { "Gemini", "gemini-1.5-flash",       "Gemini 1.5 Flash",        0.000075m, 0.000300m, "USD", 1048576, false, true, "Google AI Studio list price", now, now },
                    { "Gemini", "gemini-1.5-pro",         "Gemini 1.5 Pro",          0.00125m,  0.005m,    "USD", 2097152, false, true, "Google AI Studio list price", now, now },
                    { "Gemini", "*",                       "Gemini (default)",       0.000075m, 0.000300m, "USD", null,    false, true, "Fallback rate per provider", now, now },
                    { "Groq",   "llama-3.1-8b-instant",   "Llama 3.1 8B (Groq)",     0.00005m,  0.00008m,  "USD", 131072,  false, true, "Groq list price", now, now },
                    { "Groq",   "llama-3.3-70b-versatile","Llama 3.3 70B (Groq)",    0.00059m,  0.00079m,  "USD", 131072,  false, true, "Groq list price", now, now },
                    { "Groq",   "*",                       "Groq (default)",         0.00010m,  0.00020m,  "USD", null,    false, true, "Fallback rate per provider", now, now },
                    { "OpenAI", "gpt-4o-mini",            "GPT-4o mini",             0.00015m,  0.00060m,  "USD", 128000,  false, true, "OpenAI list price", now, now },
                    { "OpenAI", "gpt-4o",                 "GPT-4o",                  0.0025m,   0.010m,    "USD", 128000,  false, true, "OpenAI list price", now, now },
                    { "OpenAI", "*",                       "OpenAI (default)",       0.00015m,  0.00060m,  "USD", null,    false, true, "Fallback rate per provider", now, now },
                    { "Cloud",  "*",                       "Cloud / Azure (default)",0.0005m,   0.0015m,   "USD", null,    false, true, "Tune per Azure deployment", now, now },
                    { "Ollama",      "*",                  "Ollama (local)",         0m,        0m,        "USD", null,    true,  true, "Self-hosted; tokens tracked, no per-call charge", now, now },
                    { "LocalAI",     "*",                  "LocalAI (self-hosted)",  0m,        0m,        "USD", null,    true,  true, "Self-hosted; tokens tracked, no per-call charge", now, now },
                    { "DockerLocal", "*",                  "Docker model (local)",   0m,        0m,        "USD", null,    true,  true, "Docker stdin/stdout; no token stream available", now, now }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelPricings");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistory_Cost_CreatedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropIndex(
                name: "IX_CopilotTraceHistory_SourceSuite_CreatedAt",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "EstimatedCostUsd",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "LlmCallCount",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "TotalCompletionTokens",
                table: "CopilotTraceHistories");

            migrationBuilder.DropColumn(
                name: "TotalPromptTokens",
                table: "CopilotTraceHistories");
        }
    }
}
