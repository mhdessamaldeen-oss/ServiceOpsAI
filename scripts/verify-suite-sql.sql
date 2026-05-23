/* Run each candidate Gold SQL and report row counts so we know the question
   actually has data behind it.  Adjusted to today = 2026-05-14. */
SET NOCOUNT ON;

DECLARE @t TABLE (Code NVARCHAR(40), Rows INT);

-- ============ EASY ============
INSERT @t SELECT 'E01', COUNT(*) FROM Tickets WHERE IsDeleted = 0;
INSERT @t SELECT 'E02', COUNT(*) FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.Name='Open' AND t.IsDeleted=0;
INSERT @t SELECT 'E03', COUNT(*) FROM Tickets WHERE AssignedToUserId IS NULL AND IsDeleted=0;
INSERT @t SELECT 'E04', COUNT(*) FROM Tickets WHERE CAST(CreatedAt AS DATE) = CAST(GETDATE() AS DATE) AND IsDeleted=0;
INSERT @t SELECT 'E05', COUNT(*) FROM Tickets WHERE CAST(CreatedAt AS DATE) = CAST(DATEADD(day,-1,GETDATE()) AS DATE) AND IsDeleted=0;
INSERT @t SELECT 'E06', COUNT(*) FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.Name='Closed' AND t.IsDeleted=0;
INSERT @t SELECT 'E07', (SELECT COUNT(*) FROM (SELECT tp.Name, COUNT(*) c FROM Tickets t JOIN TicketPriorities tp ON tp.Id=t.PriorityId WHERE t.IsDeleted=0 GROUP BY tp.Name) z);
INSERT @t SELECT 'E08', COUNT(*) FROM Tickets t JOIN TicketPriorities p ON p.Id=t.PriorityId WHERE p.Name='Critical' AND t.IsDeleted=0;
INSERT @t SELECT 'E09', COUNT(*) FROM AspNetUsers WHERE IsActive=1;
INSERT @t SELECT 'E10', COUNT(*) FROM Entitys WHERE IsActive=1;
INSERT @t SELECT 'E11', COUNT(*) FROM Tickets t JOIN TicketSources s ON s.Id=t.SourceId WHERE s.Name='Email' AND t.IsDeleted=0;
INSERT @t SELECT 'E12', (SELECT COUNT(*) FROM (SELECT TOP 10 Id FROM Tickets WHERE IsDeleted=0 ORDER BY CreatedAt DESC) z);
INSERT @t SELECT 'E13', COUNT(*) FROM Tickets WHERE ResolvedAt IS NOT NULL AND YEAR(ResolvedAt)=YEAR(GETDATE()) AND MONTH(ResolvedAt)=MONTH(GETDATE()) AND IsDeleted=0;
INSERT @t SELECT 'E14', COUNT(*) FROM Tickets WHERE IsSlaBreached=1 AND IsDeleted=0;
INSERT @t SELECT 'E15', COUNT(*) FROM Tickets WHERE CreatedAt >= DATEADD(day,-7,GETDATE()) AND IsDeleted=0;
INSERT @t SELECT 'E16', COUNT(*) FROM Tickets WHERE Title LIKE '%login%' AND IsDeleted=0;
INSERT @t SELECT 'E17', COUNT(*) FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.Name='Rejected' AND t.IsDeleted=0;
INSERT @t SELECT 'E18', COUNT(*) FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.IsClosedState=0 AND t.IsDeleted=0;
INSERT @t SELECT 'E19', COUNT(*) FROM Tickets WHERE EscalatedAt IS NOT NULL AND IsDeleted=0;
INSERT @t SELECT 'E20', COUNT(*) FROM TicketComments;

