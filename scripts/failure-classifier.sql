-- Auto-classifies failures in a session by error-message pattern.
-- Buckets every failure into one of: model / data / code / infra / refusal-ok.
DECLARE @sessId INT = 113;

SET NOCOUNT ON;

WITH classified AS (
    SELECT
        CaseCode,
        LEFT(Question, 60) AS Q,
        LEFT(ErrorMessage, 150) AS Err,
        CASE
            -- Model-side
            WHEN ErrorMessage LIKE 'LLM produced unparseable%' THEN 'model:unparseable-json'
            WHEN ErrorMessage LIKE 'spec extraction failed%'  THEN 'model:spec-extraction'
            WHEN ErrorMessage LIKE 'I couldn''t understand%'   THEN 'model:no-understand'
            -- Infra
            WHEN ErrorMessage LIKE '%404 (Not Found)%'        THEN 'infra:ollama-404'
            WHEN ErrorMessage LIKE '%status code%'            THEN 'infra:http'
            WHEN ErrorMessage LIKE '%timeout%' OR ErrorMessage LIKE '%timed out%' THEN 'infra:timeout'
            -- Data / coverage
            WHEN ErrorMessage LIKE 'no candidate tables%'     THEN 'data:retriever-miss'
            -- Code-side: SQL execution errors
            WHEN ErrorMessage LIKE 'Conversion failed%date%'   THEN 'code:date-coercion'
            WHEN ErrorMessage LIKE 'Conversion failed%'        THEN 'code:type-coercion'
            WHEN ErrorMessage LIKE '%not contained in either an aggregate%' THEN 'code:select-groupby-mismatch'
            WHEN ErrorMessage LIKE '%multi-part identifier%could not be bound%' THEN 'code:wrong-table-or-alias'
            WHEN ErrorMessage LIKE '%Invalid column name%'   THEN 'code:wrong-column'
            WHEN ErrorMessage LIKE '%invalid for%operator%'   THEN 'code:wrong-aggregate-type'
            WHEN ErrorMessage LIKE 'Spec has distinct%degenerate%' THEN 'code:distinct-degenerate'
            WHEN ErrorMessage LIKE 'one or more sub-questions%' THEN 'code:decompose-cascade'
            -- Refusal (safety / PII)
            WHEN ErrorMessage LIKE 'select references blocked column%' THEN 'safety:pii-blocked-ok'
            WHEN ErrorMessage LIKE '%write intent%' OR ErrorMessage LIKE '%DML%' THEN 'safety:write-refused-ok'
            -- Default
            ELSE 'unclassified'
        END AS FailClass
    FROM CopilotTraceHistories
    WHERE SessionId = @sessId AND ErrorMessage IS NOT NULL
)
SELECT FailClass, COUNT(*) AS N, STRING_AGG(CaseCode, ', ') WITHIN GROUP (ORDER BY CaseCode) AS Cases
FROM classified
GROUP BY FailClass
ORDER BY N DESC;

PRINT '--- Details ---';
SELECT CaseCode, Q, Err FROM (
    SELECT CaseCode, LEFT(Question, 60) AS Q, LEFT(ErrorMessage, 150) AS Err
    FROM CopilotTraceHistories
    WHERE SessionId = @sessId AND ErrorMessage IS NOT NULL
) f
ORDER BY CaseCode;
