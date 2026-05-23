using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceOpsAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class HierarchicalTicketCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentCategoryId",
                table: "TicketCategories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServiceTypeId",
                table: "TicketCategories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "TicketCategories",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Tier",
                table: "TicketCategories",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategories_ParentCategoryId",
                table: "TicketCategories",
                column: "ParentCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategories_ServiceTypeId",
                table: "TicketCategories",
                column: "ServiceTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_TicketCategories_ServiceTypes_ServiceTypeId",
                table: "TicketCategories",
                column: "ServiceTypeId",
                principalTable: "ServiceTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TicketCategories_TicketCategories_ParentCategoryId",
                table: "TicketCategories",
                column: "ParentCategoryId",
                principalTable: "TicketCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ─── Seed the canonical Primary/Secondary taxonomy ───────────────────────
            // Insert Primary parents (one per ServiceType + 3 cross-service groups).
            // Then attach existing categories as Secondary children + mark with Tier.
            // Then insert additional Secondary children that didn't exist before.
            migrationBuilder.Sql(@"
-- Mark every existing category as Secondary by default so the column has values.
UPDATE TicketCategories SET Tier = 'Secondary' WHERE Tier IS NULL OR Tier = '';

-- Insert Primary parents.
INSERT INTO TicketCategories (Name, NameAr, Tier, ParentCategoryId, ServiceTypeId, SortOrder, IsActive)
SELECT * FROM (VALUES
    ('Electricity',         N'كهرباء',              'Primary', NULL, (SELECT Id FROM ServiceTypes WHERE Code = 'Electricity'),       10, 1),
    ('Internet',            N'إنترنت',              'Primary', NULL, (SELECT Id FROM ServiceTypes WHERE Code = 'Internet'),          20, 1),
    ('Water',               N'مياه',                'Primary', NULL, (SELECT Id FROM ServiceTypes WHERE Code = 'Water'),             30, 1),
    ('Gas',                 N'غاز',                 'Primary', NULL, (SELECT Id FROM ServiceTypes WHERE Code = 'Gas'),               40, 1),
    ('Government Process',  N'معاملة حكومية',       'Primary', NULL, (SELECT Id FROM ServiceTypes WHERE Code = 'GovernmentProcess'), 50, 1),
    ('Billing',             N'الفوترة',             'Primary', NULL, NULL,                                                            60, 1),
    ('Connection Management', N'إدارة الاشتراك',    'Primary', NULL, NULL,                                                            70, 1),
    ('Field Service',       N'الخدمة الميدانية',    'Primary', NULL, NULL,                                                            80, 1)
) AS v(Name, NameAr, Tier, ParentCategoryId, ServiceTypeId, SortOrder, IsActive)
WHERE NOT EXISTS (SELECT 1 FROM TicketCategories tc WHERE tc.Name = v.Name);

-- Attach the existing 10 Secondary categories to the right parents.
UPDATE c SET c.ParentCategoryId = p.Id, c.ServiceTypeId = p.ServiceTypeId, c.Tier = 'Secondary', c.SortOrder = 10
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Internet'
    WHERE c.Name = 'Internet outage';
UPDATE c SET c.ParentCategoryId = p.Id, c.ServiceTypeId = p.ServiceTypeId, c.Tier = 'Secondary', c.SortOrder = 20
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Internet'
    WHERE c.Name = 'Internet slow speed';
UPDATE c SET c.ParentCategoryId = p.Id, c.ServiceTypeId = p.ServiceTypeId, c.Tier = 'Secondary', c.SortOrder = 10
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Electricity'
    WHERE c.Name = 'Electricity outage';
UPDATE c SET c.ParentCategoryId = p.Id, c.ServiceTypeId = p.ServiceTypeId, c.Tier = 'Secondary', c.SortOrder = 10
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Water'
    WHERE c.Name = 'Water cut';
UPDATE c SET c.ParentCategoryId = p.Id, c.ServiceTypeId = p.ServiceTypeId, c.Tier = 'Secondary', c.SortOrder = 10
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Gas'
    WHERE c.Name = 'Gas service issue';
UPDATE c SET c.ParentCategoryId = p.Id, c.Tier = 'Secondary', c.SortOrder = 10
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Billing'
    WHERE c.Name = 'Billing dispute - amount';
UPDATE c SET c.ParentCategoryId = p.Id, c.Tier = 'Secondary', c.SortOrder = 20
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Billing'
    WHERE c.Name = 'Billing dispute - meter reading';
UPDATE c SET c.ParentCategoryId = p.Id, c.Tier = 'Secondary', c.SortOrder = 10
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Connection Management'
    WHERE c.Name = 'New service request';
UPDATE c SET c.ParentCategoryId = p.Id, c.Tier = 'Secondary', c.SortOrder = 20
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Connection Management'
    WHERE c.Name = 'Service disconnection issue';
UPDATE c SET c.ParentCategoryId = p.Id, c.Tier = 'Secondary', c.SortOrder = 10
    FROM TicketCategories c JOIN TicketCategories p ON p.Name = 'Field Service'
    WHERE c.Name = 'Technician visit needed';

-- Insert additional Secondary children (the deeper taxonomy the user asked for).
DECLARE @elec INT       = (SELECT Id FROM TicketCategories WHERE Name = 'Electricity');
DECLARE @elecSvc INT    = (SELECT Id FROM ServiceTypes WHERE Code = 'Electricity');
DECLARE @internet INT   = (SELECT Id FROM TicketCategories WHERE Name = 'Internet');
DECLARE @internetSvc INT= (SELECT Id FROM ServiceTypes WHERE Code = 'Internet');
DECLARE @water INT      = (SELECT Id FROM TicketCategories WHERE Name = 'Water');
DECLARE @waterSvc INT   = (SELECT Id FROM ServiceTypes WHERE Code = 'Water');
DECLARE @gas INT        = (SELECT Id FROM TicketCategories WHERE Name = 'Gas');
DECLARE @gasSvc INT     = (SELECT Id FROM ServiceTypes WHERE Code = 'Gas');
DECLARE @gov INT        = (SELECT Id FROM TicketCategories WHERE Name = 'Government Process');
DECLARE @govSvc INT     = (SELECT Id FROM ServiceTypes WHERE Code = 'GovernmentProcess');

INSERT INTO TicketCategories (Name, NameAr, Tier, ParentCategoryId, ServiceTypeId, SortOrder, IsActive)
SELECT * FROM (VALUES
    -- Electricity sub
    ('Voltage fluctuation',      N'تذبذب في الفولتية',      'Secondary', @elec,     @elecSvc,     30, 1),
    ('Frequent outages',         N'انقطاعات متكررة',        'Secondary', @elec,     @elecSvc,     40, 1),
    ('Wrong meter reading (elec)',N'قراءة عداد خاطئة (كهرباء)','Secondary',@elec,    @elecSvc,     50, 1),
    -- Internet sub
    ('Intermittent drops',       N'انقطاعات متقطعة',         'Secondary', @internet, @internetSvc, 30, 1),
    ('Router/modem issue',       N'مشكلة في الراوتر',        'Secondary', @internet, @internetSvc, 40, 1),
    -- Water sub
    ('Pipe leak',                N'تسرب في الأنابيب',       'Secondary', @water,    @waterSvc,    20, 1),
    ('Pipe burst',               N'انفجار أنبوب',           'Secondary', @water,    @waterSvc,    30, 1),
    ('Low pressure',             N'ضغط منخفض',              'Secondary', @water,    @waterSvc,    40, 1),
    ('Water quality',            N'جودة المياه',            'Secondary', @water,    @waterSvc,    50, 1),
    -- Gas sub
    ('Cylinder delivery delay',  N'تأخر توصيل الأسطوانة',   'Secondary', @gas,      @gasSvc,      20, 1),
    ('Gas leak',                 N'تسرب غاز',               'Secondary', @gas,      @gasSvc,      30, 1),
    -- Government Process sub
    ('Document delay',           N'تأخر في الوثيقة',        'Secondary', @gov,      @govSvc,      10, 1),
    ('Refund request',           N'طلب استرداد',            'Secondary', @gov,      @govSvc,      20, 1),
    ('Approval pending',         N'انتظار الموافقة',        'Secondary', @gov,      @govSvc,      30, 1)
) AS v(Name, NameAr, Tier, ParentCategoryId, ServiceTypeId, SortOrder, IsActive)
WHERE NOT EXISTS (SELECT 1 FROM TicketCategories tc WHERE tc.Name = v.Name);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TicketCategories_ServiceTypes_ServiceTypeId",
                table: "TicketCategories");

            migrationBuilder.DropForeignKey(
                name: "FK_TicketCategories_TicketCategories_ParentCategoryId",
                table: "TicketCategories");

            migrationBuilder.DropIndex(
                name: "IX_TicketCategories_ParentCategoryId",
                table: "TicketCategories");

            migrationBuilder.DropIndex(
                name: "IX_TicketCategories_ServiceTypeId",
                table: "TicketCategories");

            migrationBuilder.DropColumn(
                name: "ParentCategoryId",
                table: "TicketCategories");

            migrationBuilder.DropColumn(
                name: "ServiceTypeId",
                table: "TicketCategories");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "TicketCategories");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "TicketCategories");
        }
    }
}
