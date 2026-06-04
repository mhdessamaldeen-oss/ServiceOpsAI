using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServiceOpsAI.Constants;

namespace ServiceOpsAI.Models.AI
{
    public class CopilotAssessmentCorrectAnswer
    {
        public string? SQL { get; set; }
        public List<string>? ExpectedFilters { get; set; }
        public List<string>? ExpectedAnswerKeywords { get; set; }
        public List<string>? ExpectedClarificationKeywords { get; set; }
    }

    /// <summary>
    /// Represents one curated assessment scenario for the admin copilot.
    /// The same scenario catalog can drive the lab page and the sample library.
    /// </summary>
    public class CopilotAssessmentCase
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;

        public void GenerateStableId()
        {
            if (!string.IsNullOrEmpty(Id) && Id.Length > 10 && !Id.Contains("-")) return; // Already has a stable ID

            using var md5 = MD5.Create();
            var q = (Question ?? string.Empty).Trim();
            var c = (Category ?? "General").Trim();
            var tk = (ExpectedToolKey ?? string.Empty).Trim();
            var m = ExpectedMode?.ToString() ?? string.Empty;
            var i = ExpectedIntent?.ToString() ?? string.Empty;

            var input = $"{q}_{c}_{tk}_{m}_{i}";
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            Id = BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        public string Question { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public string CategoryDescription { get; set; } = string.Empty;
        public string LibraryGroup { get; set; } = "General";
        public bool IncludeInCopilotLibrary { get; set; } = true;
        public bool IncludeInAssessmentSuite { get; set; } = true;
        public List<string> LibrarySurfaces { get; set; } = [CopilotSurfaces.Default];
        public int SortOrder { get; set; }
        public string Difficulty { get; set; } = "Medium"; // Easy, Medium, Hard, Complicated
        public List<CopilotChatMessage> SeedHistory { get; set; } = new();

        /// <summary>Stamped by <c>CopilotAssessmentHandler</c> when loading a scenario from a
        /// suite file — the suite filename (e.g. "10-aggregation-count-sum-avg-2026-05-08.json").
        /// Threaded through to <c>CopilotChatRequest.SourceSuite</c> and persisted to
        /// <c>CopilotTraceHistory.SourceSuite</c> so triage can answer "which file produced this?".</summary>
        public string SourceSuite { get; set; } = string.Empty;

        /// <summary>Monotonic counter stamped by <c>CopilotAssessmentHandler.LoadDataAsync</c>
        /// in multi-suite mode so the run loop processes each suite file to completion before
        /// moving to the next, instead of interleaving questions alphabetically by Question.
        /// Single-suite mode leaves it at 0 — existing <c>SortOrder</c> / <c>Question</c>
        /// tiebreakers then control order, preserving the historical behavior.</summary>
        public int LoadOrder { get; set; }

        public CopilotChatMode? ExpectedMode { get; set; }
        public CopilotIntentKind? ExpectedIntent { get; set; }
        public string? ExpectedToolKey { get; set; }
        public bool? RequiresRecordContext { get; set; }

        // --- Golden Test Parity Extensions ---
        public string? ExpectedPrimaryEntity { get; set; }
        public string? ExpectedOperation { get; set; }
        public int? ExpectedLimit { get; set; }
        public int? ExpectedDecomposition { get; set; }
        public List<string>? ExpectedFields { get; set; }
        public List<string>? ExpectedGroupBy { get; set; }
        public List<dynamic>? ExpectedFilters { get; set; }
        public List<dynamic>? ExpectedSorts { get; set; }
        public List<dynamic>? ExpectedAggregations { get; set; }
        public List<dynamic>? ExpectedHavingFilters { get; set; }

        // --- Real Answer Verification ---
        public List<string>? ExpectedAnswerKeywords { get; set; }
        public List<string>? ForbiddenAnswerKeywords { get; set; }
        public int? ExpectedResultsCount { get; set; }
        public bool? ExpectedAnyResults { get; set; }
        public bool? ExpectedClarification { get; set; }
        public bool? ExpectedInvalid { get; set; }
        public List<string>? ExpectedClarificationKeywords { get; set; }

        // --- Consolidated JSON support ---
        public CopilotAssessmentCorrectAnswer? CorrectAnswer { get; set; }

        // --- GoldenSetRunner-style structural validation ---
        // The legacy PassedFields / PassedFilters / PassedGroupBy checks need an
        // AdminCopilotDynamicTicketQueryPlan from the response, which the AnalystAgent
        // bridge can't produce (the new pipeline doesn't emit that plan shape). These five
        // fields are the substitute: assertions on the *generated SQL text*, row count,
        // result column shape, terminal failed-stage, and latency budget. Together they let
        // the assessment lab grade the new copilot the same way the eval harness does.
        //
        // All five are optional — case authors enable only the checks that matter for the
        // scenario. The CopilotAssessmentResult Passed* getters skip the check when the
        // corresponding expectation is null/empty.

        /// <summary>SQL tokens that MUST appear in the generated SQL (substring, case-insensitive).
        /// e.g. ["WHERE", "JOIN"] for a join scenario. Skipped when null/empty.</summary>
        public List<string>? ExpectedSqlContains { get; set; }

        /// <summary>SQL tokens at least ONE of which must appear. Use when there are multiple
        /// valid SQL realisations (e.g. ["IN", "OR"] for a multi-value filter). Skipped when null/empty.</summary>
        public List<string>? ExpectedSqlAnyOf { get; set; }

        /// <summary>SQL tokens the generated SQL must NOT contain (substring, case-insensitive).
        /// Use for anti-patterns like ["DELETE", "DROP", "UPDATE"]. Skipped when null/empty.</summary>
        public List<string>? ExpectedSqlNotContains { get; set; }

        /// <summary>Minimum expected row count after execution. Use 1 to assert "any results".</summary>
        public int? ExpectedMinRows { get; set; }

        /// <summary>Maximum expected row count after execution. Use 1 for a scalar / count answer.</summary>
        public int? ExpectedMaxRows { get; set; }

        /// <summary>Column names that must appear in the result set (case-insensitive). Skipped when null/empty.</summary>
        public List<string>? ExpectedColumns { get; set; }

        /// <summary>The single returned cell VALUE for a scalar answer (e.g. COUNT(*)=13, SUM=…). Graded
        /// by PassedScalar against the first cell of the first result row. Distinct from
        /// <see cref="ExpectedRowCount"/> (which is a result-set size). Skipped when null.</summary>
        public decimal? ExpectedScalar { get; set; }

        /// <summary>The result-set ROW COUNT a list/projection answer must return (e.g. "list customers"
        /// = 200). Graded against the actual row count. Replaces the broken ExpectedResultsCount path
        /// (which read a constant-0 source). Skipped when null.</summary>
        public int? ExpectedRowCount { get; set; }

        /// <summary>Provenance flag for a frozen GOLD benchmark case: true once the ExpectedSql has been
        /// verified-by-execution + a human row-sanity check. The gold loader WARNS on any gold-* case
        /// left false, so an un-vetted gold answer can't silently become the oracle's ground truth.</summary>
        public bool GoldVerified { get; set; }

        /// <summary>Reference SQL the case was curated against. The same string flows end-to-end:
        /// here on the case, through <c>CopilotChatRequest.ExpectedSql</c> on the wire, and finally
        /// stored in <c>CopilotTraceHistory.ExpectedScript</c> for side-by-side comparison with
        /// the generated SQL. When present, the rigorous Execution-Accuracy check runs both this
        /// SQL and the copilot's, comparing result sets as multisets.</summary>
        public string? ExpectedSql { get; set; }

        /// <summary>When set + ExpectedInvalid=true, asserts the failure happened at THIS stage
        /// (e.g. "Validator" / "Preflight"). Prevents writing-off all refusals as "we caught it"
        /// when the validator should have caught it but the planner did instead.</summary>
        public string? ExpectedFailedStage { get; set; }

        // ── Legacy-suite compatibility shim ──────────────────────────────────────────────────────
        // Older gold suites (suite-baseline / suite-matrix) used "GoldSQL" + "Shape" keys the grader
        // never read, so those cases graded as failures. These aliases map them onto the canonical
        // ExpectedSql / Category so they grade through the EX oracle WITHOUT rewriting every file.
        // (Frozen gold-*.json files use the canonical keys directly.)
        [System.Text.Json.Serialization.JsonPropertyName("GoldSQL")]
        public string? GoldSqlAlias
        {
            get => null;   // write-only mapping; don't re-serialize a duplicate of ExpectedSql
            set { if (!string.IsNullOrWhiteSpace(value)) ExpectedSql = value; }
        }

        [System.Text.Json.Serialization.JsonPropertyName("Shape")]
        public string? ShapeAlias
        {
            get => null;
            set { if (!string.IsNullOrWhiteSpace(value)) Category = value; }
        }

        /// <summary>Per-case latency budget in ms. Overrides the global 30s default in
        /// CopilotAssessmentResult.PassedLatency. Use lower values (e.g. 5000) for the
        /// deterministic-tier scenarios that shouldn't reach the LLM at all.</summary>
        public long? MaxLatencyMs { get; set; }

        public string ExpectedBehaviorSummary
        {
            get
            {
                var parts = new List<string>();

                if (ExpectedIntent.HasValue)
                {
                    parts.Add($"Intent: {ExpectedIntent.Value}");
                }

                if (ExpectedMode.HasValue)
                {
                    parts.Add($"Mode: {ExpectedMode.Value}");
                }

                if (!string.IsNullOrWhiteSpace(ExpectedToolKey))
                {
                    parts.Add($"Tool: {ExpectedToolKey}");
                }

                if (!string.IsNullOrWhiteSpace(ExpectedPrimaryEntity))
                {
                    parts.Add($"Department: {ExpectedPrimaryEntity}");
                }

                if (!string.IsNullOrWhiteSpace(ExpectedOperation))
                {
                    parts.Add($"Op: {ExpectedOperation}");
                }

                if (RequiresRecordContext.HasValue)
                {
                    parts.Add(RequiresRecordContext.Value ? "Context: required" : "Context: optional");
                }

                if (ExpectedClarification == true)
                {
                    parts.Add("Clarification expected");
                }

                if (ExpectedInvalid == true)
                {
                    parts.Add("Invalid/unsupported expected");
                }

                return parts.Count > 0 ? string.Join(" | ", parts) : "General behavior";
            }
        }

        public bool SupportsSurface(string? surface)
        {
            var requestedSurface = string.IsNullOrWhiteSpace(surface) ? CopilotSurfaces.Default : surface.Trim();

            if (LibrarySurfaces == null || LibrarySurfaces.Count == 0)
            {
                return string.Equals(requestedSurface, CopilotSurfaces.Default, StringComparison.OrdinalIgnoreCase);
            }

            return LibrarySurfaces.Any(value =>
                string.Equals(value, requestedSurface, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "all", StringComparison.OrdinalIgnoreCase));
        }
    }

    public class CopilotAssessmentCaseGroup
    {
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<CopilotAssessmentCase> Cases { get; set; } = new();
    }

    public class CopilotAssessmentLabViewModel
    {
        public List<CopilotAssessmentCaseGroup> CaseGroups { get; set; } = new();
        public List<CopilotPromptGroup> CopilotSampleGroups { get; set; } = new();
        public CopilotAssessmentRunSummaryDto? LatestRun { get; set; }
        public ServiceOpsAI.Models.Common.PagedResult<CopilotAssessmentCaseGridItem> CatalogPage { get; set; } = new();
        public int TotalCases => CaseGroups.Sum(group => group.Cases.Count);

        /// <summary>
        /// Set of assessment case codes that have at least one CopilotTraceHistory entry.
        /// Used to determine the "Has Answer" column value.
        /// </summary>
        public HashSet<string> TraceCaseCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<CopilotChatSession> AvailableSessions { get; set; } = new();
        public int? SelectedSessionId { get; set; }

        /// <summary>Suites discovered under <c>Services/AI/Copilot/Assessment/Suites/</c>. The
        /// view renders these as a dropdown so the user can flip between assessment versions
        /// without editing settings. Most-recently-modified entry wins as the default.</summary>
        public List<AssessmentSuiteOption> AvailableSuites { get; set; } = new();

        /// <summary>The suite filename currently active. Empty/null = no Suites/ folder, falling
        /// back to the legacy single-file catalog.</summary>
        public string? ActiveSuiteFileName { get; set; }
    }

    public class AssessmentSuiteOption
    {
        public string FileName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int ScenarioCount { get; set; }
        public bool IsDefault { get; set; }
    }

    public class CopilotAssessmentCaseGridItem
    {
        public string Id { get; set; } = string.Empty;
        public string? Code { get; set; }
        public string Question { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string CategoryDescription { get; set; } = string.Empty;
        public string Difficulty { get; set; } = string.Empty;

        /// <summary>Suite file (without extension) the case was loaded from. Empty when running from the legacy single-file catalog.</summary>
        public string SourceSuite { get; set; } = string.Empty;
        public string ExpectedBehavior { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool HasAnswer { get; set; }
        public bool HasLatestResult { get; set; }
        public bool? IsSuccess { get; set; }
        public string ActualMode { get; set; } = string.Empty;
        public string ActualIntent { get; set; } = string.Empty;
        public string ActualTool { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string AnswerPreview { get; set; } = string.Empty;
        public long? LatencyMs { get; set; }
        public int? TraceId { get; set; }
        public int? TraceSessionId { get; set; }
        public string? ModelName { get; set; }
        public int? ContextCapacity { get; set; }
        public int? PromptChars { get; set; }
        public bool IsTruncated { get; set; }
        public bool? PreviousResult { get; set; }
        public DateTime? PreviousRunAt { get; set; }
        public string StatusChange { get; set; } = string.Empty;
        public int TotalRuns { get; set; }
        public double HistoricalSuccessRate { get; set; }
        public DateTime? LatestRunAt { get; set; }

        // Per-question LLM cost / usage from the latest trace row. Populated by whichever
        // query builds the grid items (joins CopilotTraceHistories). Surfaced in the
        // assessment table so a re-run sweep across the suite shows cost per scenario at
        // a glance — pinpoints expensive questions without opening each investigation page.
        public int? LlmCallCount { get; set; }
        public int? TotalPromptTokens { get; set; }
        public int? TotalCompletionTokens { get; set; }
        public decimal? EstimatedCostUsd { get; set; }

        /// <summary>Golden expected SQL from the assessment case (CorrectAnswer.SQL), if defined.</summary>
        public string? ExpectedSql { get; set; }

        /// <summary>Last SQL generated by the planner for this case, sourced from CopilotTraceHistories.GeneratedScript.</summary>
        public string? GeneratedSql { get; set; }
    }

    /// <summary>
    /// Result of a single assessment execution.
    /// </summary>
    public class CopilotAssessmentResult
    {
        public CopilotAssessmentCase Case { get; set; } = new();
        public CopilotChatResponse? ActualResponse { get; set; }
        public string FailureReason { get; set; } = string.Empty;

        public int? TraceId { get; set; }
        public long LatencyMs { get; set; }
        public bool HasAnswer => ActualResponse != null && PassedAnswerQuality;
        public bool IsException => !string.IsNullOrWhiteSpace(FailureReason);

        /// <summary>
        /// Industry-standard <b>Execution Accuracy (EX)</b> verdict — populated by
        /// <c>IExecutionAccuracyChecker</c> when the case has a curated ExpectedSql. The check
        /// runs both expected SQL + copilot SQL against the live DB and compares result sets
        /// as multisets.
        /// </summary>
        /// <remarks>
        /// <c>true</c>  = rows matched.
        /// <c>false</c> = rows differed.
        /// <c>null</c>  = check didn't run (no gold SQL, copilot errored, or gold SQL threw).
        /// </remarks>
        public bool? ExAccuracy { get; set; }

        /// <summary>Row count returned by the expected SQL — surfaced in the grid so the user
        /// sees "expected returned 5 rows, copilot returned 7 rows" alongside the verdict.</summary>
        public int? ExExpectedRowCount { get; set; }

        /// <summary>If <see cref="ExAccuracy"/> is null, this carries the reason (empty/gold-failed/etc.).</summary>
        public string? ExError { get; set; }

        /// <summary>SECONDARY semantic verdict from <c>IExpectationVerifier</c> (Pass/Partial/Fail/N/A
        /// over entity/op/filters/fields/aggregations/group-by). Diagnostic ONLY — it does NOT gate
        /// <see cref="IsSuccess"/> (execution accuracy is primary). Surfaced in <see cref="Detail"/>
        /// and used by the auto-promotion gate (don't promote a structurally-broken query).</summary>
        public AnalystAgent.Eval.ExpectationVerifier.VerificationResult? VerifierResult { get; set; }
        public string ActualIntent => ActualResponse?.ExecutionDetails?.DetectedIntent ?? string.Empty;
        public string ActualMode => ResolveActualMode();
        public string ActualTool => ActualResponse?.UsedTool ?? string.Empty;
        public bool ActualRequiresClarification => DetectClarification();
        public bool ActualInvalid => DetectInvalid();
        public bool HasRecordContext => DetectRecordContext();

        // ── Case classification + gold definitions (schema-agnostic: reads only typed enums + the
        //    case's own Expected* fields — no table/column/suite literal) ──────────────────────────

        /// <summary>This case asserts a refusal or a clarification, not a data answer.</summary>
        public bool IsRefusalOrClarifyCase =>
            Case.ExpectedInvalid == true || Case.ExpectedClarification == true;

        /// <summary>The case has gold SQL we can EXECUTE and compare result sets against.</summary>
        public bool HasExecutableGold => !string.IsNullOrWhiteSpace(Case.ExpectedSql);

        /// <summary>The case declares an objective RESULT expectation (columns / row bounds / row
        /// count / scalar) even without gold SQL. Excludes ExpectedResultsCount/ExpectedAnyResults —
        /// those grade against a constant-0 source and are not trustworthy gold.</summary>
        public bool HasResultGold =>
            (Case.ExpectedColumns is { Count: > 0 }) ||
            (Case.ExpectedSqlContains is { Count: > 0 }) ||
            (Case.ExpectedSqlAnyOf is { Count: > 0 }) ||
            Case.ExpectedMinRows.HasValue || Case.ExpectedMaxRows.HasValue ||
            Case.ExpectedRowCount.HasValue || Case.ExpectedScalar.HasValue;

        /// <summary>The case carries ANY gold (executable or result-shaped).</summary>
        public bool HasGold => HasExecutableGold || HasResultGold;

        /// <summary>True for a question that must be answered by data (so a no-gold case is NOT
        /// correct-by-default and an abstain is a real failure, not an "honest refusal").</summary>
        public bool IsDataQueryCase =>
            !IsRefusalOrClarifyCase && (
                Case.ExpectedIntent == CopilotIntentKind.DataQuery ||
                Case.ExpectedMode is CopilotChatMode.DynamicTicketQuery or CopilotChatMode.AppDataAnalysis ||
                HasExecutableGold || HasResultGold);

        /// <summary>A data-query case must actually PRODUCE an answer. A 0-row result is correct ONLY when
        /// the copilot ran SQL AND the gold explicitly expects an empty/zero result. An abstain (no SQL),
        /// or an unexplained empty result, is NOT correct — closes the "abstain passes via ExpectedMaxRows-
        /// only or 0-row gold" holes (an empty result trivially satisfies a max bound / a 0==0 EX match).</summary>
        public bool ProducedAnswerOrExpectedEmpty
        {
            get
            {
                if (!IsDataQueryCase) return true;
                var rows = ActualResponse?.StructuredRows?.Count ?? 0;
                if (rows > 0) return true;
                var ranSql = !string.IsNullOrWhiteSpace(ActualResponse?.Notes);
                var goldExpectsEmpty =
                    Case.ExpectedRowCount == 0 ||
                    (Case.ExpectedMinRows == 0 && !Case.ExpectedScalar.HasValue) ||      // author explicitly allows empty
                    (HasExecutableGold && ExAccuracy == true && (ExExpectedRowCount ?? -1) == 0);
                return ranSql && goldExpectsEmpty;
            }
        }

        /// <summary>Which field caused the data-query classification — surfaced in <see cref="Detail"/>.</summary>
        public string DataQueryTrigger =>
            HasExecutableGold ? "ExpectedSql"
            : HasResultGold ? "result-gold (columns/rows/scalar)"
            : Case.ExpectedIntent == CopilotIntentKind.DataQuery ? "ExpectedIntent=DataQuery"
            : Case.ExpectedMode is CopilotChatMode.DynamicTicketQuery or CopilotChatMode.AppDataAnalysis ? "ExpectedMode"
            : "n/a";

        public bool PassedPrimaryEntity =>
            string.IsNullOrWhiteSpace(Case.ExpectedPrimaryEntity) ||
            GetEffectiveQueryPlans().Any(p => 
                string.Equals(p.TargetView, Case.ExpectedPrimaryEntity, StringComparison.OrdinalIgnoreCase) ||
                (Case.ExpectedPrimaryEntity == "Ticket" && p.TargetView == "TicketRecords") ||
                (Case.ExpectedPrimaryEntity == "Department" && p.TargetView == "EntitySummary"));

        public bool PassedOperation =>
            string.IsNullOrWhiteSpace(Case.ExpectedOperation) ||
            GetEffectiveQueryPlans().Any(p => string.Equals(p.Intent.ToString(), Case.ExpectedOperation, StringComparison.OrdinalIgnoreCase));

        public bool PassedFields =>
            Case.ExpectedFields == null || !Case.ExpectedFields.Any() ||
            GetEffectiveQueryPlans().Any(p => Case.ExpectedFields.All(f => p.SelectedColumns.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase))));

        public bool PassedGroupBy =>
            Case.ExpectedGroupBy == null || !Case.ExpectedGroupBy.Any() ||
            GetEffectiveQueryPlans().Any(p => Case.ExpectedGroupBy.All(g => string.Equals(p.GroupByField, g, StringComparison.OrdinalIgnoreCase)));

        public bool PassedFilters =>
            Case.ExpectedFilters == null || !Case.ExpectedFilters.Any() ||
            CheckFiltersMatch();

        public bool PassedAggregations =>
            Case.ExpectedAggregations == null || !Case.ExpectedAggregations.Any() ||
            GetEffectiveQueryPlans().Any(p => !string.IsNullOrEmpty(p.AggregationType));

        public bool PassedMode =>
            !Case.ExpectedMode.HasValue ||
            string.Equals(ActualMode, Case.ExpectedMode.Value.ToString(), StringComparison.OrdinalIgnoreCase);

        public bool PassedIntent =>
            !Case.ExpectedIntent.HasValue ||
            string.Equals(ActualIntent, Case.ExpectedIntent.Value.ToString(), StringComparison.OrdinalIgnoreCase);

        public bool PassedTool =>
            string.IsNullOrWhiteSpace(Case.ExpectedToolKey) ||
            string.Equals(ActualTool, Case.ExpectedToolKey, StringComparison.OrdinalIgnoreCase);

        public bool PassedContext =>
            !Case.RequiresRecordContext.HasValue ||
            Case.RequiresRecordContext.Value == HasRecordContext;

        public bool PassedResultsCount =>
            (!Case.ExpectedResultsCount.HasValue || GetTotalResultsCount() == Case.ExpectedResultsCount.Value) &&
            (!Case.ExpectedAnyResults.HasValue || (GetTotalResultsCount() > 0) == Case.ExpectedAnyResults.Value);

        public bool PassedAnswerQuality => CheckAnswerQuality();

        public bool PassedClarification =>
            !Case.ExpectedClarification.HasValue ||
            Case.ExpectedClarification.Value == ActualRequiresClarification;

        public bool PassedInvalid =>
            !Case.ExpectedInvalid.HasValue ||
            Case.ExpectedInvalid.Value == ActualInvalid;

        public bool PassedDecomposition =>
            !Case.ExpectedDecomposition.HasValue ||
            CountObservedWorkflows() >= Case.ExpectedDecomposition.Value;

        /// <summary>Per-deployment fallback applied when a case doesn't set <c>MaxLatencyMs</c>
        /// explicitly. Set at run-start by <c>CopilotAssessmentHandler</c> from the
        /// <c>CopilotAssessmentDefaultLatencyMs</c> SystemSettings row (defaults to 30000 = 30s
        /// for backward compatibility). Operators tune this when running against slow local
        /// models — a 7B model on modest hardware needs 90-120s for VQ-Explainer cases.</summary>
        // 180s default — was 30s but real cases (tool-dispatch + explainer, LLM cold-cache,
        // multi-turn refinement chains) routinely exceed 30s through no fault of the planner.
        // Operators can lower this via SystemSettings.CopilotAssessmentDefaultLatencyMs for
        // hot-cache eval runs. 2026-05-20.
        public static long DefaultMaxLatencyMs { get; set; } = 180000;

        public bool PassedLatency => LatencyMs <= (Case.MaxLatencyMs ?? DefaultMaxLatencyMs);

        // ── GoldenSetRunner-style SQL / row / column / stage checks ──────────────────────
        // These mirror the validation logic in Areas/AnalystAgent/Eval/GoldenSetRunner.cs.
        // Each returns true when the expectation is absent (case doesn't care), so legacy
        // CopilotAssessmentCase rows that don't set these fields aren't affected.
        // SQL is read from ActualResponse.Notes — that's where the bridge writes the
        // generated SQL after a successful planner run.

        private string GeneratedSql => ActualResponse?.Notes ?? string.Empty;

        public bool PassedSqlContains
        {
            get
            {
                if (Case.ExpectedSqlContains is not { Count: > 0 } needs) return true;
                if (string.IsNullOrEmpty(GeneratedSql)) return false;
                return needs.All(t => GeneratedSql.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        public bool PassedSqlAnyOf
        {
            get
            {
                if (Case.ExpectedSqlAnyOf is not { Count: > 0 } anyOf) return true;
                if (string.IsNullOrEmpty(GeneratedSql)) return false;
                return anyOf.Any(t => GeneratedSql.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        public bool PassedSqlNotContains
        {
            get
            {
                if (Case.ExpectedSqlNotContains is not { Count: > 0 } banned) return true;
                if (string.IsNullOrEmpty(GeneratedSql)) return true; // no SQL = no banned token
                return !banned.Any(t => GeneratedSql.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        public bool PassedRowBounds
        {
            get
            {
                var rowCount = ActualResponse?.StructuredRows?.Count ?? 0;
                if (Case.ExpectedMinRows.HasValue && rowCount < Case.ExpectedMinRows.Value) return false;
                if (Case.ExpectedMaxRows.HasValue && rowCount > Case.ExpectedMaxRows.Value) return false;
                return true;
            }
        }

        public bool PassedColumns
        {
            get
            {
                if (Case.ExpectedColumns is not { Count: > 0 } cols) return true;
                var columns = ActualResponse?.StructuredColumns ?? new List<string>();
                if (columns.Count == 0) return false;
                var actual = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
                return cols.All(c => actual.Contains(c));
            }
        }

        /// <summary>Execution accuracy — the strongest correctness check (runs gold SQL + the copilot's
        /// and compares result sets as multisets). <c>true</c>=rows matched, <c>false</c>=rows differed,
        /// <c>null</c>=the check could not run. A null PASSES only when there was NO gold SQL to run; if
        /// gold SQL was declared but the check returned null (gold SQL threw), that is a FAIL — a missing
        /// answer must not silently pass. <see cref="ExError"/> carries the null reason.</summary>
        public bool PassedExAccuracy => ExAccuracy switch
        {
            true => true,
            false => false,
            _ => !HasExecutableGold,
        };

        /// <summary>Scalar VALUE check — the first cell of the first result row equals
        /// <see cref="CopilotAssessmentCase.ExpectedScalar"/>. Skipped when the case declares no scalar.</summary>
        public bool PassedScalar
        {
            get
            {
                if (!Case.ExpectedScalar.HasValue) return true;
                var rows = ActualResponse?.StructuredRows;
                if (rows is not { Count: > 0 }) return false;
                // Row.Values is a Dictionary<string,string> (column→cell). Match the expected scalar
                // against ANY cell of the first row, so a leftover label/Id column before the aggregate
                // (e.g. SELECT 'x', COUNT(*)) doesn't grade the wrong cell.
                foreach (var cell in rows[0].Values.Values)
                    if (decimal.TryParse(cell, System.Globalization.NumberStyles.Any,
                                         System.Globalization.CultureInfo.InvariantCulture, out var got)
                        && got == Case.ExpectedScalar.Value)
                        return true;
                return false;
            }
        }

        /// <summary>Result-set ROW COUNT check (distinct from the scalar value). Skipped when the case
        /// declares no <see cref="CopilotAssessmentCase.ExpectedRowCount"/>.</summary>
        public bool PassedRowCount =>
            !Case.ExpectedRowCount.HasValue ||
            (ActualResponse?.StructuredRows?.Count ?? 0) == Case.ExpectedRowCount.Value;

        public bool PassedFailedStage
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Case.ExpectedFailedStage)) return true;
                var actualStage = ActualResponse?.ExecutionDetails?.RouteReason ?? string.Empty;
                return string.Equals(actualStage, Case.ExpectedFailedStage, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>A case is <i>verified</i> when its author populated at least one objective
        /// expectation — gold SQL, SQL-text constraints, expected columns, row bounds, or a
        /// structured plan assertion. Without any of these, every <c>Passed*</c> check defaults
        /// to true and the case passes purely because "nothing crashed", which is a near-
        /// meaningless signal. <see cref="IsSuccess"/> now requires verification, so unverified
        /// cases are surfaced as failures and prompt the author to add expectations.
        /// <para>The <c>ExpectedInvalid</c> / <c>ExpectedClarification</c> shapes are themselves
        /// objective expectations (the case asserts the system should refuse/clarify), so they
        /// also count as verification.</para></summary>
        public bool IsVerified =>
            !string.IsNullOrWhiteSpace(Case.ExpectedSql) ||
            (Case.ExpectedSqlContains is { Count: > 0 }) ||
            (Case.ExpectedSqlAnyOf is { Count: > 0 }) ||
            (Case.ExpectedSqlNotContains is { Count: > 0 }) ||
            (Case.ExpectedColumns is { Count: > 0 }) ||
            (Case.ExpectedFields is { Count: > 0 }) ||
            (Case.ExpectedGroupBy is { Count: > 0 }) ||
            (Case.ExpectedFilters is { Count: > 0 }) ||
            (Case.ExpectedAggregations is { Count: > 0 }) ||
            Case.ExpectedMinRows.HasValue ||
            Case.ExpectedMaxRows.HasValue ||
            Case.ExpectedRowCount.HasValue ||
            Case.ExpectedScalar.HasValue ||
            Case.ExpectedResultsCount.HasValue ||
            Case.ExpectedAnyResults.HasValue ||
            Case.ExpectedInvalid.HasValue ||
            Case.ExpectedClarification.HasValue ||
            !string.IsNullOrWhiteSpace(Case.ExpectedFailedStage) ||
            Case.ExpectedMode.HasValue ||
            Case.ExpectedIntent.HasValue ||
            !string.IsNullOrWhiteSpace(Case.ExpectedToolKey);

        // CORRECTNESS verdict — latency is NOT part of it (a correct answer 1ms over budget is still
        // correct; latency is reported separately via WithinLatencyBudget). The six plan getters
        // (PassedPrimaryEntity/Operation/Fields/GroupBy/Filters/Aggregations) and PassedResultsCount read
        // sources the v2 bridge never populates (always-true / constant-0) — they are dropped from the
        // verdict but kept as public getters for the diagnostic grid. A DataQuery case with NO gold is
        // NOT correct-by-default: it cannot be graded, so it fails (forces the author to add gold).
        public bool PassedCorrectness =>
            !IsException &&
            IsVerified &&
            (!IsDataQueryCase || HasGold) &&
            ProducedAnswerOrExpectedEmpty &&
            PassedMode &&
            PassedIntent &&
            PassedTool &&
            PassedContext &&
            PassedAnswerQuality &&
            PassedClarification &&
            PassedInvalid &&
            PassedDecomposition &&
            PassedExAccuracy &&
            PassedScalar &&
            PassedRowCount &&
            PassedSqlContains &&
            PassedSqlAnyOf &&
            PassedSqlNotContains &&
            PassedRowBounds &&
            PassedColumns &&
            PassedFailedStage;

        /// <summary>The single success verdict = correctness only (latency excluded).</summary>
        public bool IsSuccess => PassedCorrectness;

        /// <summary>Reported performance axis — did the answer land within the latency budget? NOT a
        /// term in <see cref="IsSuccess"/>; surfaced separately so a correct-but-slow case isn't a "fail".</summary>
        public bool WithinLatencyBudget => PassedLatency;

        public string Detail
        {
            get
            {
                if (IsException)
                {
                    return FailureReason;
                }

                var issues = new List<string>();

                if (!IsVerified)
                {
                    issues.Add("unverified — case has no objective expectations (add ExpectedSql / ExpectedSqlContains / ExpectedColumns / ExpectedMinRows / ExpectedInvalid)");
                }

                if (IsDataQueryCase && !HasGold)
                {
                    issues.Add($"data-query case has no gold (add ExpectedSql / ExpectedColumns / ExpectedMinRows / ExpectedRowCount / ExpectedScalar) — cannot grade correctness; classified as data by {DataQueryTrigger}");
                }

                if (IsDataQueryCase && HasGold && !ProducedAnswerOrExpectedEmpty)
                {
                    issues.Add("data-query produced NO answer (0 rows / abstain) and the gold does not expect an empty result — failure, not an honest empty");
                }

                if (!PassedMode && Case.ExpectedMode.HasValue)
                {
                    issues.Add($"mode expected {Case.ExpectedMode.Value}, got {ActualMode}");
                }

                if (!PassedIntent && Case.ExpectedIntent.HasValue)
                {
                    issues.Add($"intent expected {Case.ExpectedIntent.Value}, got {ActualIntent}");
                }

                if (!PassedTool && !string.IsNullOrWhiteSpace(Case.ExpectedToolKey))
                {
                    issues.Add($"tool expected {Case.ExpectedToolKey}, got {ActualTool}");
                }

                if (!PassedContext && Case.RequiresRecordContext.HasValue)
                {
                    issues.Add(Case.RequiresRecordContext.Value ? "record context missing" : "unexpected record context");
                }

                if (!PassedAnswerQuality)
                {
                    issues.Add("answer did not contain a usable result or failed keyword check");
                }

                if (!PassedExAccuracy)
                {
                    if (ExAccuracy is null)   // null + gold present => the gold SQL itself could not run/compare
                        issues.Add($"execution accuracy FAIL — gold SQL could not run/compare: {ExError ?? "unknown"}");
                    else
                    {
                        var expectedCount = ExExpectedRowCount?.ToString() ?? "?";
                        var copilotCount = ActualResponse?.StructuredRows?.Count.ToString() ?? "?";
                        issues.Add($"execution accuracy FAIL — expected SQL returned {expectedCount} row(s), copilot SQL returned {copilotCount} row(s); result sets differ");
                    }
                }

                if (!PassedScalar && Case.ExpectedScalar.HasValue)
                {
                    var got = ActualResponse?.StructuredRows is { Count: > 0 } r ? (r[0].Values.Values.FirstOrDefault() ?? "(null)") : "(no rows)";
                    issues.Add($"scalar value FAIL — expected {Case.ExpectedScalar.Value}, got {got}");
                }
                if (!PassedRowCount && Case.ExpectedRowCount.HasValue)
                {
                    issues.Add($"row-count FAIL — expected exactly {Case.ExpectedRowCount.Value} row(s), got {ActualResponse?.StructuredRows?.Count ?? 0}");
                }

                // Secondary semantic verifier (diagnostic only — not part of the pass/fail verdict).
                if (VerifierResult is { Verdict: AnalystAgent.Eval.ExpectationVerifier.VerificationVerdict.Fail
                                              or AnalystAgent.Eval.ExpectationVerifier.VerificationVerdict.Partial })
                {
                    issues.Add($"semantic verifier (secondary, advisory): {VerifierResult.Summary}" +
                               (VerifierResult.FailedChecks.Count > 0 ? $" — {string.Join("; ", VerifierResult.FailedChecks)}" : ""));
                }

                if (!PassedClarification && Case.ExpectedClarification.HasValue)
                {
                    issues.Add(Case.ExpectedClarification.Value ? "expected clarification request" : "unexpected clarification request");
                }

                if (!PassedInvalid && Case.ExpectedInvalid.HasValue)
                {
                    issues.Add(Case.ExpectedInvalid.Value ? "expected invalid/unsupported handling" : "unexpected invalid/unsupported handling");
                }

                if (!PassedResultsCount)
                {
                    if (Case.ExpectedResultsCount.HasValue)
                        issues.Add($"expected {Case.ExpectedResultsCount.Value} result(s), got {GetTotalResultsCount()}");
                    else if (Case.ExpectedAnyResults == true)
                        issues.Add("expected at least one result, got none");
                }

                if (!PassedPrimaryEntity && !string.IsNullOrWhiteSpace(Case.ExpectedPrimaryEntity))
                {
                    issues.Add($"entity expected {Case.ExpectedPrimaryEntity}");
                }

                if (!PassedOperation && !string.IsNullOrWhiteSpace(Case.ExpectedOperation))
                {
                    issues.Add($"op expected {Case.ExpectedOperation}");
                }

                if (!PassedFields && Case.ExpectedFields?.Any() == true)
                {
                    issues.Add($"fields missing: {string.Join(", ", Case.ExpectedFields.Where(f => !GetEffectiveQueryPlans().Any(p => p.SelectedColumns.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))))}");
                }

                if (!PassedFilters && Case.ExpectedFilters?.Any() == true)
                {
                    issues.Add("filters did not match expected criteria");
                }

                if (!PassedDecomposition && Case.ExpectedDecomposition.HasValue)
                {
                    issues.Add($"expected at least {Case.ExpectedDecomposition.Value} workflow(s), got {CountObservedWorkflows()}");
                }

                if (!PassedLatency)
                {
                    var budget = Case.MaxLatencyMs ?? DefaultMaxLatencyMs;
                    issues.Add($"latency over budget {budget}ms, got {LatencyMs}ms (advisory — latency is NOT part of the pass/fail verdict)");
                }

                // GoldenSetRunner-style structural checks. Each emits a specific reason so the
                // assessment row's Detail column says *what* was wrong, not just "failed".
                if (!PassedSqlContains && Case.ExpectedSqlContains is { Count: > 0 } needs)
                {
                    var missing = needs.Where(t => GeneratedSql.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0).ToList();
                    issues.Add(missing.Count == needs.Count
                        ? $"sql missing all required tokens: [{string.Join(", ", needs)}]"
                        : $"sql missing token(s): [{string.Join(", ", missing)}]");
                }
                if (!PassedSqlAnyOf && Case.ExpectedSqlAnyOf is { Count: > 0 } anyOf)
                {
                    issues.Add($"sql contains none of the expected alternatives: [{string.Join(", ", anyOf)}]");
                }
                if (!PassedSqlNotContains && Case.ExpectedSqlNotContains is { Count: > 0 } banned)
                {
                    var hit = banned.Where(t => GeneratedSql.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    issues.Add($"sql contains banned token(s): [{string.Join(", ", hit)}]");
                }
                if (!PassedRowBounds)
                {
                    var rc = ActualResponse?.StructuredRows?.Count ?? 0;
                    if (Case.ExpectedMinRows.HasValue && rc < Case.ExpectedMinRows.Value)
                        issues.Add($"row count {rc} below expected min {Case.ExpectedMinRows.Value}");
                    if (Case.ExpectedMaxRows.HasValue && rc > Case.ExpectedMaxRows.Value)
                        issues.Add($"row count {rc} above expected max {Case.ExpectedMaxRows.Value}");
                }
                if (!PassedColumns && Case.ExpectedColumns is { Count: > 0 } expectedCols)
                {
                    var actual = new HashSet<string>(ActualResponse?.StructuredColumns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    var missingCols = expectedCols.Where(c => !actual.Contains(c)).ToList();
                    issues.Add($"missing expected column(s): [{string.Join(", ", missingCols)}]");
                }
                if (!PassedFailedStage && !string.IsNullOrWhiteSpace(Case.ExpectedFailedStage))
                {
                    var actualStage = ActualResponse?.ExecutionDetails?.RouteReason ?? "ok";
                    issues.Add($"expected failed-stage '{Case.ExpectedFailedStage}' but got '{actualStage}'");
                }

                return issues.Count > 0
                    ? string.Join(" | ", issues)
                    : "Matched expected behavior.";
            }
        }

        private List<AdminCopilotDynamicTicketQueryPlan> GetEffectiveQueryPlans()
        {
            var plans = new List<AdminCopilotDynamicTicketQueryPlan>();
            if (ActualResponse == null) return plans;

            if (ActualResponse.DynamicQueryPlan != null)
                plans.Add(ActualResponse.DynamicQueryPlan);

            if (ActualResponse.ExecutionDetails?.QueryPlan != null)
                plans.Add(ActualResponse.ExecutionDetails.QueryPlan);

            if (ActualResponse.ExecutionDetails?.QueryPlans?.Any() == true)
                plans.AddRange(ActualResponse.ExecutionDetails.QueryPlans);

            if (ActualResponse.StructuredQueryResults?.Any() == true)
                plans.AddRange(ActualResponse.StructuredQueryResults.Select(r => r.Execution.Plan));

            return plans.Where(p => p != null).ToList()!;
        }

        private string ResolveActualMode()
        {
            if (ActualResponse == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(ActualTool) &&
                !string.Equals(ActualTool, "none", StringComparison.OrdinalIgnoreCase))
            {
                return CopilotChatMode.ExternalUtility.ToString();
            }

            if (string.Equals(ActualIntent, CopilotIntentKind.DataQuery.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return CopilotChatMode.DynamicTicketQuery.ToString();
            }

            if (string.Equals(ActualIntent, CopilotIntentKind.ExternalToolQuery.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return CopilotChatMode.ExternalUtility.ToString();
            }

            if (string.Equals(ActualIntent, CopilotIntentKind.GeneralChat.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ActualIntent, CopilotIntentKind.Unsupported.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ActualIntent, "Clarification", StringComparison.OrdinalIgnoreCase))
            {
                return CopilotChatMode.GeneralSupport.ToString();
            }

            return ActualResponse.ResponseMode.ToString();
        }

        private bool CheckFiltersMatch()
        {
            if (Case.ExpectedFilters == null || !Case.ExpectedFilters.Any()) return true;
            var plans = GetEffectiveQueryPlans();
            if (!plans.Any()) return false;

            // Simplified check: at least one plan must contain all expected filters
            return plans.Any(plan =>
            {
                foreach (var expectedRaw in Case.ExpectedFilters)
                {
                    // This expects a structure like { Field: "...", Operator: "...", Value: "..." }
                    var expected = JsonSerializer.Deserialize<CopilotDataFilterPlan>(JsonSerializer.Serialize(expectedRaw));
                    if (expected == null) continue;

                    bool match = false;
                    // STRICT METADATA CHECK: No hard-coded field names allowed here.
                    // The Planner must populate GlobalFilters for all extracted criteria.
                    if (plan.GlobalFilters.TryGetValue(expected.Field, out string? actualValue))
                    {
                        if (string.Equals(actualValue, expected.Value?.ToString(), StringComparison.OrdinalIgnoreCase))
                            match = true;
                    }

                    if (!match) return false;
                }
                return true;
            });
        }

        public string AnswerPreview
        {
            get
            {
                var answer = ActualResponse?.Answer ?? string.Empty;
                if (string.IsNullOrWhiteSpace(answer))
                {
                    return string.Empty;
                }

                answer = answer.ReplaceLineEndings(" ").Trim();
                return answer.Length <= 220 ? answer : $"{answer[..220]}...";
            }
        }

        private bool DetectRecordContext()
        {
            if (ActualResponse == null)
            {
                return false;
            }

            if (ActualResponse.ActionPlan?.TargetTicketId.HasValue == true)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(ActualResponse.DynamicQueryPlan?.TicketNumber))
            {
                return true;
            }

            if (ActualResponse.StructuredQueryResults.Any(result => !string.IsNullOrWhiteSpace(result.Execution.Plan.TicketNumber)))
            {
                return true;
            }

            if (ActualResponse.ExecutionDetails?.QueryPlans?.Any(plan => !string.IsNullOrWhiteSpace(plan.TicketNumber)) == true)
            {
                return true;
            }

            return false;
        }

        private bool CheckAnswerQuality()
        {
            var answer = ActualResponse?.Answer?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(answer))
            {
                return false;
            }

            if (Case.ExpectedClarification == true || Case.ExpectedInvalid == true)
            {
                if (Case.ExpectedClarificationKeywords?.Any() == true &&
                    !Case.ExpectedClarificationKeywords.All(k => answer.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                return DetectClarification() || DetectInvalid();
            }

            // Explicit grouping: a retrieval-failure phrase OR a (tool + failed) pair indicates a
            // non-answer. Parenthesized so '&&' (which binds tighter than '||') groups as intended.
            if (answer.Contains("couldn't retrieve", StringComparison.OrdinalIgnoreCase) ||
                (answer.Contains("external tool", StringComparison.OrdinalIgnoreCase) &&
                 answer.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (Case.ExpectedIntent == CopilotIntentKind.DataQuery &&
                Regex.IsMatch(answer, @"^Found\s+\d+\s+records\.\s*$", RegexOptions.IgnoreCase))
            {
                return false;
            }

            // Keyword Validation
            if (Case.ExpectedAnswerKeywords?.Any() == true)
            {
                if (!Case.ExpectedAnswerKeywords.All(k => answer.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            if (Case.ForbiddenAnswerKeywords?.Any() == true)
            {
                if (Case.ForbiddenAnswerKeywords.Any(k => answer.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            return true;
        }

        private bool DetectClarification()
        {
            var answer = ActualResponse?.Answer ?? string.Empty;
            var route = ActualResponse?.ExecutionDetails?.RouteReason ?? string.Empty;
            var intent = ActualResponse?.ExecutionDetails?.DetectedIntent ?? string.Empty;

            if (string.Equals(intent, "Clarification", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (route.Contains("clarification", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(answer, @"\b(please clarify|clarify|which|what specific|provide|ask .* instead|as written)\b", RegexOptions.IgnoreCase);
        }

        private bool DetectInvalid()
        {
            var answer = ActualResponse?.Answer ?? string.Empty;
            var route = ActualResponse?.ExecutionDetails?.RouteReason ?? string.Empty;
            var intent = ActualResponse?.ExecutionDetails?.DetectedIntent ?? string.Empty;

            if (string.Equals(intent, CopilotIntentKind.Unsupported.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // RouteReason for refusals is "preflight-refused" (set by HostTraceSink.ResolveFailedStage
            // and the orchestrator's refusal paths via Trace=StageNames.PreflightRefused). Match it
            // alongside the legacy "invalid"/"unsafe"/"unsupported" substrings.
            if (route.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                route.Contains("unsafe", StringComparison.OrdinalIgnoreCase) ||
                route.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                route.Contains("preflight-refused", StringComparison.OrdinalIgnoreCase) ||
                route.Contains("refused", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Refusal answer text patterns. The write-intent guard emits "This pipeline is read-only.
            // The question expresses a 'delete' intent (en) which is not supported." — so we match
            // "read-only", "is not supported", "expresses a" alongside the legacy phrases.
            return Regex.IsMatch(answer,
                @"\b(can't help|cannot help|can't answer|unsupported|unsafe|not able to help|as written|read-only|is not supported|expresses a)\b",
                RegexOptions.IgnoreCase);
        }

        // Result-set size for the v2 bridge: read StructuredRows (the bridge populates this), falling
        // back to the legacy StructuredQueryResults plan-shape when present. The old version read ONLY
        // StructuredQueryResults — which the v2 bridge never emits — so this returned a constant 0,
        // silently breaking ExpectedResultsCount / ExpectedAnyResults. (Prefer ExpectedRowCount.)
        private int GetTotalResultsCount()
        {
            if (ActualResponse == null) return 0;
            if (ActualResponse.StructuredQueryResults?.Any() == true)
                return ActualResponse.StructuredQueryResults.Sum(r => r.Execution.TotalCount);
            return ActualResponse.StructuredRows?.Count ?? 0;
        }

        private int CountObservedWorkflows()
        {
            if (ActualResponse == null)
            {
                return 0;
            }

            var subExecutionCount = ActualResponse.ExecutionDetails?.SubExecutions?.Count ?? 0;
            if (subExecutionCount > 0)
            {
                return subExecutionCount;
            }

            var subWorkflowSteps = ActualResponse.ExecutionDetails?.Steps.Count(step =>
                step.Action.StartsWith("Sub-Workflow", StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (subWorkflowSteps > 0)
            {
                return subWorkflowSteps;
            }

            return 1;
        }
    }

    /// <summary>
    /// Aggregated results for an assessment run.
    /// </summary>
    public class CopilotAssessmentReport
    {
        public DateTime RunAt { get; set; } = DateTime.UtcNow;
        public int? SessionId { get; set; }
        public string Version { get; set; } = "1.0";
        public List<CopilotAssessmentResult> Results { get; set; } = new();

        public int TotalCases { get; set; }
        public int SuccessCount { get; set; }
        public double SuccessRate => TotalCases > 0 ? (double)SuccessCount / TotalCases : 0;
        public long AverageLatencyMs => Results.Count > 0 ? (long)Results.Average(result => result.LatencyMs) : 0;

        /// <summary>Per-shape (Category) / per-difficulty accuracy breakdown for the run, serialized.
        /// The headline benchmark output — shapes are the distinct Category strings in the loaded gold
        /// (data-driven, never enumerated in code). DB persistence is a follow-up; this is in-memory.</summary>
        public string? ShapeBreakdownJson { get; set; }
    }

    public class CopilotAssessmentRunSummaryDto
    {
        public int SummaryId { get; set; }
        public int? SessionId { get; set; }
        public DateTime RunAt { get; set; }
        public int TotalCases { get; set; }
        public int SuccessCount { get; set; }
        public double SuccessRate { get; set; }
        public long AverageLatencyMs { get; set; }
        public List<CopilotAssessmentCaseResultDto> Results { get; set; } = new();
    }

    public class CopilotAssessmentCaseResultDto
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Difficulty { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string ExpectedBehavior { get; set; } = string.Empty;
        public string ActualMode { get; set; } = string.Empty;
        public string ActualIntent { get; set; } = string.Empty;
        public string ActualTool { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public string AnswerPreview { get; set; } = string.Empty;
        public long LatencyMs { get; set; }
        public bool IsSuccess { get; set; }
        public int? TraceId { get; set; }

        /// <summary>Golden expected SQL from the assessment case (CorrectAnswer.SQL), if defined.</summary>
        public string? ExpectedSql { get; set; }

        /// <summary>SQL the planner actually generated for this run, sourced from CopilotTraceHistories.GeneratedScript.</summary>
        public string? GeneratedSql { get; set; }

        // Historical data properties
        public bool? PreviousResult { get; set; }
        public DateTime? PreviousRunAt { get; set; }
        public string StatusChange { get; set; } = string.Empty; // "Improved", "Regressed", "Same", "New"
        public int TotalRuns { get; set; }
        public double HistoricalSuccessRate { get; set; }
    }

    public class RunCopilotAssessmentRequest
    {
        public List<string>? CaseIds { get; set; }
        public int? SessionId { get; set; }

        /// <summary>Optional: run only cases whose Category matches (case-insensitive). E.g. "Selection".</summary>
        public string? Category { get; set; }

        /// <summary>Optional: run only cases whose Difficulty matches (case-insensitive). E.g. "Easy".</summary>
        public string? Difficulty { get; set; }

        /// <summary>The list of suite filenames the user picked in the multi-select dropdown.
        /// REQUIRED for multi-suite runs because the assessment handler is Scoped — its in-memory
        /// <c>_activeSuiteFiles</c> doesn't survive across requests, so the run endpoint must
        /// re-apply the selection before loading scenarios. Without this, multi-suite runs would
        /// fall back to "newest file in folder" and only ~10 questions would execute.</summary>
        public List<string>? SuiteFiles { get; set; }
    }
}
