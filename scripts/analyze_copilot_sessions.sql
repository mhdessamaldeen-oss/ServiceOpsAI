-- Analyze Copilot Sessions 1, 0, 3, 7 for Assessment Quality
-- This queries trace history to evaluate answer quality

SELECT 
    th.Id AS TraceId,
    th.SessionId,
    th.CaseCode,
    th.Question,
    th.Answer,
    th.ModelName,
    th.Intent,
    th.IsSuccess,
    th.TotalElapsedMs,
    th.AssessmentActualMode,
    th.AssessmentActualIntent,
    th.AssessmentActualTool,
    th.AssessmentDetail,
    th.CreatedAt,
    th.SystemVersion,
    th.BuildFingerprint
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
ORDER BY th.SessionId, th.CreatedAt;

-- Summary by Session
SELECT 
    SessionId,
    COUNT(*) AS TotalQuestions,
    SUM(CASE WHEN IsSuccess = 1 THEN 1 ELSE 0 END) AS SuccessCount,
    SUM(CASE WHEN IsSuccess = 0 THEN 1 ELSE 0 END) AS FailureCount,
    SUM(CASE WHEN IsSuccess IS NULL THEN 1 ELSE 0 END) AS UnknownCount,
    AVG(CAST(TotalElapsedMs AS FLOAT)) AS AvgLatencyMs,
    COUNT(DISTINCT ModelName) AS ModelsUsed,
    COUNT(DISTINCT Intent) AS IntentsDetected
FROM CopilotTraceHistories
WHERE SessionId IN (1, 0, 3, 7)
GROUP BY SessionId
ORDER BY SessionId;

-- Assessment Quality Breakdown
SELECT 
    SessionId,
    CaseCode,
    AssessmentActualMode,
    AssessmentActualIntent,
    COUNT(*) AS Count,
    AVG(CAST(TotalElapsedMs AS FLOAT)) AS AvgLatencyMs
FROM CopilotTraceHistories
WHERE SessionId IN (1, 0, 3, 7)
    AND AssessmentActualMode IS NOT NULL
GROUP BY SessionId, CaseCode, AssessmentActualMode, AssessmentActualIntent
ORDER BY SessionId, CaseCode;

-- Failed or Null Answers Detail
SELECT 
    th.Id,
    th.SessionId,
    th.CaseCode,
    th.Question,
    th.Answer,
    th.IsSuccess,
    th.AssessmentDetail,
    th.ModelName
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
    AND (th.IsSuccess = 0 OR th.IsSuccess IS NULL OR th.Answer IS NULL OR th.Answer = '')  COLLATE Latin1_General_CI_AS
ORDER BY th.SessionId, th.CreatedAt;
