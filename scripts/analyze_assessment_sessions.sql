-- ============================================================
-- ASSESSMENT ANALYSIS: Sessions 1, 0, 3, 7
-- This analyzes the Agentic Copilot assessment results
-- ============================================================

-- 1. OVERALL SESSION SUMMARY
SELECT 
    'SESSION SUMMARY' AS AnalysisType,
    th.SessionId,
    COUNT(*) AS TotalQuestions,
    SUM(CASE WHEN th.IsSuccess = 1 THEN 1 ELSE 0 END) AS PassedCount,
    SUM(CASE WHEN th.IsSuccess = 0 THEN 1 ELSE 0 END) AS FailedCount,
    SUM(CASE WHEN th.IsSuccess IS NULL THEN 1 ELSE 0 END) AS UngradedCount,
    CAST(SUM(CASE WHEN th.IsSuccess = 1 THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS DECIMAL(5,2)) AS SuccessRatePct,
    AVG(CAST(th.TotalElapsedMs AS FLOAT)) AS AvgLatencyMs,
    MAX(th.TotalElapsedMs) AS MaxLatencyMs,
    COUNT(DISTINCT th.CaseCode) AS UniqueCases,
    STRING_AGG(DISTINCT th.ModelName, ', ') AS ModelsUsed
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
GROUP BY th.SessionId
ORDER BY th.SessionId;

-- 2. FAILURE ANALYSIS BY CASE
SELECT 
    'FAILURE DETAILS' AS AnalysisType,
    th.SessionId,
    th.CaseCode,
    th.Question,
    th.Answer,
    th.IsSuccess,
    th.AssessmentActualMode,
    th.AssessmentActualIntent,
    th.AssessmentDetail,
    th.TotalElapsedMs,
    th.ModelName
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
    AND th.IsSuccess = 0
ORDER BY th.SessionId, th.CaseCode;

-- 3. INTENT DETECTION ACCURACY
SELECT 
    'INTENT ANALYSIS' AS AnalysisType,
    th.SessionId,
    th.AssessmentActualIntent,
    COUNT(*) AS Count,
    SUM(CASE WHEN th.IsSuccess = 1 THEN 1 ELSE 0 END) AS SuccessCount,
    CAST(SUM(CASE WHEN th.IsSuccess = 1 THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS DECIMAL(5,2)) AS SuccessRatePct
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
    AND th.AssessmentActualIntent IS NOT NULL
GROUP BY th.SessionId, th.AssessmentActualIntent
ORDER BY th.SessionId, Count DESC;

-- 4. MODE DISTRIBUTION
SELECT 
    'MODE USAGE' AS AnalysisType,
    th.SessionId,
    th.AssessmentActualMode,
    COUNT(*) AS Count,
    AVG(CAST(th.TotalElapsedMs AS FLOAT)) AS AvgLatencyMs
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
    AND th.AssessmentActualMode IS NOT NULL
GROUP BY th.SessionId, th.AssessmentActualMode
ORDER BY th.SessionId, Count DESC;

-- 5. CLARIFICATION/INVALID REQUESTS
SELECT 
    'CLARIFICATION/INVALID' AS AnalysisType,
    th.SessionId,
    th.CaseCode,
    th.Question,
    th.Answer,
    th.AssessmentActualIntent,
    th.AssessmentDetail,
    CASE 
        WHEN th.Answer LIKE '%clarify%' OR th.Answer LIKE '%Please clarify%' THEN 'Clarification Requested'
        WHEN th.Answer LIKE '%can''t help%' OR th.Answer LIKE '%unsupported%' THEN 'Rejected'
        ELSE 'Other'
    END AS ResponseType
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
    AND (th.AssessmentActualIntent LIKE '%Clarification%' 
         OR th.AssessmentActualIntent LIKE '%Unsupported%'
         OR th.Answer LIKE '%clarify%'
         OR th.Answer LIKE '%can''t help%'
         OR th.Answer LIKE '%unsupported%')
ORDER BY th.SessionId, th.CaseCode;

-- 6. EXECUTION DETAILS PARSING (JSON Analysis)
-- This extracts specific failure reasons from AssessmentDetail
SELECT 
    'FAILURE CATEGORIES' AS AnalysisType,
    th.SessionId,
    th.CaseCode,
    th.Question,
    th.AssessmentDetail,
    CASE 
        WHEN th.AssessmentDetail LIKE '%entity expected%' THEN 'Entity Mismatch'
        WHEN th.AssessmentDetail LIKE '%intent expected%' THEN 'Intent Mismatch'
        WHEN th.AssessmentDetail LIKE '%mode expected%' THEN 'Mode Mismatch'
        WHEN th.AssessmentDetail LIKE '%tool expected%' THEN 'Tool Mismatch'
        WHEN th.AssessmentDetail LIKE '%answer did not contain%' THEN 'Answer Quality'
        WHEN th.AssessmentDetail LIKE '%expected clarification%' THEN 'Clarification Handling'
        WHEN th.AssessmentDetail LIKE '%result(s)%' THEN 'Result Count Mismatch'
        WHEN th.AssessmentDetail LIKE '%latency exceeded%' THEN 'Performance Timeout'
        ELSE 'Other/Unknown'
    END AS FailureCategory
FROM CopilotTraceHistories th
WHERE th.SessionId IN (1, 0, 3, 7)
    AND th.IsSuccess = 0
    AND th.AssessmentDetail IS NOT NULL
ORDER BY th.SessionId, FailureCategory;

-- 7. LATEST RUN PER SESSION (with version info)
SELECT 
    'VERSION INFO' AS AnalysisType,
    SessionId,
    MAX(CreatedAt) AS LatestRunAt,
    MAX(SystemVersion) AS SystemVersion,
    MAX(BuildFingerprint) AS BuildFingerprint,
    COUNT(DISTINCT SystemVersion) AS VersionChanges
FROM CopilotTraceHistories
WHERE SessionId IN (1, 0, 3, 7)
GROUP BY SessionId;

-- 8. COMPARISON TOOL REGRESSION CHECK
-- Compare if same case passed in one session but failed in another
WITH CaseResults AS (
    SELECT 
        th.CaseCode,
        th.SessionId,
        th.IsSuccess,
        th.AssessmentDetail,
        ROW_NUMBER() OVER (PARTITION BY th.CaseCode, th.SessionId ORDER BY th.CreatedAt DESC) AS rn
    FROM CopilotTraceHistories th
    WHERE th.SessionId IN (1, 0, 3, 7)
        AND th.CaseCode IS NOT NULL
)
SELECT 
    'REGRESSION ANALYSIS' AS AnalysisType,
    cr1.CaseCode,
    MAX(CASE WHEN cr1.SessionId = 0 THEN cr1.IsSuccess END) AS Session0_Result,
    MAX(CASE WHEN cr1.SessionId = 1 THEN cr1.IsSuccess END) AS Session1_Result,
    MAX(CASE WHEN cr1.SessionId = 3 THEN cr1.IsSuccess END) AS Session3_Result,
    MAX(CASE WHEN cr1.SessionId = 7 THEN cr1.IsSuccess END) AS Session7_Result,
    CASE 
        WHEN COUNT(DISTINCT cr1.IsSuccess) > 1 THEN 'INCONSISTENT'
        WHEN MAX(cr1.IsSuccess) = 1 THEN 'STABLE PASS'
        WHEN MAX(cr1.IsSuccess) = 0 THEN 'STABLE FAIL'
        ELSE 'UNKNOWN'
    END AS Stability
FROM CaseResults cr1
WHERE cr1.rn = 1
GROUP BY cr1.CaseCode
HAVING COUNT(*) > 1  -- Only show cases that appear in multiple sessions
ORDER BY 
    CASE WHEN COUNT(DISTINCT cr1.IsSuccess) > 1 THEN 0 ELSE 1 END,
    cr1.CaseCode;
