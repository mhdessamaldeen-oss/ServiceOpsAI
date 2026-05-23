#!/usr/bin/env bash
# Helper: run a suite, poll for completion, dump summary.
# Usage: ./run-suite.sh suite-2-aggregation-2026-05-19
set -e
SUITE="$1"
if [ -z "$SUITE" ]; then echo "usage: $0 <suite-name>"; exit 2; fi
RESP=$(curl -sSk -X POST "https://localhost:8899/api/blackbox/run/$SUITE" 2>&1)
RID=$(echo "$RESP" | grep -oP '"runId":"[^"]+"' | grep -oP '[^"]+$')
echo "Started $SUITE with RunId=$RID"
until [[ $(sqlcmd -S "PC\\SQLEXPRESS" -d "AISupportAnalysisDB" -E -h-1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM CopilotAssessmentRunSummaries WHERE RunId = '$RID'" 2>/dev/null | tr -d ' \r\n') == "1" ]]; do
  sleep 25
done
echo "===DONE==="
sqlcmd -S "PC\\SQLEXPRESS" -d "AISupportAnalysisDB" -E -h-1 -W -Q "SET NOCOUNT ON; SELECT TotalCases, PassCount, FailCount, AvgLatencyMs, MaxLatencyMs FROM CopilotAssessmentRunSummaries WHERE RunId = '$RID'"
echo "===FAILED==="
sqlcmd -S "PC\\SQLEXPRESS" -d "AISupportAnalysisDB" -E -h-1 -W -Q "SET NOCOUNT ON; SELECT FailedCaseCodes FROM CopilotAssessmentRunSummaries WHERE RunId = '$RID'"
