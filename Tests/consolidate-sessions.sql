-- Consolidate per-case sessions into one canonical session per suite-run.
-- Per-case sessions have title: "Assessment Case [<code>] (Run <runId>)"
-- For each runId:
--   1. Pick the MIN session-id case session as the canonical session.
--   2. Move all messages from the other per-case sessions to the canonical session.
--   3. Soft-delete the other per-case sessions and the orphan run-level "Automated Assessment" session.
--   4. Update the canonical session title to "Assessment Run [<suite-name>] <date>".
SET NOCOUNT ON;

DECLARE @runId UNIQUEIDENTIFIER;
DECLARE @runIdStr NVARCHAR(64);
DECLARE @canonical INT;
DECLARE @suiteName NVARCHAR(200);
DECLARE @runAt DATETIME2;
DECLARE @newTitle NVARCHAR(400);
DECLARE @movedMsgs INT;
DECLARE @killedSessions INT;

DECLARE runCur CURSOR FOR
    SELECT DISTINCT RunId FROM CopilotAssessmentRunSummaries WHERE RunId IS NOT NULL
    ORDER BY RunId;

OPEN runCur;
FETCH NEXT FROM runCur INTO @runId;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @runIdStr = CONVERT(NVARCHAR(64), @runId);

    -- Find the canonical session (lowest Id among per-case sessions for this run)
    SELECT @canonical = MIN(Id)
    FROM CopilotChatSessions
    WHERE Title LIKE '%(Run ' + @runIdStr + ')%' AND IsDeleted = 0;

    IF @canonical IS NOT NULL
    BEGIN
        -- Pick a representative trace to learn the suite name and timestamp
        SELECT TOP 1 @suiteName = SourceSuite, @runAt = CreatedAt
        FROM CopilotTraceHistories
        WHERE CaseCode IN (
            SELECT SUBSTRING(Title, CHARINDEX('[', Title)+1, CHARINDEX(']', Title) - CHARINDEX('[', Title) - 1)
            FROM CopilotChatSessions
            WHERE Title LIKE '%(Run ' + @runIdStr + ')%' AND IsDeleted = 0
        )
        ORDER BY CreatedAt;

        SET @newTitle = 'Assessment Run [' + ISNULL(@suiteName, 'unknown') + '] '
            + CONVERT(NVARCHAR(20), ISNULL(@runAt, GETUTCDATE()), 120);

        -- 1) Reassign all messages from other case sessions to canonical
        UPDATE m
        SET m.SessionId = @canonical
        FROM CopilotChatMessages m
        INNER JOIN CopilotChatSessions s ON s.Id = m.SessionId
        WHERE s.Title LIKE '%(Run ' + @runIdStr + ')%'
          AND s.Id <> @canonical;
        SET @movedMsgs = @@ROWCOUNT;

        -- 2) Soft-delete the now-empty per-case sessions (but NOT the canonical)
        UPDATE CopilotChatSessions
        SET IsDeleted = 1
        WHERE Title LIKE '%(Run ' + @runIdStr + ')%'
          AND Id <> @canonical;
        SET @killedSessions = @@ROWCOUNT;

        -- 3) Update canonical title
        UPDATE CopilotChatSessions
        SET Title = @newTitle, LastInteractionAt = GETUTCDATE()
        WHERE Id = @canonical;

        PRINT 'Run ' + @runIdStr + ' -> canonical session ' + CONVERT(NVARCHAR(10), @canonical)
            + ', moved ' + CONVERT(NVARCHAR(10), @movedMsgs) + ' messages, soft-deleted '
            + CONVERT(NVARCHAR(10), @killedSessions) + ' per-case sessions.';
    END

    FETCH NEXT FROM runCur INTO @runId;
END

CLOSE runCur;
DEALLOCATE runCur;

-- Also soft-delete the empty run-level "Automated Assessment" sessions (no messages, only created for SignalR scope)
UPDATE CopilotChatSessions
SET IsDeleted = 1
WHERE IsDeleted = 0
  AND Title LIKE 'Automated Assessment:%'
  AND NOT EXISTS (SELECT 1 FROM CopilotChatMessages m WHERE m.SessionId = CopilotChatSessions.Id);

PRINT '--- Final per-suite session check ---';
SELECT s.Id AS SessionId, s.Title, COUNT(m.Id) AS Messages
FROM CopilotChatSessions s
LEFT JOIN CopilotChatMessages m ON m.SessionId = s.Id
WHERE s.IsAssessment = 1 AND s.IsDeleted = 0
GROUP BY s.Id, s.Title
ORDER BY s.Id;
