using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class PromoteEnumsToLookupTables : Migration
    {
        // HAND-EDITED. The default EF-generated migration just dropped the old string
        // ServiceType/ComplaintType columns and added int FK columns with default 0,
        // which would orphan all existing rows. This version:
        //   1) creates the lookup tables,
        //   2) seeds initial values whose Code matches the old enum string,
        //   3) adds the FK columns as NULLABLE,
        //   4) back-fills via UPDATE...JOIN on the matching Code,
        //   5) makes the columns NOT NULL where the model requires,
        //   6) adds FK constraints,
        //   7) drops the old string columns last.
        //
        // The "GovernmentProcess" service type and "OutageCleared" resolution type are
        // included from day one to demonstrate that the lookup is open-ended — admins
        // can add more from /ReferenceData without a code change.

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ─── 1. Create lookup tables ──────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "ServiceTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IconClass = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComplaintTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplaintTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResolutionTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolutionTypes", x => x.Id);
                });

            migrationBuilder.CreateIndex(name: "IX_ServiceTypes_Code",   table: "ServiceTypes",   column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ComplaintTypes_Code", table: "ComplaintTypes", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ResolutionTypes_Code",table: "ResolutionTypes",column: "Code", unique: true);

            // ─── 2. Seed lookup values ───────────────────────────────────────────────
            // ServiceTypes: 4 utility services + one example "GovernmentProcess" to
            // demonstrate the lookup is open-ended (admins can add more via UI).
            migrationBuilder.InsertData(
                table: "ServiceTypes",
                columns: new[] { "Code", "NameEn", "NameAr", "Unit", "IconClass", "SortOrder", "IsActive" },
                values: new object[,]
                {
                    { "Electricity",       "Electricity",        "كهرباء",         "kWh",  "bi-lightning-charge-fill", 10, true },
                    { "Internet",          "Internet",           "إنترنت",         "GB",   "bi-router-fill",           20, true },
                    { "Water",             "Water",              "مياه",           "m³",   "bi-droplet-fill",          30, true },
                    { "Gas",               "Gas",                "غاز",            "cyl",  "bi-fire",                  40, true },
                    { "GovernmentProcess", "Government Process", "معاملة حكومية", null,  "bi-bank2",                 90, true }
                });

            migrationBuilder.InsertData(
                table: "ComplaintTypes",
                columns: new[] { "Code", "NameEn", "NameAr", "SortOrder", "IsActive" },
                values: new object[,]
                {
                    { "ServiceDown",     "Service Down",        "انقطاع الخدمة",       10, true },
                    { "ServiceDegraded", "Service Degraded",    "تدهور الخدمة",       20, true },
                    { "BillingDispute",  "Billing Dispute",     "اعتراض على الفاتورة", 30, true },
                    { "MeterIssue",      "Meter Issue",         "مشكلة في العداد",    40, true },
                    { "Disconnection",   "Disconnection",       "فصل الخدمة",         50, true },
                    { "NewConnection",   "New Connection",      "طلب اشتراك جديد",    60, true },
                    { "Other",           "Other",               "أخرى",               99, true }
                });

            migrationBuilder.InsertData(
                table: "ResolutionTypes",
                columns: new[] { "Code", "NameEn", "NameAr", "SortOrder", "IsActive" },
                values: new object[,]
                {
                    { "Resolved",      "Resolved",            "تم الحل",                10, true },
                    { "NoFault",       "No Fault Found",      "لا يوجد عطل",            20, true },
                    { "BillAdjusted",  "Bill Adjusted",       "تعديل الفاتورة",          30, true },
                    { "Escalated",     "Escalated",           "تم التصعيد",              40, true },
                    { "Cancelled",     "Cancelled",           "ملغاة",                   50, true },
                    { "OutageCleared", "Outage Cleared",      "تم إصلاح الانقطاع",       60, true }
                });

            // ─── 3. Add new FK columns as NULLABLE (so we can back-fill before NOT NULL) ──
            migrationBuilder.AddColumn<int>(name: "ServiceTypeId",    table: "Bills",       type: "int", nullable: true);
            migrationBuilder.AddColumn<int>(name: "ServiceTypeId",    table: "Departments", type: "int", nullable: true);
            migrationBuilder.AddColumn<int>(name: "ComplaintTypeId",  table: "Tickets",     type: "int", nullable: true);
            migrationBuilder.AddColumn<int>(name: "ResolutionTypeId", table: "Tickets",     type: "int", nullable: true);
            migrationBuilder.AddColumn<int>(name: "RegionId",         table: "Tickets",     type: "int", nullable: true);

            // ─── 4. Back-fill via JOIN on Code ←→ old string column ──────────────────
            // Bills.ServiceType (string) → Bills.ServiceTypeId (FK)
            migrationBuilder.Sql(@"
                UPDATE B
                SET B.ServiceTypeId = S.Id
                FROM [Bills] B
                INNER JOIN [ServiceTypes] S ON S.Code = B.ServiceType;");

            // Departments.ServiceType (string) → Departments.ServiceTypeId (FK)
            migrationBuilder.Sql(@"
                UPDATE D
                SET D.ServiceTypeId = S.Id
                FROM [Departments] D
                INNER JOIN [ServiceTypes] S ON S.Code = D.ServiceType;");

            // Tickets.ComplaintType (string) → Tickets.ComplaintTypeId (FK)
            migrationBuilder.Sql(@"
                UPDATE T
                SET T.ComplaintTypeId = C.Id
                FROM [Tickets] T
                INNER JOIN [ComplaintTypes] C ON C.Code = T.ComplaintType
                WHERE T.ComplaintType IS NOT NULL;");

            // ─── 5. Tighten Bills.ServiceTypeId + Departments.ServiceTypeId to NOT NULL ──
            // (Both are required in the model — the back-fill above must have populated
            // every row.) Tickets columns stay nullable per the model.
            migrationBuilder.AlterColumn<int>(
                name: "ServiceTypeId",
                table: "Bills",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ServiceTypeId",
                table: "Departments",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            // ─── 6. Add FK constraints + indexes ─────────────────────────────────────
            migrationBuilder.CreateIndex(name: "IX_Bills_ServiceTypeId",       table: "Bills",       column: "ServiceTypeId");
            migrationBuilder.CreateIndex(name: "IX_Departments_ServiceTypeId", table: "Departments", column: "ServiceTypeId");
            migrationBuilder.CreateIndex(name: "IX_Tickets_ComplaintTypeId",   table: "Tickets",     column: "ComplaintTypeId");
            migrationBuilder.CreateIndex(name: "IX_Tickets_ResolutionTypeId",  table: "Tickets",     column: "ResolutionTypeId");
            migrationBuilder.CreateIndex(name: "IX_Tickets_RegionId",          table: "Tickets",     column: "RegionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_ServiceTypes_ServiceTypeId",
                table: "Bills", column: "ServiceTypeId",
                principalTable: "ServiceTypes", principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Departments_ServiceTypes_ServiceTypeId",
                table: "Departments", column: "ServiceTypeId",
                principalTable: "ServiceTypes", principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_ComplaintTypes_ComplaintTypeId",
                table: "Tickets", column: "ComplaintTypeId",
                principalTable: "ComplaintTypes", principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_ResolutionTypes_ResolutionTypeId",
                table: "Tickets", column: "ResolutionTypeId",
                principalTable: "ResolutionTypes", principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Regions_RegionId",
                table: "Tickets", column: "RegionId",
                principalTable: "Regions", principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ─── 7. Drop the old string columns ──────────────────────────────────────
            migrationBuilder.DropColumn(name: "ServiceType",   table: "Bills");
            migrationBuilder.DropColumn(name: "ServiceType",   table: "Departments");
            migrationBuilder.DropColumn(name: "ComplaintType", table: "Tickets");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rough reverse — restores the string columns but does NOT back-fill them
            // from the lookup tables (the column rename is intentionally one-way for
            // simplicity; a Down here is for completeness, not data fidelity).
            migrationBuilder.DropForeignKey("FK_Bills_ServiceTypes_ServiceTypeId",             "Bills");
            migrationBuilder.DropForeignKey("FK_Departments_ServiceTypes_ServiceTypeId",       "Departments");
            migrationBuilder.DropForeignKey("FK_Tickets_ComplaintTypes_ComplaintTypeId",       "Tickets");
            migrationBuilder.DropForeignKey("FK_Tickets_ResolutionTypes_ResolutionTypeId",     "Tickets");
            migrationBuilder.DropForeignKey("FK_Tickets_Regions_RegionId",                     "Tickets");

            migrationBuilder.DropTable("ComplaintTypes");
            migrationBuilder.DropTable("ResolutionTypes");
            migrationBuilder.DropTable("ServiceTypes");

            migrationBuilder.DropIndex("IX_Bills_ServiceTypeId",       "Bills");
            migrationBuilder.DropIndex("IX_Departments_ServiceTypeId", "Departments");
            migrationBuilder.DropIndex("IX_Tickets_ComplaintTypeId",   "Tickets");
            migrationBuilder.DropIndex("IX_Tickets_ResolutionTypeId",  "Tickets");
            migrationBuilder.DropIndex("IX_Tickets_RegionId",          "Tickets");

            migrationBuilder.DropColumn("ServiceTypeId",    "Bills");
            migrationBuilder.DropColumn("ServiceTypeId",    "Departments");
            migrationBuilder.DropColumn("ComplaintTypeId",  "Tickets");
            migrationBuilder.DropColumn("ResolutionTypeId", "Tickets");
            migrationBuilder.DropColumn("RegionId",         "Tickets");

            migrationBuilder.AddColumn<string>("ServiceType",   "Bills",       type: "nvarchar(max)", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>("ServiceType",   "Departments", type: "nvarchar(max)", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>("ComplaintType", "Tickets",     type: "nvarchar(max)", nullable: true);
        }
    }
}