-- ============ MEDIUM ============
INSERT @t SELECT 'M01', (SELECT COUNT(*) FROM (SELECT e.Name, COUNT(*) c FROM Tickets t JOIN Entitys e ON e.Id=t.EntityId WHERE t.IsDeleted=0 GROUP BY e.Name) z);
INSERT @t SELECT 'M02', (SELECT COUNT(*) FROM (SELECT s.Name, COUNT(*) c FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE t.IsDeleted=0 GROUP BY s.Name) z);
INSERT @t SELECT 'M03', (SELECT COUNT(*) FROM (SELECT s.Name, COUNT(*) c FROM Tickets t JOIN TicketSources s ON s.Id=t.SourceId WHERE t.IsDeleted=0 GROUP BY s.Name) z);
INSERT @t SELECT 'M04', (SELECT COUNT(*) FROM (SELECT TOP 5 c.Name, COUNT(*) cnt FROM Tickets t JOIN TicketCategories c ON c.Id=t.CategoryId WHERE t.IsDeleted=0 GROUP BY c.Name ORDER BY cnt DESC) z);
INSERT @t SELECT 'M05', COUNT(*) FROM Tickets t JOIN AspNetUsers u ON u.Id=t.CreatedByUserId WHERE u.FirstName='Sara' AND u.LastName='Hassan' AND t.IsDeleted=0;
INSERT @t SELECT 'M06', COUNT(*) FROM Tickets t JOIN AspNetUsers u ON u.Id=t.ResolvedByUserId WHERE u.FirstName='Lina' AND u.LastName='Mansour' AND t.IsDeleted=0;
INSERT @t SELECT 'M07', COUNT(*) FROM Tickets t JOIN AspNetUsers u ON u.Id=t.CreatedByUserId JOIN Entitys e ON e.Id=u.EntityId WHERE e.Name='Finance' AND t.IsDeleted=0;
INSERT @t SELECT 'M08', (SELECT COUNT(*) FROM (SELECT u.Id FROM AspNetUsers u JOIN AspNetUserRoles ur ON ur.UserId=u.Id JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE r.Name IN ('SupportAgent','Support Agent') AND u.IsActive=1) z);
INSERT @t SELECT 'M09', (SELECT COUNT(*) FROM (SELECT p.Name, COUNT(*) c FROM Tickets t JOIN TicketPriorities p ON p.Id=t.PriorityId WHERE t.IsDeleted=0 GROUP BY p.Name) z);
INSERT @t SELECT 'M10', COUNT(*) FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.Name='Resolved' AND YEAR(t.CreatedAt)=2026 AND MONTH(t.CreatedAt)=4 AND t.IsDeleted=0;
INSERT @t SELECT 'M11', COUNT(*) FROM Tickets WHERE EscalatedAt IS NOT NULL AND IsDeleted=0;
INSERT @t SELECT 'M12', COUNT(*) FROM Tickets t JOIN AspNetUsers u ON u.Id=t.AssignedToUserId WHERE u.FirstName='Tariq' AND u.LastName='Hamdan' AND t.IsDeleted=0;
INSERT @t SELECT 'M13', COUNT(*) FROM Tickets WHERE CreatedAt >= DATEADD(day,-DATEPART(weekday,GETDATE())+1,CAST(GETDATE() AS DATE)) AND IsDeleted=0;
INSERT @t SELECT 'M14', COUNT(*) FROM Tickets t JOIN TicketSources s ON s.Id=t.SourceId WHERE s.Name='Mobile App' AND t.IsDeleted=0;
INSERT @t SELECT 'M15', (SELECT COUNT(*) FROM (SELECT e.Name, COUNT(*) c FROM Tickets t JOIN Entitys e ON e.Id=t.EntityId WHERE t.IsDeleted=0 GROUP BY e.Name) z);
INSERT @t SELECT 'M16', COUNT(*) FROM Tickets t JOIN TicketPriorities p ON p.Id=t.PriorityId WHERE p.Name IN ('High','Critical') AND t.IsDeleted=0;
INSERT @t SELECT 'M17', (SELECT COUNT(*) FROM (SELECT u.Email, COUNT(*) c FROM Tickets t JOIN AspNetUsers u ON u.Id=t.AssignedToUserId WHERE t.IsDeleted=0 GROUP BY u.Email) z);
INSERT @t SELECT 'M18', COUNT(*) FROM Tickets WHERE ResolvedAt IS NULL AND IsDeleted=0;
INSERT @t SELECT 'M19', COUNT(*) FROM Tickets t JOIN AspNetUsers u ON u.Id=t.CreatedByUserId JOIN Entitys e ON e.Id=u.EntityId WHERE e.Name='Health' AND t.IsDeleted=0;
INSERT @t SELECT 'M20', COUNT(*) FROM TicketAttachments;

