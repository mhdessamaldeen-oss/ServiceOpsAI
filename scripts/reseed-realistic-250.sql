/* ============================================================
   Realistic seed: 250 tickets, expanded users + entities
   Reference date: 2026-05-14 (today)
   ============================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET ANSI_PADDING ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

BEGIN TRY
BEGIN TRANSACTION;

/* -------------------------------------------------------------
   1) ADD NEW ENTITIES (Id 15-20)
   ------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM Entitys WHERE Name = 'Operations')
INSERT INTO Entitys (Name, IsActive) VALUES
    ('Operations',         1),
    ('Customer Service',   1),
    ('Procurement',        1),
    ('Audit & Compliance', 1),
    ('Engineering',        1),
    ('Quality Assurance',  1);

-- Deactivate Temp_ legacy entities so they no longer pollute pickers
UPDATE Entitys SET IsActive = 0 WHERE Name LIKE 'Temp_%';

/* -------------------------------------------------------------
   2) ADD NEW USERS  (13 new — End Users + Support Agents + 1 Admin + 1 Manager)
   ------------------------------------------------------------- */
DECLARE @PwdHash NVARCHAR(MAX) = N'AQAAAAIAAYagAAAAENKcQfMKnjntY9034fCtrhLN5rqByWi/vYvbvTNMxmdT2PnyBx74x+a0gbIQImxnng==';
DECLARE @RoleEndUser    NVARCHAR(450) = (SELECT Id FROM AspNetRoles WHERE Name = 'EndUser');
DECLARE @RoleAgent      NVARCHAR(450) = (SELECT Id FROM AspNetRoles WHERE Name = 'SupportAgent');
DECLARE @RoleAdmin      NVARCHAR(450) = (SELECT Id FROM AspNetRoles WHERE Name = 'Admin');
DECLARE @RoleManager    NVARCHAR(450) = (SELECT Id FROM AspNetRoles WHERE Name = 'Manager');

DECLARE @NewUsers TABLE (
    Id          NVARCHAR(450),
    FirstName   NVARCHAR(50),
    LastName    NVARCHAR(50),
    Email       NVARCHAR(256),
    EntityName  NVARCHAR(100),
    Role        NVARCHAR(450)
);

INSERT INTO @NewUsers VALUES
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Sara',     'Hassan',     'sara.hassan@health.local',           'Health',            @RoleEndUser),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Omar',     'Khalifa',    'omar.khalifa@finance.local',         'Finance',           @RoleEndUser),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Lina',     'Mansour',    'lina.mansour@finance.local',         'Finance',           @RoleAgent),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Ahmed',    'Saleh',      'ahmed.saleh@transport.local',        'Transport',         @RoleEndUser),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Noura',    'Al-Otaibi',  'noura.alotaibi@transport.local',     'Transport',         @RoleAgent),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Karim',    'Younes',     'karim.younes@infrastructure.local',  'Infrastructure',    @RoleEndUser),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Fatima',   'Zahra',      'fatima.zahra@hr.local',              'HR',                @RoleEndUser),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Hassan',   'Ibrahim',    'hassan.ibrahim@hr.local',            'HR',                @RoleAgent),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Dina',     'El-Sayed',   'dina.elsayed@legal.local',           'Legal',             @RoleEndUser),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Yusuf',    'Al-Mutairi', 'yusuf.almutairi@marketing.local',    'Marketing',         @RoleEndUser),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Reem',     'Saadi',      'reem.saadi@sales.local',             'Sales',             @RoleAgent),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Tariq',    'Hamdan',     'tariq.hamdan@operations.local',      'Operations',        @RoleAdmin),
    (LOWER(CONVERT(NVARCHAR(36), NEWID())), 'Maya',     'Costa',      'maya.costa@engineering.local',       'Engineering',       @RoleManager);

