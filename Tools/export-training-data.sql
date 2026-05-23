-- ============================================================================
-- export-training-data.sql
--   Extracts the (Question, GeneratedScript) pairs that will become the
--   fine-tune corpus for qwen2.5-coder:7b → copilot-nl2sql-v1.
--
--   Source rule: keep only traces tagged with a `pre-fine-tune-baseline-*`
--   label AND whose CaseCode did NOT appear in the FailedCaseCodes JSON array
--   of its run summary. That way we only train on examples the assessor
--   judged correct end-to-end (not just "the pipeline didn't crash").
--
--   Output columns are intentionally minimal — finetune\build_dataset.py
--   reads them via bcp / sqlcmd -W -s "|" and converts to JSONL.
-- ============================================================================

SET NOCOUNT ON;

WITH FailedCaseList AS (
    -- Explode every FailedCaseCodes JSON array into rows of (RunId, CaseCode).
    -- OPENJSON keeps this engine-side so we don't need to parse JSON in app code.
    SELECT s.RunId, jval.[value] AS CaseCode
    FROM   CopilotAssessmentRunSummaries s
    CROSS APPLY OPENJSON(s.FailedCaseCodes) AS jval
    WHERE  s.BaselineLabel IS NOT NULL
),
RunForTrace AS (
    -- Each trace belongs to the most recent run summary whose RunAt is at or
    -- before the trace's CreatedAt for the same SourceSuite. The cross-apply
    -- enforces a one-to-one mapping so we don't double-count.
    SELECT t.Id, t.CaseCode, t.SourceSuite, t.BaselineLabel,
           t.Question, t.GeneratedScript, t.Answer, t.ModelName,
           r.RunId
    FROM   CopilotTraceHistories t
    OUTER APPLY (
        SELECT TOP 1 s.RunId
        FROM   CopilotAssessmentRunSummaries s
        WHERE  s.BaselineLabel = t.BaselineLabel
          AND  s.RunAt >= t.CreatedAt
        ORDER  BY s.RunAt ASC
    ) r
    WHERE  t.BaselineLabel IS NOT NULL
      AND  t.GeneratedScript IS NOT NULL
      AND  t.ErrorMessage IS NULL
      AND  LEN(LTRIM(RTRIM(t.GeneratedScript))) > 10
)
SELECT  rft.Id            AS TraceId,
        rft.CaseCode      AS CaseCode,
        rft.SourceSuite   AS SourceSuite,
        rft.BaselineLabel AS BaselineLabel,
        rft.Question      AS Question,
        rft.GeneratedScript AS Sql
FROM    RunForTrace rft
LEFT JOIN FailedCaseList f
       ON f.RunId = rft.RunId AND f.CaseCode = rft.CaseCode
WHERE   f.CaseCode IS NULL   -- exclude cases that failed in their run
ORDER BY rft.SourceSuite, rft.CaseCode, rft.Id;
