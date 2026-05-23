using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntegrateTicketWithUtilityDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_DepartmentId",
                table: "Tickets");

            migrationBuilder.AddColumn<string>(
                name: "ComplaintType",
                table: "Tickets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RelatedBillId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameAr",
                table: "TicketCategories",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CustomerId_CreatedAt",
                table: "Tickets",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DepartmentId_StatusId",
                table: "Tickets",
                columns: new[] { "DepartmentId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RelatedBillId",
                table: "Tickets",
                column: "RelatedBillId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Bills_RelatedBillId",
                table: "Tickets",
                column: "RelatedBillId",
                principalTable: "Bills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Customers_CustomerId",
                table: "Tickets",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Bills_RelatedBillId",
                table: "Tickets");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Customers_CustomerId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_CustomerId_CreatedAt",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_DepartmentId_StatusId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_RelatedBillId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ComplaintType",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RelatedBillId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "NameAr",
                table: "TicketCategories");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DepartmentId",
                table: "Tickets",
                column: "DepartmentId");
        }
    }
}
