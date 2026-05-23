using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddRetrievalBenchmarkRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RetrievalBenchmarkRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TotalCases = table.Column<int>(type: "int", nullable: false),
                    EvaluatedCases = table.Column<int>(type: "int", nullable: false),
                    HitCases = table.Column<int>(type: "int", nullable: false),
                    HitRate = table.Column<double>(type: "float", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetrievalBenchmarkRuns", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RetrievalBenchmarkRuns");
        }
    }
}
