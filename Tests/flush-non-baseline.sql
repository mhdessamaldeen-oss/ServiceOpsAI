-- Selective flush — keep the pre-fine-tune baseline rows, drop everything else.
-- Run this AFTER fine-tuning when you want to clean post-train clutter while
-- preserving the 2026-05-19 baseline for before/after comparison.

SET NOCOUNT ON;
DECLARE @keepLabel NVARCHAR(100) = 'pre-fine-tune-baseline-2026-05-19';

-- Delete non-baseline trace history rows
DELETE FROM CopilotTraceHistories WHERE BaselineLabel IS NULL OR BaselineLabel <> @keepLabel;
SELECT 'Traces removed' AS T, @@ROWCOUNT AS N;

-- Delete non-baseline chat messages
DELETE FROM CopilotChatMessages WHERE BaselineLabel IS NULL OR BaselineLabel <> @keepLabel;
SELECT 'Messages removed' AS T, @@ROWCOUNT;

-- Delete non-baseline chat sessions
DELETE FROM CopilotChatSessions WHERE IsAssessment = 1 AND (BaselineLabel IS NULL OR BaselineLabel <> @keepLabel);
SELECT 'Sessions removed' AS T, @@ROWCOUNT;

-- Delete non-baseline run summaries
DELETE FROM CopilotAssessmentRunSummaries WHERE BaselineLabel IS NULL OR BaselineLabel <> @keepLabel;
SELECT 'RunSummaries removed' AS T, @@ROWCOUNT;

-- Surviving baseline counts
SELECT 'BASELINE PRESERVED — Traces' AS T, COUNT(*) AS N FROM CopilotTraceHistories;
SELECT 'BASELINE PRESERVED — Sessions' AS T, COUNT(*) FROM CopilotChatSessions WHERE IsAssessment = 1;
SELECT 'BASELINE PRESERVED — Messages' AS T, COUNT(*) FROM CopilotChatMessages;
SELECT 'BASELINE PRESERVED — RunSummaries' AS T, COUNT(*) FROM CopilotAssessmentRunSummaries;
