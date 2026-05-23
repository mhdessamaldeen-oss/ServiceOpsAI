using Microsoft.EntityFrameworkCore.Migrations;

# nullable disable

namespace AISupportAnalysisPlatform.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. vw_TicketAnalytics ──
            migrationBuilder.Sql(@"
                IF OBJECT_ID('dbo.vw_TicketAnalytics', 'V') IS NOT NULL DROP VIEW dbo.vw_TicketAnalytics;
                EXEC('CREATE VIEW dbo.vw_TicketAnalytics AS
                SELECT
                    t.Id                            AS TicketId,
                    t.TicketNumber,
                    t.Title,
                    t.[Description],
                    LEN(t.[Description])            AS DescriptionLength,
                    LEN(t.Title)                    AS TitleLength,
                    ISNULL(ts.Name, '''')             AS StatusName,
                    ISNULL(ts.IsClosedState, 0)     AS IsClosedStatus,
                    ISNULL(tp.Name, '''')             AS PriorityName,
                    ISNULL(tp.Level, 0)             AS PriorityLevel,
                    ISNULL(tc.Name, '''')             AS CategoryName,
                    ISNULL(src.Name, '''')            AS SourceName,
                    ISNULL(e.Name, '''')              AS EntityName,
                    t.ProductArea,
                    t.EnvironmentName,
                    t.BrowserName,
                    t.OperatingSystem,
                    t.ImpactScope,
                    t.AffectedUsersCount,
                    t.EscalationLevel,
                    ISNULL(creator.FirstName + '' '' + creator.LastName, '''')      AS CreatedByName,
                    assignee.FirstName + '' '' + assignee.LastName                AS AssignedToName,
                    resolver.FirstName + '' '' + resolver.LastName                AS ResolvedByName,
                    escalatee.FirstName + '' '' + escalatee.LastName              AS EscalatedToName,
                    t.CreatedAt,
                    t.UpdatedAt,
                    t.DueDate,
                    t.ResolvedAt,
                    t.ClosedAt,
                    t.EscalatedAt,
                    t.FirstRespondedAt,
                    t.IsSlaBreached,
                    t.RequiresManagerReview,
                    t.ResolutionSummary,
                    t.RootCause,
                    t.PendingReason,
                    t.ParentTicketId,
                    ISNULL((SELECT COUNT(*) FROM TicketComments   cc WHERE cc.TicketId = t.Id), 0)  AS CommentCount,
                    ISNULL((SELECT COUNT(*) FROM TicketAttachments aa WHERE aa.TicketId = t.Id), 0)  AS AttachmentCount,
                    DATEDIFF(DAY, t.CreatedAt, ISNULL(t.ResolvedAt, GETUTCDATE()))                  AS DaysOpen,
                    CASE WHEN t.FirstRespondedAt IS NOT NULL THEN DATEDIFF(HOUR, t.CreatedAt, t.FirstRespondedAt) END AS HoursToFirstResponse,
                    CASE WHEN t.ResolvedAt IS NOT NULL THEN DATEDIFF(HOUR, t.CreatedAt, t.ResolvedAt) END AS HoursToResolution
                FROM Tickets t
                LEFT JOIN TicketStatuses   ts   ON t.StatusId     = ts.Id
                LEFT JOIN TicketPriorities tp   ON t.PriorityId   = tp.Id
                LEFT JOIN TicketCategories tc   ON t.CategoryId   = tc.Id
                LEFT JOIN TicketSources    src  ON t.SourceId     = src.Id
                LEFT JOIN Entitys          e    ON t.EntityId     = e.Id
                LEFT JOIN AspNetUsers      creator   ON t.CreatedByUserId   = creator.Id
                LEFT JOIN AspNetUsers      assignee  ON t.AssignedToUserId  = assignee.Id
                LEFT JOIN AspNetUsers      resolver  ON t.ResolvedByUserId  = resolver.Id
                LEFT JOIN AspNetUsers      escalatee ON t.EscalatedToUserId = escalatee.Id
                WHERE t.IsDeleted = 0;')");

            // ── 2. vw_EntityPerformance ──
            migrationBuilder.Sql(@"
                IF OBJECT_ID('dbo.vw_EntityPerformance', 'V') IS NOT NULL DROP VIEW dbo.vw_EntityPerformance;
                EXEC('CREATE VIEW dbo.vw_EntityPerformance AS
                SELECT
                    e.Id                                AS EntityId,
                    e.Name                              AS EntityName,
                    e.IsActive                          AS IsActive,
                    COUNT(t.Id)                         AS TotalTickets,
                    SUM(CASE WHEN ts.IsClosedState = 0 AND t.Id IS NOT NULL THEN 1 ELSE 0 END)     AS OpenTickets,
                    SUM(CASE WHEN ts.IsClosedState = 1 THEN 1 ELSE 0 END)                          AS ClosedTickets,
                    SUM(CASE WHEN t.ResolvedAt IS NOT NULL THEN 1 ELSE 0 END)                      AS ResolvedTickets,
                    SUM(CASE WHEN t.IsSlaBreached = 1 THEN 1 ELSE 0 END)                           AS SlaBreaches,
                    CAST(CASE WHEN COUNT(t.Id) > 0 THEN SUM(CASE WHEN t.IsSlaBreached = 1 THEN 1 ELSE 0 END) * 100.0 / COUNT(t.Id) ELSE 0 END AS DECIMAL(5,1)) AS SlaBreachRate,
                    SUM(CASE WHEN t.RequiresManagerReview = 1 THEN 1 ELSE 0 END)                   AS ManagerReviewCount,
                    AVG(CASE WHEN t.ResolvedAt IS NOT NULL THEN DATEDIFF(HOUR, t.CreatedAt, t.ResolvedAt) END) AS AvgResolutionHours,
                    AVG(CASE WHEN t.FirstRespondedAt IS NOT NULL THEN DATEDIFF(HOUR, t.CreatedAt, t.FirstRespondedAt) END) AS AvgFirstResponseHours,
                    SUM(CASE WHEN t.EscalationLevel IS NOT NULL THEN 1 ELSE 0 END)                AS EscalatedTickets,
                    SUM(CASE WHEN t.CreatedAt >= DATEADD(DAY, -7, GETUTCDATE()) THEN 1 ELSE 0 END)  AS TicketsLast7Days,
                    SUM(CASE WHEN t.CreatedAt >= DATEADD(DAY, -30, GETUTCDATE()) THEN 1 ELSE 0 END) AS TicketsLast30Days,
                    MAX(t.CreatedAt)                    AS LastTicketDate,
                    MIN(t.CreatedAt)                    AS FirstTicketDate
                FROM Entitys e
                LEFT JOIN Tickets t ON t.EntityId = e.Id AND t.IsDeleted = 0
                LEFT JOIN TicketStatuses ts ON t.StatusId = ts.Id
                GROUP BY e.Id, e.Name, e.IsActive;')");

            // ── 3. vw_AgentPerformance ──
            migrationBuilder.Sql(@"
                IF OBJECT_ID('dbo.vw_AgentPerformance', 'V') IS NOT NULL DROP VIEW dbo.vw_AgentPerformance;
                EXEC('CREATE VIEW dbo.vw_AgentPerformance AS
                SELECT
                    u.Id                                AS UserId,
                    u.FirstName + '' '' + u.LastName      AS AgentName,
                    u.Email                             AS AgentEmail,
                    ISNULL(ent.Name, '''')                AS EntityName,
                    u.IsActive                          AS IsActive,
                    (SELECT COUNT(*) FROM Tickets ta WHERE ta.AssignedToUserId = u.Id AND ta.IsDeleted = 0)  AS TotalAssigned,
                    (SELECT COUNT(*) FROM Tickets ta INNER JOIN TicketStatuses tsa ON ta.StatusId = tsa.Id WHERE ta.AssignedToUserId = u.Id AND ta.IsDeleted = 0 AND tsa.IsClosedState = 0) AS CurrentOpenAssigned,
                    (SELECT COUNT(*) FROM Tickets tr WHERE tr.ResolvedByUserId = u.Id AND tr.IsDeleted = 0) AS TotalResolved,
                    (SELECT AVG(DATEDIFF(HOUR, tr.CreatedAt, tr.ResolvedAt)) FROM Tickets tr WHERE tr.ResolvedByUserId = u.Id AND tr.ResolvedAt IS NOT NULL AND tr.IsDeleted = 0) AS AvgResolutionHours,
                    (SELECT COUNT(*) FROM Tickets tc WHERE tc.CreatedByUserId = u.Id AND tc.IsDeleted = 0)  AS TotalCreated,
                    (SELECT COUNT(*) FROM Tickets ts WHERE ts.AssignedToUserId = u.Id AND ts.IsSlaBreached = 1 AND ts.IsDeleted = 0) AS SlaBreaches,
                    (SELECT COUNT(*) FROM TicketComments cm WHERE cm.CreatedByUserId = u.Id)                 AS TotalComments,
                    (SELECT COUNT(*) FROM Tickets t7 WHERE t7.AssignedToUserId = u.Id AND t7.IsDeleted = 0 AND t7.CreatedAt >= DATEADD(DAY, -7, GETUTCDATE())) AS AssignedLast7Days,
                    (SELECT COUNT(*) FROM Tickets t30 WHERE t30.ResolvedByUserId = u.Id AND t30.IsDeleted = 0 AND t30.ResolvedAt >= DATEADD(DAY, -30, GETUTCDATE())) AS ResolvedLast30Days
                FROM AspNetUsers u
                LEFT JOIN Entitys ent ON u.EntityId = ent.Id;')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_TicketAnalytics;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_EntityPerformance;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_AgentPerformance;");
        }
    }
}
