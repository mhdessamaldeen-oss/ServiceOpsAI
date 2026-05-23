using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class FinalArchitectStudioSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomThemes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PrimaryColor = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BgMain = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BgCard = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BgSidebar = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BgHeader = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TextMain = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TextMuted = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BorderColor = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IsSystemTheme = table.Column<bool>(type: "bit", nullable: false),
                    SystemIdentifier = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomThemes", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomThemes");
        }
    }
}
