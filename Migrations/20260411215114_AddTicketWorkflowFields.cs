using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AISupportAnalysisPlatform.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketWorkflowFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedAt",
                table: "Tickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalatedToUserId",
                table: "Tickets",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalationLevel",
                table: "Tickets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolutionApprovedAt",
                table: "Tickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionApprovedByUserId",
                table: "Tickets",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedByUserId",
                table: "Tickets",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RootCause",
                table: "Tickets",
                type: "nvarchar(1500)",
                maxLength: 1500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TechnicalAssessment",
                table: "Tickets",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationNotes",
                table: "Tickets",
                type: "nvarchar(1500)",
                maxLength: 1500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_EscalatedToUserId",
                table: "Tickets",
                column: "EscalatedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ResolutionApprovedByUserId",
                table: "Tickets",
                column: "ResolutionApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ResolvedByUserId",
                table: "Tickets",
                column: "ResolvedByUserId");

            migrationBuilder.Sql("""
                UPDATE t
                SET TechnicalAssessment = LEFT(
                    CONCAT(
                        'Imported workflow baseline for existing ticket. ',
                        'Product area: ', ISNULL(t.ProductArea, 'Not Set'), '. ',
                        'Environment: ', ISNULL(t.EnvironmentName, 'Not Set'), '. ',
                        'Impact scope: ', ISNULL(t.ImpactScope, 'Unknown'), '.'
                    ),
                    2000
                )
                FROM Tickets t
                WHERE t.TechnicalAssessment IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE t
                SET ResolvedByUserId = t.AssignedToUserId
                FROM Tickets t
                INNER JOIN TicketStatuses ts ON ts.Id = t.StatusId
                WHERE t.ResolvedByUserId IS NULL
                  AND t.AssignedToUserId IS NOT NULL
                  AND ts.Name IN ('Resolved', 'Closed');
                """);

            migrationBuilder.Sql("""
                UPDATE t
                SET VerificationNotes = 'Imported verification baseline from existing resolved workflow state.'
                FROM Tickets t
                INNER JOIN TicketStatuses ts ON ts.Id = t.StatusId
                WHERE t.VerificationNotes IS NULL
                  AND ts.Name IN ('Resolved', 'Closed');
                """);

            migrationBuilder.Sql("""
                UPDATE t
                SET ResolutionApprovedByUserId = t.AssignedToUserId,
                    ResolutionApprovedAt = COALESCE(t.ClosedAt, t.ResolvedAt, t.UpdatedAt, t.CreatedAt)
                FROM Tickets t
                INNER JOIN TicketStatuses ts ON ts.Id = t.StatusId
                WHERE t.ResolutionApprovedByUserId IS NULL
                  AND t.AssignedToUserId IS NOT NULL
                  AND ts.Name = 'Closed';
                """);

            migrationBuilder.Sql("""
                UPDATE t
                SET EscalationLevel = 'L2',
                    EscalatedToUserId = t.AssignedToUserId,
                    EscalatedAt = COALESCE(t.UpdatedAt, t.CreatedAt)
                FROM Tickets t
                INNER JOIN TicketStatuses ts ON ts.Id = t.StatusId
                INNER JOIN TicketPriorities tp ON tp.Id = t.PriorityId
                WHERE t.EscalationLevel IS NULL
                  AND t.EscalatedToUserId IS NULL
                  AND t.AssignedToUserId IS NOT NULL
                  AND ts.Name IN ('In Progress', 'Pending')
                  AND tp.Name IN ('High', 'Critical');
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_AspNetUsers_EscalatedToUserId",
                table: "Tickets",
                column: "EscalatedToUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_AspNetUsers_ResolutionApprovedByUserId",
                table: "Tickets",
                column: "ResolutionApprovedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_AspNetUsers_ResolvedByUserId",
                table: "Tickets",
                column: "ResolvedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_AspNetUsers_EscalatedToUserId",
                table: "Tickets");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_AspNetUsers_ResolutionApprovedByUserId",
                table: "Tickets");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_AspNetUsers_ResolvedByUserId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_EscalatedToUserId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ResolutionApprovedByUserId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ResolvedByUserId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "EscalatedToUserId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "EscalationLevel",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolutionApprovedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolutionApprovedByUserId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ResolvedByUserId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RootCause",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "TechnicalAssessment",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "VerificationNotes",
                table: "Tickets");
        }
    }
}