-- ============ HARD ============
INSERT @t SELECT 'H01', (SELECT COUNT(*) FROM (
    SELECT 'ThisMonth' AS p, COUNT(*) c FROM Tickets WHERE YEAR(CreatedAt)=YEAR(GETDATE()) AND MONTH(CreatedAt)=MONTH(GETDATE()) AND IsDeleted=0
    UNION ALL
    SELECT 'LastMonth', COUNT(*) FROM Tickets WHERE YEAR(CreatedAt)=YEAR(DATEADD(month,-1,GETDATE())) AND MONTH(CreatedAt)=MONTH(DATEADD(month,-1,GETDATE())) AND IsDeleted=0
) z);
INSERT @t SELECT 'H02', (SELECT COUNT(*) FROM (
    SELECT 'CurrentYear' AS p, COUNT(*) c FROM Tickets WHERE YEAR(CreatedAt)=YEAR(GETDATE()) AND IsDeleted=0
    UNION ALL
    SELECT 'LastYear', COUNT(*) FROM Tickets WHERE YEAR(CreatedAt)=YEAR(GETDATE())-1 AND IsDeleted=0
) z);
INSERT @t SELECT 'H03', (SELECT COUNT(*) FROM (SELECT u.FirstName, u.LastName, e.Name AS EntityName, COUNT(*) Total FROM Tickets t JOIN AspNetUsers u ON u.Id=t.CreatedByUserId LEFT JOIN Entitys e ON e.Id=u.EntityId WHERE t.IsDeleted=0 GROUP BY u.FirstName, u.LastName, e.Name) z);
INSERT @t SELECT 'H04', (SELECT COUNT(*) FROM (SELECT u.FirstName, u.LastName, COUNT(*) Total, AVG(CAST(DATEDIFF(hour, t.CreatedAt, t.ResolvedAt) AS FLOAT)) AvgHrs FROM Tickets t JOIN AspNetUsers u ON u.Id=t.AssignedToUserId WHERE t.IsDeleted=0 GROUP BY u.FirstName, u.LastName) z);
INSERT @t SELECT 'H05', (SELECT COUNT(*) FROM (SELECT t.Id, t.Title FROM Tickets t LEFT JOIN TicketComments c ON c.TicketId=t.Id WHERE c.Id IS NULL AND t.IsDeleted=0) z);
INSERT @t SELECT 'H06', (SELECT COUNT(*) FROM (SELECT u.Id FROM AspNetUsers u LEFT JOIN Tickets t ON t.CreatedByUserId=u.Id WHERE t.Id IS NULL) z);
INSERT @t SELECT 'H07', (SELECT AVG(CAST(DATEDIFF(hour, CreatedAt, ResolvedAt) AS FLOAT)) FROM Tickets WHERE ResolvedAt IS NOT NULL AND IsDeleted=0);
INSERT @t SELECT 'H08', (SELECT COUNT(*) FROM (SELECT TOP 5 u.FirstName, u.LastName, COUNT(*) c FROM Tickets t JOIN AspNetUsers u ON u.Id=t.CreatedByUserId WHERE t.IsDeleted=0 GROUP BY u.FirstName, u.LastName ORDER BY c DESC) z);
INSERT @t SELECT 'H09', (SELECT COUNT(*) FROM (SELECT TOP 5 u.FirstName, u.LastName, COUNT(*) c FROM Tickets t JOIN AspNetUsers u ON u.Id=t.ResolvedByUserId WHERE t.ResolvedAt IS NOT NULL AND t.IsDeleted=0 GROUP BY u.FirstName, u.LastName ORDER BY c DESC) z);
INSERT @t SELECT 'H10', (SELECT COUNT(*) FROM (SELECT t.Id, t.Title, p.Name AS Priority, u.FirstName + ' ' + u.LastName AS CreatedBy FROM Tickets t JOIN TicketPriorities p ON p.Id=t.PriorityId JOIN AspNetUsers u ON u.Id=t.CreatedByUserId WHERE t.IsSlaBreached=1 AND t.IsDeleted=0) z);
INSERT @t SELECT 'H11', (SELECT COUNT(*) FROM (SELECT c.Name, COUNT(*) c FROM Tickets t JOIN TicketCategories c ON c.Id=t.CategoryId WHERE t.IsDeleted=0 GROUP BY c.Name HAVING COUNT(*) > 15) z);
INSERT @t SELECT 'H12', (SELECT COUNT(*) FROM (SELECT e.Name, COUNT(*) c FROM Tickets t JOIN Entitys e ON e.Id=t.EntityId WHERE t.IsDeleted=0 GROUP BY e.Name HAVING COUNT(*) > 10) z);
INSERT @t SELECT 'H13', (SELECT COUNT(*) FROM (SELECT t.Id FROM Tickets t WHERE t.ResolvedAt IS NOT NULL AND DATEDIFF(hour, t.CreatedAt, t.ResolvedAt) > 24 AND t.IsDeleted=0) z);
INSERT @t SELECT 'H14', (SELECT COUNT(*) FROM (SELECT TOP 10 t.Id, t.Title, COUNT(c.Id) Comments FROM Tickets t JOIN TicketComments c ON c.TicketId=t.Id WHERE t.IsDeleted=0 GROUP BY t.Id, t.Title ORDER BY Comments DESC) z);
INSERT @t SELECT 'H15', (SELECT COUNT(*) FROM (SELECT t.Id, t.Title, t.PendingReason FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.Name='Pending' AND t.IsDeleted=0) z);
INSERT @t SELECT 'H16', (SELECT COUNT(*) FROM (SELECT u.FirstName, u.LastName, e.Name FROM AspNetUsers u JOIN Entitys e ON e.Id=u.EntityId JOIN AspNetUserRoles ur ON ur.UserId=u.Id JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE r.Name='EndUser' AND u.IsActive=1) z);
INSERT @t SELECT 'H17', (SELECT COUNT(*) FROM (SELECT t.Id, t.Title, t.EscalationLevel, u.FirstName + ' ' + u.LastName AS EscalatedTo FROM Tickets t LEFT JOIN AspNetUsers u ON u.Id=t.EscalatedToUserId WHERE t.EscalatedAt IS NOT NULL AND t.IsDeleted=0) z);
INSERT @t SELECT 'H18', (SELECT COUNT(*) FROM (SELECT c.Name AS Category, COUNT(*) c FROM Tickets t JOIN TicketCategories c ON c.Id=t.CategoryId JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.Name='Closed' AND t.IsDeleted=0 GROUP BY c.Name) z);
INSERT @t SELECT 'H19', (SELECT COUNT(*) FROM (SELECT TOP 10 t.Id, t.Title, DATEDIFF(hour, t.CreatedAt, t.ResolvedAt) AS Hours FROM Tickets t WHERE t.ResolvedAt IS NOT NULL AND t.IsDeleted=0 ORDER BY Hours DESC) z);
INSERT @t SELECT 'H20', (SELECT COUNT(*) FROM (SELECT t.Id, t.Title, p.Id AS ParentId, p.Title AS ParentTitle FROM Tickets t JOIN Tickets p ON p.Id=t.ParentTicketId WHERE t.IsDeleted=0) z);

