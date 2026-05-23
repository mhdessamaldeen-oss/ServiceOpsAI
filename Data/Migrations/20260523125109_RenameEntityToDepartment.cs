using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameEntityToDepartment : Migration
    {
        // NOTE: Hand-edited. The EF auto-generated diff didn't detect the
        // EntityId -> DepartmentId column rename because the mass C# rename
        // (Phase 01.B) also touched the model snapshot, so EF saw the
        // "previous" state as already having DepartmentId. The DB itself
        // still has EntityId columns + FK_*_Entitys_EntityId constraints,
        // so we hand-write the proper Up() that:
        //   1. Drops the real existing FKs (Entitys_EntityId).
        //   2. Renames the columns and their indexes.
        //   3. Drops the legacy Entitys table.
        //   4. Creates Departments with the new schema.
        //   5. Re-adds the FKs under their new names.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent FK drop — handles two cases:
            //   (1) Existing DB where earlier migrations created the FK with the OLD
            //       EntityId column name → constraint is FK_*_Entitys_EntityId
            //   (2) Fresh DB where the mass-rename touched the older migration files →
            //       constraint is already FK_*_Entitys_DepartmentId
            // We try both names with IF EXISTS guards.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_AspNetUsers_Entitys_EntityId')
                    ALTER TABLE [AspNetUsers] DROP CONSTRAINT [FK_AspNetUsers_Entitys_EntityId];
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_AspNetUsers_Entitys_DepartmentId')
                    ALTER TABLE [AspNetUsers] DROP CONSTRAINT [FK_AspNetUsers_Entitys_DepartmentId];
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Tickets_Entitys_EntityId')
                    ALTER TABLE [Tickets] DROP CONSTRAINT [FK_Tickets_Entitys_EntityId];
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Tickets_Entitys_DepartmentId')
                    ALTER TABLE [Tickets] DROP CONSTRAINT [FK_Tickets_Entitys_DepartmentId];
            ");

            // Idempotent column rename — only if the OLD EntityId column actually exists.
            // On a fresh DB built from migrations that were mass-renamed, the column is
            // already DepartmentId and this is a no-op.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE name = 'EntityId' AND object_id = OBJECT_ID('AspNetUsers'))
                    EXEC sp_rename N'AspNetUsers.EntityId', N'DepartmentId', N'COLUMN';
                IF EXISTS (SELECT 1 FROM sys.columns WHERE name = 'EntityId' AND object_id = OBJECT_ID('Tickets'))
                    EXEC sp_rename N'Tickets.EntityId', N'DepartmentId', N'COLUMN';
            ");

            // Idempotent index rename — same dual-name handling.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AspNetUsers_EntityId' AND object_id = OBJECT_ID('AspNetUsers'))
                    EXEC sp_rename N'AspNetUsers.IX_AspNetUsers_EntityId', N'IX_AspNetUsers_DepartmentId', N'INDEX';
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tickets_EntityId' AND object_id = OBJECT_ID('Tickets'))
                    EXEC sp_rename N'Tickets.IX_Tickets_EntityId', N'IX_Tickets_DepartmentId', N'INDEX';
            ");

            // Now safe to drop the legacy Entitys table.
            migrationBuilder.DropTable(
                name: "Entitys");

            // Unrelated columns the EF diff picked up on RetrievalBenchmarkRuns —
            // these are pre-existing model fields that hadn't been migrated yet.
            // Adding them here is fine; they're additive and non-breaking.
            migrationBuilder.AddColumn<double>(
                name: "MeanReciprocalRank",
                table: "RetrievalBenchmarkRuns",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Ndcg5",
                table: "RetrievalBenchmarkRuns",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Recall1",
                table: "RetrievalBenchmarkRuns",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Recall10",
                table: "RetrievalBenchmarkRuns",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Recall3",
                table: "RetrievalBenchmarkRuns",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Recall5",
                table: "RetrievalBenchmarkRuns",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            // Create the new Departments table with the full schema.
            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ServiceType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RegionId = table.Column<int>(type: "int", nullable: true),
                    ManagerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            // After the column rename, AspNetUsers.DepartmentId and Tickets.DepartmentId
            // still point at the now-dropped Entitys table id space. We need to NULL them
            // out so the new FK to Departments doesn't fail on orphan ids. Any existing
            // dept references are lost — the seeder will populate fresh ones.
            migrationBuilder.Sql("UPDATE [AspNetUsers] SET [DepartmentId] = NULL");
            migrationBuilder.Sql("UPDATE [Tickets] SET [DepartmentId] = NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Departments_DepartmentId",
                table: "AspNetUsers",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Departments_DepartmentId",
                table: "Tickets",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Departments_DepartmentId",
                table: "AspNetUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Departments_DepartmentId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropColumn(name: "MeanReciprocalRank", table: "RetrievalBenchmarkRuns");
            migrationBuilder.DropColumn(name: "Ndcg5",               table: "RetrievalBenchmarkRuns");
            migrationBuilder.DropColumn(name: "Recall1",             table: "RetrievalBenchmarkRuns");
            migrationBuilder.DropColumn(name: "Recall10",            table: "RetrievalBenchmarkRuns");
            migrationBuilder.DropColumn(name: "Recall3",             table: "RetrievalBenchmarkRuns");
            migrationBuilder.DropColumn(name: "Recall5",             table: "RetrievalBenchmarkRuns");

            migrationBuilder.RenameIndex(
                name: "IX_AspNetUsers_DepartmentId",
                table: "AspNetUsers",
                newName: "IX_AspNetUsers_EntityId");

            migrationBuilder.RenameIndex(
                name: "IX_Tickets_DepartmentId",
                table: "Tickets",
                newName: "IX_Tickets_EntityId");

            migrationBuilder.RenameColumn(
                name: "DepartmentId",
                table: "AspNetUsers",
                newName: "EntityId");

            migrationBuilder.RenameColumn(
                name: "DepartmentId",
                table: "Tickets",
                newName: "EntityId");

            migrationBuilder.CreateTable(
                name: "Entitys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entitys", x => x.Id);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Entitys_EntityId",
                table: "AspNetUsers",
                column: "EntityId",
                principalTable: "Entitys",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Entitys_EntityId",
                table: "Tickets",
                column: "EntityId",
                principalTable: "Entitys",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