-- Insert users (skip any whose email already exists). Resolve EntityId by Name.
INSERT INTO AspNetUsers
    (Id, FirstName, LastName, IsActive, EntityId, UserName, NormalizedUserName,
     Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp, ConcurrencyStamp,
     PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
SELECT
    n.Id, n.FirstName, n.LastName, 1, e.Id, n.Email, UPPER(n.Email),
    n.Email, UPPER(n.Email), 1, @PwdHash,
    REPLACE(UPPER(CONVERT(NVARCHAR(36), NEWID())), '-', ''),
    LOWER(CONVERT(NVARCHAR(36), NEWID())),
    '+971-50-' + RIGHT('0000000' + CONVERT(NVARCHAR(7), ABS(CHECKSUM(NEWID())) % 10000000), 7),
    0, 0, 1, 0
FROM @NewUsers n
JOIN Entitys e ON e.Name = n.EntityName
WHERE NOT EXISTS (SELECT 1 FROM AspNetUsers u WHERE u.NormalizedEmail = UPPER(n.Email));

-- Assign roles
INSERT INTO AspNetUserRoles (UserId, RoleId)
SELECT n.Id, n.Role FROM @NewUsers n
WHERE EXISTS (SELECT 1 FROM AspNetUsers u WHERE u.Id = n.Id)
  AND NOT EXISTS (SELECT 1 FROM AspNetUserRoles ur WHERE ur.UserId = n.Id AND ur.RoleId = n.Role);

PRINT '-> Users and entities added.';

/* -------------------------------------------------------------
   3) PICK 250 TICKETS TO KEEP — random sample
   ------------------------------------------------------------- */
IF OBJECT_ID('tempdb..#Keep') IS NOT NULL DROP TABLE #Keep;
CREATE TABLE #Keep (Id INT PRIMARY KEY, RowNo INT NOT NULL);

INSERT INTO #Keep (Id, RowNo)
SELECT TOP 250 Id, ROW_NUMBER() OVER (ORDER BY NEWID())
FROM Tickets
WHERE IsDeleted = 0
ORDER BY NEWID();

/* -------------------------------------------------------------
   4) DELETE DEPENDENT ROWS for tickets NOT in the keep list
   ------------------------------------------------------------- */
-- AI logs depend on AI analyses (which point to tickets)
DELETE l FROM TicketAiAnalysisLogs l
JOIN TicketAiAnalyses a ON a.Id = l.TicketAiAnalysisId
WHERE a.TicketId NOT IN (SELECT Id FROM #Keep);

DELETE FROM TicketAiAnalyses     WHERE TicketId NOT IN (SELECT Id FROM #Keep);
DELETE FROM TicketSemanticEmbeddings WHERE TicketId NOT IN (SELECT Id FROM #Keep);
DELETE FROM TicketHistories      WHERE TicketId NOT IN (SELECT Id FROM #Keep);
DELETE FROM TicketAttachments    WHERE TicketId NOT IN (SELECT Id FROM #Keep);
DELETE FROM TicketComments       WHERE TicketId NOT IN (SELECT Id FROM #Keep);

-- Drop all parent->child links; we'll re-establish a few below
UPDATE Tickets SET ParentTicketId = NULL;

-- Now delete the unwanted tickets
DELETE FROM Tickets WHERE Id NOT IN (SELECT Id FROM #Keep);

PRINT '-> Trimmed tickets to 250.';

/* -------------------------------------------------------------
   5) BUILD POOLS of users for assignment
   ------------------------------------------------------------- */
IF OBJECT_ID('tempdb..#EndUsers') IS NOT NULL DROP TABLE #EndUsers;
IF OBJECT_ID('tempdb..#Agents')   IS NOT NULL DROP TABLE #Agents;

CREATE TABLE #EndUsers   (Id NVARCHAR(450), RowNo INT IDENTITY(0,1) PRIMARY KEY);
CREATE TABLE #Agents     (Id NVARCHAR(450), RowNo INT IDENTITY(0,1) PRIMARY KEY);
CREATE TABLE #EntityPool (Id INT,           RowNo INT IDENTITY(0,1) PRIMARY KEY);

INSERT INTO #EntityPool (Id)
SELECT Id FROM Entitys WHERE IsActive = 1 AND Name NOT LIKE 'Temp_%' ORDER BY Id;

INSERT INTO #EndUsers (Id)
SELECT DISTINCT u.Id FROM AspNetUsers u
JOIN AspNetUserRoles ur ON ur.UserId = u.Id
JOIN AspNetRoles r ON r.Id = ur.RoleId
WHERE r.Name IN ('EndUser') AND u.IsActive = 1;

INSERT INTO #Agents (Id)
SELECT DISTINCT u.Id FROM AspNetUsers u
JOIN AspNetUserRoles ur ON ur.UserId = u.Id
JOIN AspNetRoles r ON r.Id = ur.RoleId
WHERE r.Name IN ('SupportAgent', 'Support Agent', 'Admin', 'Manager') AND u.IsActive = 1;

DECLARE @EndUserCount INT = (SELECT COUNT(*) FROM #EndUsers);
DECLARE @AgentCount   INT = (SELECT COUNT(*) FROM #Agents);
DECLARE @EntityCount  INT = (SELECT COUNT(*) FROM #EntityPool);
PRINT '-> EndUsers pool: ' + CAST(@EndUserCount AS NVARCHAR) + ', Agents pool: ' + CAST(@AgentCount AS NVARCHAR) + ', Entity pool: ' + CAST(@EntityCount AS NVARCHAR);

/* -------------------------------------------------------------
   6) UPDATE EACH SURVIVING TICKET — varied creator/assignee/entity/etc.
      and CreatedAt spread across recent buckets.

      Bucket plan (250 total):
        RowNo 1-20    -> Today          (2026-05-14)
        RowNo 21-40   -> Yesterday      (2026-05-13)
        RowNo 41-80   -> This week      (2026-05-08..12)
        RowNo 81-130  -> Last week      (2026-05-01..07)
        RowNo 131-190 -> Late April     (2026-04-14..30)
        RowNo 191-230 -> Early April    (2026-04-01..13)
        RowNo 231-250 -> March          (2026-03-01..31)
   ------------------------------------------------------------- */

;WITH Recipe AS (
    SELECT
        k.Id AS TicketId,
        k.RowNo,
        -- CreatedAt date bucket
        CASE
            WHEN k.RowNo <= 20  THEN DATEADD(MINUTE, ((k.RowNo * 47) % 540) + 480, CAST('2026-05-14' AS DATETIME2))
            WHEN k.RowNo <= 40  THEN DATEADD(MINUTE, ((k.RowNo * 53) % 720) + 360, CAST('2026-05-13' AS DATETIME2))
            WHEN k.RowNo <= 80  THEN DATEADD(MINUTE, ((k.RowNo * 73) % 1380), DATEADD(DAY, ((k.RowNo - 41) % 5), CAST('2026-05-08' AS DATETIME2)))
            WHEN k.RowNo <= 130 THEN DATEADD(MINUTE, ((k.RowNo * 91) % 1380), DATEADD(DAY, ((k.RowNo - 81) % 7), CAST('2026-05-01' AS DATETIME2)))
            WHEN k.RowNo <= 190 THEN DATEADD(MINUTE, ((k.RowNo * 113) % 1380), DATEADD(DAY, ((k.RowNo - 131) % 17), CAST('2026-04-14' AS DATETIME2)))
            WHEN k.RowNo <= 230 THEN DATEADD(MINUTE, ((k.RowNo * 131) % 1380), DATEADD(DAY, ((k.RowNo - 191) % 13), CAST('2026-04-01' AS DATETIME2)))
            ELSE                     DATEADD(MINUTE, ((k.RowNo * 137) % 1380), DATEADD(DAY, ((k.RowNo - 231) % 31), CAST('2026-03-01' AS DATETIME2)))
        END AS NewCreatedAt,
        -- Status spread: depends on age bucket
        CASE
            WHEN k.RowNo <= 20 THEN -- today: New / Open / In Progress
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 1 WHEN 1 THEN 1 WHEN 2 THEN 1 WHEN 3 THEN 1
                    WHEN 4 THEN 2 WHEN 5 THEN 2 WHEN 6 THEN 2
                    WHEN 7 THEN 3 WHEN 8 THEN 3 ELSE 4 END
            WHEN k.RowNo <= 40 THEN -- yesterday
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 1 WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 2
                    WHEN 4 THEN 3 WHEN 5 THEN 3 WHEN 6 THEN 3 WHEN 7 THEN 4
                    WHEN 8 THEN 5 ELSE 2 END
            WHEN k.RowNo <= 80 THEN -- this week
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 2 WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 3
                    WHEN 4 THEN 3 WHEN 5 THEN 4 WHEN 6 THEN 4 WHEN 7 THEN 5
                    WHEN 8 THEN 5 ELSE 6 END
            WHEN k.RowNo <= 130 THEN -- last week
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 3 WHEN 1 THEN 3 WHEN 2 THEN 4 WHEN 3 THEN 4
                    WHEN 4 THEN 5 WHEN 5 THEN 5 WHEN 6 THEN 5 WHEN 7 THEN 6
                    WHEN 8 THEN 6 ELSE 7 END
            WHEN k.RowNo <= 190 THEN -- late April
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 5 WHEN 1 THEN 5 WHEN 2 THEN 5 WHEN 3 THEN 6
                    WHEN 4 THEN 6 WHEN 5 THEN 6 WHEN 6 THEN 6 WHEN 7 THEN 4
                    WHEN 8 THEN 7 ELSE 5 END
            ELSE -- early April + March: mostly closed/resolved
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 5 WHEN 1 THEN 5 WHEN 2 THEN 6 WHEN 3 THEN 6
                    WHEN 4 THEN 6 WHEN 5 THEN 6 WHEN 6 THEN 6 WHEN 7 THEN 7
                    WHEN 8 THEN 5 ELSE 6 END
        END AS NewStatusId,
        -- Priority spread: Medium ~40%, High ~30%, Low ~20%, Critical ~10%
        CASE (k.RowNo % 10)
            WHEN 0 THEN 2 WHEN 1 THEN 2 WHEN 2 THEN 2 WHEN 3 THEN 2
            WHEN 4 THEN 3 WHEN 5 THEN 3 WHEN 6 THEN 3
            WHEN 7 THEN 1 WHEN 8 THEN 1 ELSE 4 END AS NewPriorityId,
        -- Category cycles 1..15 (skip "Unknown"=16)
        ((k.RowNo - 1) % 15) + 1 AS NewCategoryId,
        -- Source cycles 1..6
        ((k.RowNo - 1) % 6) + 1  AS NewSourceId,
        -- Entity: cycle through active non-Temp entity pool
        (SELECT Id FROM #EntityPool WHERE RowNo = ((k.RowNo - 1) % @EntityCount)) AS NewEntityId,
        -- Creator: cycle through end users
        (SELECT Id FROM #EndUsers WHERE RowNo = ((k.RowNo - 1) % @EndUserCount)) AS NewCreatedBy,
        -- Assignee: cycle through agents, NULL for ~10% (modulo)
        CASE WHEN (k.RowNo % 11) = 0 THEN NULL
             ELSE (SELECT Id FROM #Agents WHERE RowNo = ((k.RowNo - 1) % @AgentCount))
        END AS NewAssignedTo
    FROM #Keep k
)
UPDATE t SET
    t.TicketNumber  = 'TCK-2026-' + RIGHT('00000' + CAST(r.RowNo AS NVARCHAR), 5),
    t.CreatedAt     = r.NewCreatedAt,
    t.StatusId      = r.NewStatusId,
    t.PriorityId    = r.NewPriorityId,
    t.CategoryId    = r.NewCategoryId,
    t.SourceId      = r.NewSourceId,
    t.EntityId      = r.NewEntityId,
    t.CreatedByUserId = r.NewCreatedBy,
    t.AssignedToUserId = r.NewAssignedTo,
    t.IsDeleted     = 0,
    t.ParentTicketId = NULL,
    -- Reset timestamps that depend on status; we'll backfill correctly below
    t.UpdatedAt     = NULL,
    t.ResolvedAt    = NULL,
    t.ClosedAt      = NULL,
    t.FirstRespondedAt = NULL,
    t.ResolvedByUserId = NULL,
    t.ResolutionApprovedAt = NULL,
    t.ResolutionApprovedByUserId = NULL,
    t.EscalatedAt   = NULL,
    t.EscalatedToUserId = NULL,
    t.EscalationLevel = NULL,
    t.IsSlaBreached = 0,
    t.DueDate       = DATEADD(DAY, 7, r.NewCreatedAt),
    t.FirstResponseDueAt = DATEADD(HOUR, 4, r.NewCreatedAt),
    t.ResolutionDueAt    = DATEADD(DAY, 5, r.NewCreatedAt)
FROM Tickets t
JOIN Recipe r ON r.TicketId = t.Id;

PRINT '-> Tickets bulk-rewritten.';

/* -------------------------------------------------------------
   7) DERIVE TIMESTAMPS THAT MUST MATCH STATUS
   ------------------------------------------------------------- */
-- FirstRespondedAt: any status >= "In Progress" got a first response
UPDATE Tickets
SET FirstRespondedAt = DATEADD(MINUTE, 30 + (ABS(CHECKSUM(NEWID())) % 240), CreatedAt)
WHERE StatusId IN (3, 4, 5, 6); -- In Progress, Pending, Resolved, Closed

-- ResolvedAt: Resolved + Closed
UPDATE Tickets
SET ResolvedAt = DATEADD(HOUR, 6 + (ABS(CHECKSUM(NEWID())) % 96),  CreatedAt),
    ResolvedByUserId = AssignedToUserId
WHERE StatusId IN (5, 6); -- Resolved, Closed

-- ClosedAt: only Closed (and Rejected)
UPDATE Tickets
SET ClosedAt = DATEADD(HOUR, 4 + (ABS(CHECKSUM(NEWID())) % 72), ISNULL(ResolvedAt, CreatedAt))
WHERE StatusId IN (6, 7); -- Closed, Rejected

-- UpdatedAt = max of available timestamps
UPDATE Tickets
SET UpdatedAt = (SELECT MAX(v) FROM (VALUES (CreatedAt),(FirstRespondedAt),(ResolvedAt),(ClosedAt)) AS V(v));

-- A few SLA breaches on older unresolved tickets
UPDATE Tickets
SET IsSlaBreached = 1
WHERE StatusId IN (2, 3, 4)
  AND CreatedAt < DATEADD(DAY, -14, CAST('2026-05-14' AS DATETIME2))
  AND (Id % 7) = 0;

-- Resolution summary for resolved/closed
UPDATE Tickets
SET ResolutionSummary = CASE (Id % 5)
        WHEN 0 THEN 'Root cause identified; configuration corrected and verified with the requester.'
        WHEN 1 THEN 'Applied vendor patch and restored normal operation; user confirmed.'
        WHEN 2 THEN 'Permissions reissued and access validated end-to-end.'
        WHEN 3 THEN 'Network route restored; service reachable from all reported sites.'
        ELSE        'Workaround provided and incident closed; long-term fix tracked separately.'
    END
WHERE StatusId IN (5, 6) AND ResolutionSummary IS NULL;

-- Pending reason for pending tickets
UPDATE Tickets
SET PendingReason = CASE (Id % 4)
        WHEN 0 THEN 'Awaiting vendor response.'
        WHEN 1 THEN 'Awaiting user confirmation on workaround.'
        WHEN 2 THEN 'Waiting on change-window approval.'
        ELSE        'Waiting on third-party data refresh.'
    END
WHERE StatusId = 4 AND PendingReason IS NULL;

-- Sprinkle some escalations on older High/Critical tickets
UPDATE Tickets
SET EscalatedAt = DATEADD(HOUR, 8, CreatedAt),
    EscalationLevel = 'Level 2',
    EscalatedToUserId = (SELECT TOP 1 Id FROM #Agents ORDER BY RowNo DESC)
WHERE PriorityId IN (3, 4)
  AND StatusId IN (3, 4, 5, 6)
  AND (Id % 9) = 0;

PRINT '-> Status-dependent timestamps backfilled.';

/* -------------------------------------------------------------
   8) Rebuild parent->child for ~12 tickets so we have hierarchies
   ------------------------------------------------------------- */
;WITH NumberedTickets AS (
    SELECT t.Id, ROW_NUMBER() OVER (ORDER BY t.CreatedAt) AS rn
    FROM Tickets t
)
UPDATE t
SET ParentTicketId = p.Id
FROM Tickets t
JOIN NumberedTickets c ON c.Id = t.Id
JOIN NumberedTickets p ON p.rn = c.rn - 1
WHERE c.rn IN (50, 75, 100, 125, 150, 175, 200, 220, 230, 240, 245, 248);

PRINT '-> Parent-child links added.';

COMMIT TRANSACTION;
PRINT '== DONE ==';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @msg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ln  INT = ERROR_LINE();
    RAISERROR('FAILED at line %d: %s', 16, 1, @ln, @msg);
END CATCH;
