/* Re-number the 250 remaining tickets 1..250 and re-apply date / status spread. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;

BEGIN TRY
BEGIN TRANSACTION;

IF OBJECT_ID('tempdb..#Renum') IS NOT NULL DROP TABLE #Renum;
CREATE TABLE #Renum (Id INT PRIMARY KEY, RowNo INT NOT NULL);

INSERT INTO #Renum (Id, RowNo)
SELECT Id, ROW_NUMBER() OVER (ORDER BY NEWID())
FROM Tickets;

IF OBJECT_ID('tempdb..#EndUsers') IS NOT NULL DROP TABLE #EndUsers;
IF OBJECT_ID('tempdb..#Agents')   IS NOT NULL DROP TABLE #Agents;
IF OBJECT_ID('tempdb..#EntityPool') IS NOT NULL DROP TABLE #EntityPool;

CREATE TABLE #EndUsers   (Id NVARCHAR(450), RowNo INT IDENTITY(0,1) PRIMARY KEY);
CREATE TABLE #Agents     (Id NVARCHAR(450), RowNo INT IDENTITY(0,1) PRIMARY KEY);
CREATE TABLE #EntityPool (Id INT,           RowNo INT IDENTITY(0,1) PRIMARY KEY);

INSERT INTO #EndUsers (Id)
SELECT DISTINCT u.Id FROM AspNetUsers u
JOIN AspNetUserRoles ur ON ur.UserId = u.Id
JOIN AspNetRoles r ON r.Id = ur.RoleId
WHERE r.Name = 'EndUser' AND u.IsActive = 1;

INSERT INTO #Agents (Id)
SELECT DISTINCT u.Id FROM AspNetUsers u
JOIN AspNetUserRoles ur ON ur.UserId = u.Id
JOIN AspNetRoles r ON r.Id = ur.RoleId
WHERE r.Name IN ('SupportAgent', 'Support Agent', 'Admin', 'Manager') AND u.IsActive = 1;

INSERT INTO #EntityPool (Id)
SELECT Id FROM Entitys WHERE IsActive = 1 AND Name NOT LIKE 'Temp_%' ORDER BY Id;

DECLARE @EndUserCount INT = (SELECT COUNT(*) FROM #EndUsers);
DECLARE @AgentCount   INT = (SELECT COUNT(*) FROM #Agents);
DECLARE @EntityCount  INT = (SELECT COUNT(*) FROM #EntityPool);

;WITH Recipe AS (
    SELECT
        k.Id AS TicketId,
        k.RowNo,
        CASE
            WHEN k.RowNo <= 20  THEN DATEADD(MINUTE, ((k.RowNo * 47) % 540) + 480, CAST('2026-05-14' AS DATETIME2))
            WHEN k.RowNo <= 40  THEN DATEADD(MINUTE, ((k.RowNo * 53) % 720) + 360, CAST('2026-05-13' AS DATETIME2))
            WHEN k.RowNo <= 80  THEN DATEADD(MINUTE, ((k.RowNo * 73) % 1380), DATEADD(DAY, ((k.RowNo - 41) % 5), CAST('2026-05-08' AS DATETIME2)))
            WHEN k.RowNo <= 130 THEN DATEADD(MINUTE, ((k.RowNo * 91) % 1380), DATEADD(DAY, ((k.RowNo - 81) % 7), CAST('2026-05-01' AS DATETIME2)))
            WHEN k.RowNo <= 190 THEN DATEADD(MINUTE, ((k.RowNo * 113) % 1380), DATEADD(DAY, ((k.RowNo - 131) % 17), CAST('2026-04-14' AS DATETIME2)))
            WHEN k.RowNo <= 230 THEN DATEADD(MINUTE, ((k.RowNo * 131) % 1380), DATEADD(DAY, ((k.RowNo - 191) % 13), CAST('2026-04-01' AS DATETIME2)))
            ELSE                     DATEADD(MINUTE, ((k.RowNo * 137) % 1380), DATEADD(DAY, ((k.RowNo - 231) % 31), CAST('2026-03-01' AS DATETIME2)))
        END AS NewCreatedAt,
        CASE
            WHEN k.RowNo <= 20 THEN
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 1 WHEN 1 THEN 1 WHEN 2 THEN 1 WHEN 3 THEN 1
                    WHEN 4 THEN 2 WHEN 5 THEN 2 WHEN 6 THEN 2
                    WHEN 7 THEN 3 WHEN 8 THEN 3 ELSE 4 END
            WHEN k.RowNo <= 40 THEN
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 1 WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 2
                    WHEN 4 THEN 3 WHEN 5 THEN 3 WHEN 6 THEN 3 WHEN 7 THEN 4
                    WHEN 8 THEN 5 ELSE 2 END
            WHEN k.RowNo <= 80 THEN
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 2 WHEN 1 THEN 2 WHEN 2 THEN 3 WHEN 3 THEN 3
                    WHEN 4 THEN 3 WHEN 5 THEN 4 WHEN 6 THEN 4 WHEN 7 THEN 5
                    WHEN 8 THEN 5 ELSE 6 END
            WHEN k.RowNo <= 130 THEN
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 3 WHEN 1 THEN 3 WHEN 2 THEN 4 WHEN 3 THEN 4
                    WHEN 4 THEN 5 WHEN 5 THEN 5 WHEN 6 THEN 5 WHEN 7 THEN 6
                    WHEN 8 THEN 6 ELSE 7 END
            WHEN k.RowNo <= 190 THEN
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 5 WHEN 1 THEN 5 WHEN 2 THEN 5 WHEN 3 THEN 6
                    WHEN 4 THEN 6 WHEN 5 THEN 6 WHEN 6 THEN 6 WHEN 7 THEN 4
                    WHEN 8 THEN 7 ELSE 5 END
            ELSE
                CASE (k.RowNo % 10)
                    WHEN 0 THEN 5 WHEN 1 THEN 5 WHEN 2 THEN 6 WHEN 3 THEN 6
                    WHEN 4 THEN 6 WHEN 5 THEN 6 WHEN 6 THEN 6 WHEN 7 THEN 7
                    WHEN 8 THEN 5 ELSE 6 END
        END AS NewStatusId,
        CASE (k.RowNo % 10)
            WHEN 0 THEN 2 WHEN 1 THEN 2 WHEN 2 THEN 2 WHEN 3 THEN 2
            WHEN 4 THEN 3 WHEN 5 THEN 3 WHEN 6 THEN 3
            WHEN 7 THEN 1 WHEN 8 THEN 1 ELSE 4 END AS NewPriorityId,
        ((k.RowNo - 1) % 15) + 1 AS NewCategoryId,
        ((k.RowNo - 1) % 6) + 1  AS NewSourceId,
        (SELECT Id FROM #EntityPool WHERE RowNo = ((k.RowNo - 1) % @EntityCount)) AS NewEntityId,
        (SELECT Id FROM #EndUsers   WHERE RowNo = ((k.RowNo - 1) % @EndUserCount)) AS NewCreatedBy,
        CASE WHEN (k.RowNo % 11) = 0 THEN NULL
             ELSE (SELECT Id FROM #Agents WHERE RowNo = ((k.RowNo - 1) % @AgentCount))
        END AS NewAssignedTo
    FROM #Renum k
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

-- Re-derive status-dependent timestamps
UPDATE Tickets SET FirstRespondedAt = DATEADD(MINUTE, 30 + (ABS(CHECKSUM(NEWID())) % 240), CreatedAt)
WHERE StatusId IN (3, 4, 5, 6);

UPDATE Tickets SET ResolvedAt = DATEADD(HOUR, 6 + (ABS(CHECKSUM(NEWID())) % 96),  CreatedAt),
                   ResolvedByUserId = AssignedToUserId
WHERE StatusId IN (5, 6);

UPDATE Tickets SET ClosedAt = DATEADD(HOUR, 4 + (ABS(CHECKSUM(NEWID())) % 72), ISNULL(ResolvedAt, CreatedAt))
WHERE StatusId IN (6, 7);

UPDATE Tickets SET UpdatedAt = (SELECT MAX(v) FROM (VALUES (CreatedAt),(FirstRespondedAt),(ResolvedAt),(ClosedAt)) AS V(v));

UPDATE Tickets SET IsSlaBreached = 1
WHERE StatusId IN (2, 3, 4)
  AND CreatedAt < DATEADD(DAY, -14, CAST('2026-05-14' AS DATETIME2))
  AND (Id % 7) = 0;

UPDATE Tickets SET ResolutionSummary = CASE (Id % 5)
        WHEN 0 THEN 'Root cause identified; configuration corrected and verified with the requester.'
        WHEN 1 THEN 'Applied vendor patch and restored normal operation; user confirmed.'
        WHEN 2 THEN 'Permissions reissued and access validated end-to-end.'
        WHEN 3 THEN 'Network route restored; service reachable from all reported sites.'
        ELSE        'Workaround provided and incident closed; long-term fix tracked separately.'
    END
WHERE StatusId IN (5, 6);

UPDATE Tickets SET PendingReason = CASE (Id % 4)
        WHEN 0 THEN 'Awaiting vendor response.'
        WHEN 1 THEN 'Awaiting user confirmation on workaround.'
        WHEN 2 THEN 'Waiting on change-window approval.'
        ELSE        'Waiting on third-party data refresh.'
    END
WHERE StatusId = 4;

UPDATE Tickets
SET EscalatedAt = DATEADD(HOUR, 8, CreatedAt),
    EscalationLevel = 'Level 2',
    EscalatedToUserId = (SELECT TOP 1 Id FROM #Agents ORDER BY RowNo DESC)
WHERE PriorityId IN (3, 4)
  AND StatusId IN (3, 4, 5, 6)
  AND (Id % 9) = 0;

-- Rebuild parent->child for 12 tickets
UPDATE Tickets SET ParentTicketId = NULL;
;WITH NumberedTickets AS (
    SELECT t.Id, ROW_NUMBER() OVER (ORDER BY t.CreatedAt) AS rn FROM Tickets t
)
UPDATE t
SET ParentTicketId = p.Id
FROM Tickets t
JOIN NumberedTickets c ON c.Id = t.Id
JOIN NumberedTickets p ON p.rn = c.rn - 1
WHERE c.rn IN (50, 75, 100, 125, 150, 175, 200, 220, 230, 240, 245, 248);

COMMIT TRANSACTION;
PRINT '== Date distribution fixed ==';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @msg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ln  INT = ERROR_LINE();
    RAISERROR('FAILED at line %d: %s', 16, 1, @ln, @msg);
END CATCH;