-- ============ COMPLEX ============
INSERT @t SELECT 'C01', (SELECT COUNT(*) FROM (SELECT YEAR(CreatedAt) Y, MONTH(CreatedAt) M, COUNT(*) c FROM Tickets WHERE CreatedAt >= DATEADD(month,-3,GETDATE()) AND IsDeleted=0 GROUP BY YEAR(CreatedAt), MONTH(CreatedAt)) z);
INSERT @t SELECT 'C02', (SELECT 100.0 * SUM(CASE WHEN s.IsClosedState=1 THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE t.IsDeleted=0);
INSERT @t SELECT 'C03', (SELECT COUNT(*) FROM (
    SELECT 'ThisWeek' p, COUNT(*) c FROM Tickets WHERE ResolvedAt >= DATEADD(day,-DATEPART(weekday,GETDATE())+1,CAST(GETDATE() AS DATE)) AND ResolvedAt IS NOT NULL AND IsDeleted=0
    UNION ALL
    SELECT 'LastWeek', COUNT(*) FROM Tickets WHERE ResolvedAt >= DATEADD(day,-DATEPART(weekday,GETDATE())-6,CAST(GETDATE() AS DATE)) AND ResolvedAt < DATEADD(day,-DATEPART(weekday,GETDATE())+1,CAST(GETDATE() AS DATE)) AND IsDeleted=0
) z);
INSERT @t SELECT 'C04', (SELECT COUNT(*) FROM (SELECT p.Name, COUNT(*) Total, AVG(CAST(DATEDIFF(day, t.CreatedAt, GETDATE()) AS FLOAT)) AvgAge FROM Tickets t JOIN TicketPriorities p ON p.Id=t.PriorityId WHERE t.IsDeleted=0 GROUP BY p.Name) z);
INSERT @t SELECT 'C05', (SELECT COUNT(*) FROM (
    SELECT u.FirstName + ' ' + u.LastName AS Agent,
           COUNT(*) Assigned,
           SUM(CASE WHEN t.ResolvedAt IS NOT NULL THEN 1 ELSE 0 END) Resolved,
           100.0 * SUM(CASE WHEN t.ResolvedAt IS NOT NULL THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) ResRate
    FROM Tickets t JOIN AspNetUsers u ON u.Id=t.AssignedToUserId
    WHERE t.IsDeleted=0
    GROUP BY u.FirstName, u.LastName
) z);
INSERT @t SELECT 'C06', (SELECT COUNT(*) FROM (SELECT CAST(CreatedAt AS DATE) D, COUNT(*) C FROM Tickets WHERE CreatedAt >= DATEADD(day,-14,GETDATE()) AND IsDeleted=0 GROUP BY CAST(CreatedAt AS DATE)) z);
INSERT @t SELECT 'C07', (SELECT COUNT(*) FROM (SELECT TOP 3 e.Name, COUNT(*) c FROM Tickets t JOIN Entitys e ON e.Id=t.EntityId WHERE t.CreatedAt >= DATEADD(day,-30,GETDATE()) AND t.IsDeleted=0 GROUP BY e.Name ORDER BY c DESC) z);
INSERT @t SELECT 'C08', (SELECT COUNT(*) FROM (SELECT p.Name, AVG(CAST(DATEDIFF(hour, t.CreatedAt, t.FirstRespondedAt) AS FLOAT)) AvgHrs FROM Tickets t JOIN TicketPriorities p ON p.Id=t.PriorityId WHERE t.FirstRespondedAt IS NOT NULL AND t.IsDeleted=0 GROUP BY p.Name) z);
INSERT @t SELECT 'C09', (SELECT COUNT(*) FROM (
    SELECT s.Name AS Status, src.Name AS Source, COUNT(*) c
    FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId JOIN TicketSources src ON src.Id=t.SourceId
    WHERE t.IsDeleted=0 GROUP BY s.Name, src.Name
) z);
INSERT @t SELECT 'C10', (SELECT COUNT(*) FROM (SELECT TOP 1 e.Name, COUNT(*) c FROM Tickets t JOIN Entitys e ON e.Id=t.EntityId JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.IsClosedState=0 AND t.IsDeleted=0 GROUP BY e.Name ORDER BY c DESC) z);
INSERT @t SELECT 'C11', (SELECT COUNT(*) FROM (SELECT TOP 5 t.Id, t.Title, DATEDIFF(day, t.CreatedAt, GETDATE()) AgeDays FROM Tickets t JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.IsClosedState=0 AND t.IsDeleted=0 ORDER BY t.CreatedAt ASC) z);
INSERT @t SELECT 'C12', (SELECT COUNT(*) FROM (SELECT DATEPART(week, ResolvedAt) W, COUNT(*) c FROM Tickets WHERE ResolvedAt IS NOT NULL AND ResolvedAt >= DATEADD(week,-4,GETDATE()) AND IsDeleted=0 GROUP BY DATEPART(week, ResolvedAt)) z);
INSERT @t SELECT 'C13', (SELECT COUNT(*) FROM (
    SELECT c.Name AS Category,
           SUM(CASE WHEN s.IsClosedState=0 THEN 1 ELSE 0 END) OpenCnt,
           SUM(CASE WHEN s.IsClosedState=1 THEN 1 ELSE 0 END) ClosedCnt
    FROM Tickets t JOIN TicketCategories c ON c.Id=t.CategoryId JOIN TicketStatuses s ON s.Id=t.StatusId
    WHERE t.IsDeleted=0 GROUP BY c.Name
) z);
INSERT @t SELECT 'C14', (SELECT COUNT(*) FROM (
    SELECT 'Today' d, COUNT(*) c FROM Tickets WHERE CAST(CreatedAt AS DATE)=CAST(GETDATE() AS DATE) AND IsDeleted=0
    UNION ALL
    SELECT 'Yesterday', COUNT(*) FROM Tickets WHERE CAST(CreatedAt AS DATE)=CAST(DATEADD(day,-1,GETDATE()) AS DATE) AND IsDeleted=0
    UNION ALL
    SELECT '2 days ago', COUNT(*) FROM Tickets WHERE CAST(CreatedAt AS DATE)=CAST(DATEADD(day,-2,GETDATE()) AS DATE) AND IsDeleted=0
) z);
INSERT @t SELECT 'C15', (SELECT COUNT(*) FROM (SELECT TOP 10 u.FirstName + ' ' + u.LastName Agent, COUNT(*) Cnt FROM Tickets t JOIN AspNetUsers u ON u.Id=t.AssignedToUserId JOIN TicketStatuses s ON s.Id=t.StatusId WHERE s.IsClosedState=0 AND t.IsDeleted=0 GROUP BY u.FirstName, u.LastName ORDER BY Cnt DESC) z);

SELECT Code, Rows FROM @t ORDER BY Code;
