-- Per-shape summary for a given SessionId. Pass the session via :sessId
-- (or hard-code at the top). Outputs: total / passed / failed / pass-rate per shape category.

DECLARE @sessId INT = 110;

SET NOCOUNT ON;

-- Map each trace's CaseCode to its shape category (FIRST hyphen-separated token).
WITH shaped AS (
    SELECT
        CaseCode,
        CASE
            WHEN CaseCode LIKE 'COUNT-%'    THEN 'COUNT'
            WHEN CaseCode LIKE 'LKP-%'      THEN 'LOOKUP'
            WHEN CaseCode LIKE 'FLT-%'      THEN 'FILTER'
            WHEN CaseCode LIKE 'AGG-%'      THEN 'AGGREGATE'
            WHEN CaseCode LIKE 'TOPN-%'     THEN 'TOPN'
            WHEN CaseCode LIKE 'JOIN-%'     THEN 'JOIN'
            WHEN CaseCode LIKE 'TS-%'       THEN 'TIMESERIES'
            WHEN CaseCode LIKE 'CMP-%'      THEN 'COMPARE'
            WHEN CaseCode LIKE 'WIN-%'      THEN 'WINDOW'
            WHEN CaseCode LIKE 'EX-%' OR CaseCode LIKE 'NEX-%' THEN 'EXISTS'
            WHEN CaseCode LIKE 'HAV-%'      THEN 'HAVING'
            WHEN CaseCode LIKE 'SELF-%'     THEN 'SELFJOIN'
            WHEN CaseCode LIKE 'UNION-%'    THEN 'UNION'
            WHEN CaseCode LIKE 'REC-%'      THEN 'RECURSIVE'
            WHEN CaseCode LIKE 'SAFETY-%'   THEN 'SAFETY'
            ELSE 'OTHER'
        END AS Shape,
        ErrorMessage,
        GeneratedScript
    FROM CopilotTraceHistories
    WHERE SessionId = @sessId AND CaseCode IS NOT NULL
)
SELECT
    Shape,
    COUNT(*) AS Total,
    SUM(CASE WHEN ErrorMessage IS NULL THEN 1 ELSE 0 END) AS ExecutionPass,
    SUM(CASE WHEN ErrorMessage IS NOT NULL THEN 1 ELSE 0 END) AS ExecutionFail,
    CAST(SUM(CASE WHEN ErrorMessage IS NULL THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS DECIMAL(5,1)) AS ExecPassPct
FROM shaped
GROUP BY Shape
ORDER BY Shape;

-- Failure detail
PRINT '--- All failures ---';
SELECT CaseCode, LEFT(Question, 60) AS Q, LEFT(ErrorMessage, 180) AS Err
FROM CopilotTraceHistories
WHERE SessionId = @sessId AND ErrorMessage IS NOT NULL
ORDER BY CaseCode;
