using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CommentAttachmentId",
                table: "TicketComments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CommentId",
                table: "TicketAttachments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketAttachments_CommentId",
                table: "TicketAttachments",
                column: "CommentId");

            migrationBuilder.AddForeignKey(
                name: "FK_TicketAttachments_TicketComments_CommentId",
                table: "TicketAttachments",
                column: "CommentId",
                principalTable: "TicketComments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TicketAttachments_TicketComments_CommentId",
                table: "TicketAttachments");

            migrationBuilder.DropIndex(
                name: "IX_TicketAttachments_CommentId",
                table: "TicketAttachments");

            migrationBuilder.DropColumn(
                name: "CommentAttachmentId",
                table: "TicketComments");

            migrationBuilder.DropColumn(
                name: "CommentId",
                table: "TicketAttachments");
        }
    }
}
