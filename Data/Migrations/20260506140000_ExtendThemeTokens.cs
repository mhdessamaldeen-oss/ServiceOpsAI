using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtendThemeTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrimaryContrastText",
                table: "CustomThemes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BgSurfaceAlt",
                table: "CustomThemes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BgElevated",
                table: "CustomThemes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuccessColor",
                table: "CustomThemes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WarningColor",
                table: "CustomThemes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DangerColor",
                table: "CustomThemes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InfoColor",
                table: "CustomThemes",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShadowColor",
                table: "CustomThemes",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PrimaryContrastText", table: "CustomThemes");
            migrationBuilder.DropColumn(name: "BgSurfaceAlt", table: "CustomThemes");
            migrationBuilder.DropColumn(name: "BgElevated", table: "CustomThemes");
            migrationBuilder.DropColumn(name: "SuccessColor", table: "CustomThemes");
            migrationBuilder.DropColumn(name: "WarningColor", table: "CustomThemes");
            migrationBuilder.DropColumn(name: "DangerColor", table: "CustomThemes");
            migrationBuilder.DropColumn(name: "InfoColor", table: "CustomThemes");
            migrationBuilder.DropColumn(name: "ShadowColor", table: "CustomThemes");
        }
    }
}
