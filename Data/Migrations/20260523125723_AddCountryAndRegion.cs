using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCountryAndRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Countries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsoCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Regions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameEn = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CountryId = table.Column<int>(type: "int", nullable: false),
                    ParentRegionId = table.Column<int>(type: "int", nullable: true),
                    RegionType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Regions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Regions_Countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "Countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Regions_Regions_ParentRegionId",
                        column: x => x.ParentRegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Departments_RegionId",
                table: "Departments",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Countries_IsoCode",
                table: "Countries",
                column: "IsoCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Regions_CountryId_RegionType",
                table: "Regions",
                columns: new[] { "CountryId", "RegionType" });

            migrationBuilder.CreateIndex(
                name: "IX_Regions_ParentRegionId",
                table: "Regions",
                column: "ParentRegionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Departments_Regions_RegionId",
                table: "Departments",
                column: "RegionId",
                principalTable: "Regions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // --- Seed Syria ---
            migrationBuilder.InsertData(
                table: "Countries",
                columns: new[] { "Id", "NameEn", "NameAr", "IsoCode", "IsActive" },
                values: new object[] { 1, "Syria", "سوريا", "SYR", true });

            // --- Seed 14 Syrian governorates (Ids 1-14) ---
            migrationBuilder.InsertData(
                table: "Regions",
                columns: new[] { "Id", "NameEn", "NameAr", "CountryId", "ParentRegionId", "RegionType", "IsActive" },
                values: new object[,]
                {
                    { 1,  "Damascus",     "دمشق",       1, null, "Governorate", true },
                    { 2,  "Rif Dimashq",  "ريف دمشق",   1, null, "Governorate", true },
                    { 3,  "Aleppo",       "حلب",        1, null, "Governorate", true },
                    { 4,  "Homs",         "حمص",        1, null, "Governorate", true },
                    { 5,  "Hama",         "حماة",       1, null, "Governorate", true },
                    { 6,  "Lattakia",     "اللاذقية",   1, null, "Governorate", true },
                    { 7,  "Tartus",       "طرطوس",      1, null, "Governorate", true },
                    { 8,  "Idlib",        "إدلب",       1, null, "Governorate", true },
                    { 9,  "Daraa",        "درعا",       1, null, "Governorate", true },
                    { 10, "As-Suwayda",   "السويداء",   1, null, "Governorate", true },
                    { 11, "Quneitra",     "القنيطرة",   1, null, "Governorate", true },
                    { 12, "Deir ez-Zor",  "دير الزور",  1, null, "Governorate", true },
                    { 13, "Raqqa",        "الرقة",      1, null, "Governorate", true },
                    { 14, "Al-Hasakah",   "الحسكة",     1, null, "Governorate", true }
                });

            // --- Seed districts (Ids 15-76) ---
            migrationBuilder.InsertData(
                table: "Regions",
                columns: new[] { "Id", "NameEn", "NameAr", "CountryId", "ParentRegionId", "RegionType", "IsActive" },
                values: new object[,]
                {
                    // Damascus districts (parent 1)
                    { 15, "Old Damascus",   "دمشق القديمة", 1, 1, "District", true },
                    { 16, "Bab Touma",      "باب توما",      1, 1, "District", true },
                    { 17, "Mezzeh",         "المزة",        1, 1, "District", true },
                    { 18, "Malki",          "المالكي",       1, 1, "District", true },
                    { 19, "Midan",          "الميدان",       1, 1, "District", true },
                    { 20, "Qassaa",         "القصاع",        1, 1, "District", true },
                    { 21, "Rukn al-Din",    "ركن الدين",     1, 1, "District", true },
                    { 22, "Mazzeh 86",      "مزة 86",       1, 1, "District", true },
                    { 23, "Dummar",         "دمر",           1, 1, "District", true },
                    { 24, "Kafr Sousa",     "كفر سوسة",      1, 1, "District", true },

                    // Rif Dimashq districts (parent 2)
                    { 25, "Douma",          "دوما",          1, 2, "District", true },
                    { 26, "Daraya",         "داريا",         1, 2, "District", true },
                    { 27, "Harasta",        "حرستا",         1, 2, "District", true },
                    { 28, "Zabadani",       "الزبداني",      1, 2, "District", true },
                    { 29, "Qudsaya",        "قدسيا",         1, 2, "District", true },
                    { 30, "Jaramana",       "جرمانا",        1, 2, "District", true },

                    // Aleppo districts (parent 3)
                    { 31, "Old Aleppo",     "حلب القديمة",   1, 3, "District", true },
                    { 32, "Sulaymaniyah",   "السليمانية",    1, 3, "District", true },
                    { 33, "Jamiliyah",      "الجميلية",      1, 3, "District", true },
                    { 34, "Azizieh",        "العزيزية",      1, 3, "District", true },
                    { 35, "Sheikh Maksoud", "الشيخ مقصود",   1, 3, "District", true },
                    { 36, "Salah ad-Din",   "صلاح الدين",    1, 3, "District", true },
                    { 37, "Hamdaniyah",     "الحمدانية",     1, 3, "District", true },
                    { 38, "New Aleppo",     "حلب الجديدة",   1, 3, "District", true },

                    // Homs districts (parent 4)
                    { 39, "Old Homs",       "حمص القديمة",   1, 4, "District", true },
                    { 40, "Al-Waer",        "الوعر",         1, 4, "District", true },
                    { 41, "Karm al-Zaytoun","كرم الزيتون",    1, 4, "District", true },
                    { 42, "Inshaat",        "الإنشاءات",     1, 4, "District", true },
                    { 43, "Hamidiyah",      "الحميدية",      1, 4, "District", true },

                    // Hama districts (parent 5)
                    { 44, "Al-Madinah",     "المدينة",       1, 5, "District", true },
                    { 45, "Al-Hader",       "الحاضر",        1, 5, "District", true },
                    { 46, "Suq al-Hal",     "سوق الهال",     1, 5, "District", true },
                    { 47, "Aleppo Road",    "طريق حلب",      1, 5, "District", true },

                    // Lattakia districts (parent 6)
                    { 48, "Sheikh Daher",   "الشيخ ضاهر",    1, 6, "District", true },
                    { 49, "Al-Mashrou",     "المشروع",       1, 6, "District", true },
                    { 50, "Al-Saliba",      "الصليبة",       1, 6, "District", true },
                    { 51, "American Quarter","الحي الأمريكي", 1, 6, "District", true },

                    // Tartus districts (parent 7)
                    { 52, "Tartus Center",  "وسط طرطوس",     1, 7, "District", true },
                    { 53, "Banias",         "بانياس",        1, 7, "District", true },
                    { 54, "Safita",         "صافيتا",        1, 7, "District", true },

                    // Idlib districts (parent 8)
                    { 55, "Idlib Center",   "وسط إدلب",      1, 8, "District", true },
                    { 56, "Ariha",          "أريحا",         1, 8, "District", true },
                    { 57, "Maarat al-Numan","معرة النعمان",   1, 8, "District", true },

                    // Daraa districts (parent 9)
                    { 58, "Daraa al-Balad", "درعا البلد",    1, 9, "District", true },
                    { 59, "Daraa al-Mahatta","درعا المحطة",   1, 9, "District", true },
                    { 60, "Izra",           "إزرع",          1, 9, "District", true },

                    // As-Suwayda districts (parent 10)
                    { 61, "Suwayda Center", "وسط السويداء",  1, 10, "District", true },
                    { 62, "Salkhad",        "صلخد",          1, 10, "District", true },
                    { 63, "Shahba",         "شهبا",          1, 10, "District", true },

                    // Quneitra districts (parent 11)
                    { 64, "Quneitra Center","وسط القنيطرة",  1, 11, "District", true },
                    { 65, "Khan Arnabeh",   "خان أرنبة",     1, 11, "District", true },

                    // Deir ez-Zor districts (parent 12)
                    { 66, "Deir ez-Zor Center","وسط دير الزور",1, 12, "District", true },
                    { 67, "Al-Joura",       "الجورة",        1, 12, "District", true },
                    { 68, "Al-Mayadin",     "الميادين",      1, 12, "District", true },

                    // Raqqa districts (parent 13)
                    { 69, "Raqqa Center",   "وسط الرقة",     1, 13, "District", true },
                    { 70, "Al-Thawrah",     "الثورة",        1, 13, "District", true },
                    { 71, "Tabqa",          "الطبقة",        1, 13, "District", true },

                    // Al-Hasakah districts (parent 14)
                    { 72, "Hasakah Center", "وسط الحسكة",    1, 14, "District", true },
                    { 73, "Qamishli",       "القامشلي",      1, 14, "District", true },
                    { 74, "Amuda",          "عامودا",        1, 14, "District", true },
                    { 75, "Ras al-Ayn",     "رأس العين",     1, 14, "District", true },
                    { 76, "Al-Malikiyah",   "المالكية",      1, 14, "District", true }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Departments_Regions_RegionId",
                table: "Departments");

            migrationBuilder.DropTable(
                name: "Regions");

            migrationBuilder.DropTable(
                name: "Countries");

            migrationBuilder.DropIndex(
                name: "IX_Departments_RegionId",
                table: "Departments");
        }
    }
}
